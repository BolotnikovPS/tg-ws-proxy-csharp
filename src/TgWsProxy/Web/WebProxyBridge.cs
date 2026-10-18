using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Web;

/// <summary>
/// Bridge-листенер WEB proxy (browser-relay): динамическая bridge-страница на <c>GET /?bridge=…</c>
/// и HTTPS long-poll носитель на <c>{base}api/v1/{session,up,down}</c> (SPEC §1/§2). Делит один
/// Kestrel-хост с панелью, но слушает отдельный порт <see cref="Config.WebProxyPort"/>, поэтому
/// маршруты различаются по <see cref="ConnectionInfo.LocalPort"/>. Регистрируется до статических
/// файлов панели, чтобы перехватывать корень bridge-порта.
/// </summary>
internal static class WebProxyBridge
{
    /// <summary>Предел тела <c>POST /session</c> — единственный HELLO-кадр (8 + 1 байт).</summary>
    private const int MaxCreateBodyBytes = 64;

    /// <summary>Предел тела <c>POST /up</c> (2 МиБ).</summary>
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    /// <summary>Предел нисходящего батча и <c>batchLimit</c> bridge-страницы (2 МиБ).</summary>
    private const int CarrierBatchBytes = 2 * 1024 * 1024;

    private static readonly TimeSpan LongPollTimeout = TimeSpan.FromSeconds(25);

    private const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), clipboard-read=(), clipboard-write=(), display-capture=(), encrypted-media=(), fullscreen=(), geolocation=(), gyroscope=(), hid=(), idle-detection=(), magnetometer=(), microphone=(), midi=(), payment=(), picture-in-picture=(), publickey-credentials-create=(), publickey-credentials-get=(), screen-wake-lock=(), serial=(), usb=(), web-share=(), xr-spatial-tracking=()";

    /// <summary>Регистрирует bridge-конвейер: carrier-API и маршруты bridge-сайта.</summary>
    public static void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var cfg = context.RequestServices.GetRequiredService<Config>();
            if (!cfg.WebProxyEnabled || context.Connection.LocalPort != cfg.WebProxyPort)
            {
                await next();
                return;
            }

            var basePath = BridgeCapabilityVerifier.NormalizeBasePath(cfg.WebProxyBasePath);
            var basePrefix = Base(basePath);
            var rawPath = context.Request.Path.Value ?? "/";

            if (rawPath.StartsWith(basePrefix, StringComparison.Ordinal))
            {
                switch (rawPath[basePrefix.Length..])
                {
                    case "api/v1/session":
                    case "api/v1/up":
                    case "api/v1/down":
                        await HandleApiAsync(context, cfg, basePath, rawPath[basePrefix.Length..]);
                        return;
                }
            }

            if (IsBridgeRoot(rawPath, basePath))
            {
                await ServeBridgeAsync(context, cfg, basePath);
                return;
            }

            // Bridge-порт обслуживает только bridge-маршруты; панель сюда не просачивается.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
    }

    /// <summary>Префикс carrier-эндпоинтов: <c>/</c> либо <c>/slug/</c> (всегда с хвостовым слэшем).</summary>
    private static string Base(string basePath) => basePath.Length == 0 ? "/" : "/" + basePath + "/";

    private static bool IsBridgeRoot(string rawPath, string basePath)
    {
        if (basePath.Length == 0)
        {
            return rawPath == "/";
        }

        return rawPath == "/" + basePath || rawPath == "/" + basePath + "/";
    }

    // ---------------------------------------------------------------------
    // Bridge-страница
    // ---------------------------------------------------------------------

    private static async Task ServeBridgeAsync(HttpContext context, Config cfg, string basePath)
    {
        var logger = GetLogger(context);
        try
        {
            var request = context.Request;
            if (!HttpMethods.IsGet(request.Method))
            {
                NotFound(context);
                return;
            }

            // Точно один параметр query: bridge=<43-char capability>.
            var query = request.Query;
            if (query.Count != 1 || !query.TryGetValue("bridge", out var capabilityValues)
                || capabilityValues.Count != 1)
            {
                NotFound(context);
                return;
            }

            var capability = capabilityValues[0]!;
            if (capability.Length != 43)
            {
                NotFound(context);
                return;
            }

            var relay = context.RequestServices.GetRequiredService<IWebProxyRelaySession>();
            var host = request.Host.Host;
            var token = relay.ResolveToken(host, capability);
            if (token is null)
            {
                NotFound(context);
                return;
            }

            var manager = context.RequestServices.GetRequiredService<WebProxySessionManager>();
            var bootstrap = manager.IssueBootstrap(token);

            var publicHost = WebProxyLinkBuilder.ResolveHost(cfg) ?? host;
            var relayBase = "https://" + publicHost + Base(basePath);
            var nonce = GenerateNonce();

            var html = BridgePageTemplate
                .Replace("__NONCE__", nonce, StringComparison.Ordinal)
                .Replace("__RELAY_BASE__", JsonEncode(relayBase), StringComparison.Ordinal)
                .Replace("__BOOTSTRAP__", JsonEncode(bootstrap), StringComparison.Ordinal)
                .Replace("__CARRIER_MODE__", JsonEncode("https"), StringComparison.Ordinal)
                .Replace("__BATCH_LIMIT__", CarrierBatchBytes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers.ContentSecurityPolicy = BuildCsp(nonce, publicHost);
            response.Headers.CacheControl = "no-store";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-DNS-Prefetch-Control"] = "off";
            response.Headers["Permissions-Policy"] = PermissionsPolicy;

            var body = Encoding.UTF8.GetBytes(html);
            response.ContentLength = body.Length;
            await response.Body.WriteAsync(body, context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "web-proxy bridge page failed");
            context.Abort();
        }
    }

    // ---------------------------------------------------------------------
    // Carrier API
    // ---------------------------------------------------------------------

    private static async Task HandleApiAsync(HttpContext context, Config cfg, string basePath, string suffix)
    {
        var logger = GetLogger(context);
        try
        {
            var request = context.Request;

            // Carrier-запросы без query и с единственным Authorization-заголовком.
            if (request.QueryString.HasValue || request.Headers.Authorization.Count > 1)
            {
                NotFound(context);
                return;
            }

            if (!TryParseBearer(request.Headers.Authorization.ToString(), out var token))
            {
                NotFound(context);
                return;
            }

            switch (suffix)
            {
                case "api/v1/session":
                    await HandleSessionAsync(context, token);
                    break;
                case "api/v1/up":
                    await HandleUpAsync(context, token);
                    break;
                case "api/v1/down":
                    await HandleDownAsync(context, token);
                    break;
                default:
                    NotFound(context);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "web-proxy carrier request failed");
            context.Abort();
        }
    }

    private static async Task HandleSessionAsync(HttpContext context, string token)
    {
        var manager = context.RequestServices.GetRequiredService<WebProxySessionManager>();
        var request = context.Request;
        var ct = context.RequestAborted;

        if (HttpMethods.IsDelete(request.Method))
        {
            if (!await IsEmptyBodyAsync(request, ct) || request.ContentType is not null)
            {
                NotFound(context);
                return;
            }

            if (!manager.CloseSession(token))
            {
                NotFound(context);
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (!HttpMethods.IsPost(request.Method) || !IsBinaryContentType(request.ContentType))
        {
            NotFound(context);
            return;
        }

        var body = await ReadBodyAsync(request, MaxCreateBodyBytes, ct);
        if (body is null)
        {
            NotFound(context);
            return;
        }

        var peer = ResolvePeer(context);
        var result = manager.CreateSession(token, body, peer, "web-proxy|" + context.Connection.Id);

        switch (result.Status)
        {
            case WebProxyCreateStatus.Ok:
                var response = context.Response;
                response.Headers.CacheControl = "no-store";
                response.ContentType = "application/octet-stream";
                response.Headers["X-Session-Token"] = result.SessionToken;
                response.Headers["X-Carrier-Mode"] = "https";
                response.Headers["X-Down-Cursor"] = "0";
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentLength = result.Welcome!.Length;
                await response.Body.WriteAsync(result.Welcome, ct);
                return;

            case WebProxyCreateStatus.Busy:
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.RetryAfter = "1";
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;

            default:
                NotFound(context);
                return;
        }
    }

    private static async Task HandleUpAsync(HttpContext context, string token)
    {
        var manager = context.RequestServices.GetRequiredService<WebProxySessionManager>();
        var request = context.Request;
        var ct = context.RequestAborted;

        if (!HttpMethods.IsPost(request.Method) || !IsBinaryContentType(request.ContentType))
        {
            NotFound(context);
            return;
        }

        if (!TryParseCanonicalUint(request.Headers["X-Up-Seq"].ToString(), out var sequence)
            || sequence == 0)
        {
            NotFound(context);
            return;
        }

        var session = manager.GetSession(token);
        if (session is null)
        {
            NotFound(context);
            return;
        }

        var body = await ReadBodyAsync(request, MaxBodyBytes, ct);
        if (body is null)
        {
            NotFound(context);
            return;
        }

        var result = await session.ProcessUpAsync(sequence, body, ct);
        if (!result.Accepted)
        {
            NotFound(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Up-Ack"] = result.Ack.ToString(CultureInfo.InvariantCulture);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task HandleDownAsync(HttpContext context, string token)
    {
        var manager = context.RequestServices.GetRequiredService<WebProxySessionManager>();
        var request = context.Request;
        var ct = context.RequestAborted;

        if (!HttpMethods.IsPost(request.Method) || request.ContentType is not null)
        {
            NotFound(context);
            return;
        }

        if (!TryParseCanonicalUint(request.Headers["X-Down-Cursor"].ToString(), out var cursor))
        {
            NotFound(context);
            return;
        }

        var session = manager.GetSession(token);
        if (session is null)
        {
            NotFound(context);
            return;
        }

        if (!await IsEmptyBodyAsync(request, ct))
        {
            NotFound(context);
            return;
        }

        // Long-poll: ждём данных, закрытия сессии либо 25-секундного таймаута.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LongPollTimeout);

        WebProxyDownlink downlink;
        try
        {
            downlink = await session.PollAsync(cursor, cts.Token);
        }
        catch (InvalidDataException)
        {
            NotFound(context);
            return;
        }

        if (ct.IsCancellationRequested)
        {
            // Клиент оборвал соединение — писать некуда.
            return;
        }

        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Down-Cursor"] = downlink.Cursor.ToString(CultureInfo.InvariantCulture);

        if (downlink.Closed || downlink.Batch is null)
        {
            response.StatusCode = downlink.Closed
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status204NoContent;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/octet-stream";
        response.ContentLength = downlink.Batch.Length;
        await response.Body.WriteAsync(downlink.Batch, ct);
    }

    // ---------------------------------------------------------------------
    // Вспомогательные
    // ---------------------------------------------------------------------

    private static ILogger GetLogger(HttpContext context)
        => context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("web-proxy");

    private static void NotFound(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static bool IsBinaryContentType(string? contentType)
        => string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);

    /// <summary>Читает тело с пределом; <see langword="null"/> — пусто либо больше лимита.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            stream.Write(buffer, 0, read);
        }

        return total == 0 ? null : stream.ToArray();
    }

    private static async Task<bool> IsEmptyBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength == 0)
        {
            return true;
        }

        if (request.ContentLength is null)
        {
            var single = new byte[1];
            var read = await request.Body.ReadAsync(single, ct);
            return read == 0;
        }

        return false;
    }

    /// <summary>Bearer-токен: 43-char канонический base64url, декодируется ровно в 32 байта.</summary>
    private static bool TryParseBearer(string? authorization, out string token)
    {
        token = "";
        if (string.IsNullOrEmpty(authorization))
        {
            return false;
        }

        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = authorization[prefix.Length..];
        if (candidate.Length == 0 || candidate.Contains(' '))
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = System.Buffers.Text.Base64Url.DecodeFromChars(candidate);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length != 32 || System.Buffers.Text.Base64Url.EncodeToString(decoded) != candidate)
        {
            return false;
        }

        token = candidate;
        return true;
    }

    /// <summary>Каноническое десятичное без знака: без ведущих нулей и <c>+</c>, без пробелов.</summary>
    private static bool TryParseCanonicalUint(string? value, out long result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.Length > 1 && value[0] == '0')
        {
            return false;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result))
        {
            return false;
        }

        return result >= 0 && result.ToString(CultureInfo.InvariantCulture) == value;
    }

    private static IPAddress? ResolvePeer(HttpContext context)
    {
        // При наличии обратного прокси берём единственный адрес из X-Forwarded-For,
        // иначе — непосредственный peer соединения.
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(forwarded) && !forwarded.Contains(','))
        {
            return IPAddress.TryParse(forwarded.Trim(), out var parsed) ? parsed : null;
        }

        return context.Connection.RemoteIpAddress;
    }

    private static string GenerateNonce()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return BridgeCapabilityVerifier.Base64Url.Encode(bytes);
    }

    private static string BuildCsp(string nonce, string host)
        => "default-src 'none'; base-uri 'none'; child-src 'none'; connect-src 'self' wss://" + host
            + "; font-src 'none'; form-action 'none'; frame-ancestors http://127.0.0.1:*; frame-src 'none'; img-src 'none'; manifest-src 'none'; media-src 'none'; object-src 'none'; script-src 'nonce-"
            + nonce + "'; style-src 'none'; worker-src 'none'; sandbox allow-same-origin allow-scripts";

    private static string JsonEncode(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    // ---------------------------------------------------------------------
    // Bridge-страница (эталонный JS-носитель tproxy-server, carrier_mode = https)
    // ---------------------------------------------------------------------

    private const string BridgePageTemplate = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Connection</title>
</head>
<body>
<script nonce="__NONCE__">
(()=>{
'use strict';
const relayBase=__RELAY_BASE__,bootstrap=__BOOTSTRAP__,carrierMode=__CARRIER_MODE__;
const fragment=location.hash,androidNonce=/^#android=([A-Za-z0-9_-]{43})$/.exec(fragment)?.[1]||'';
history.replaceState(null,'',location.pathname);
let initialized=false,closed=false,port=null,sessionToken='',createStarted=false;
let queuedBytes=0,queuedItems=0,pollController=null,webSocket=null,webSocketTimer=0;
const pending=[],upPending=[],lanes=new Map(),closedLanes=new Set(),closedLaneOrder=[];
const queueLimit=33554432,queueItemLimit=16384,closedLaneLimit=4096;
const laneQueueLimit=8388608,laneItemLimit=1024,batchLimit=__BATCH_LIMIT__;
let upSequence=1,downCursor='0',upRunning=false;
const status=state=>{if(port&&!closed)port.postMessage({t:'status',state})};
const pause=milliseconds=>new Promise(resolve=>setTimeout(resolve,milliseconds));
const options=(method,token,body,headers,signal,keepalive)=>({
 method,body,signal,keepalive:!!keepalive,mode:'same-origin',credentials:'omit',cache:'no-store',redirect:'error',referrerPolicy:'no-referrer',
 headers:Object.assign(token?{Authorization:'Bearer '+token}:{},body?{'Content-Type':'application/octet-stream'}:{},headers||{})
});
const bufferedBytes=()=>{
 let total=webSocket&&webSocket.readyState===WebSocket.OPEN?webSocket.bufferedAmount:0;
 if(carrierMode==='websocket-lanes')for(const lane of lanes.values())if(lane.socket&&lane.socket.readyState===WebSocket.OPEN)total+=lane.socket.bufferedAmount;
 return total;
};
function reserve(data,lane){
 if(!data.byteLength||queuedBytes+bufferedBytes()>queueLimit-data.byteLength||queuedItems>=queueItemLimit)return false;
 if(lane&&(lane.bytes>laneQueueLimit-data.byteLength||lane.items>=laneItemLimit))return false;
 queuedBytes+=data.byteLength;queuedItems++;
 if(lane){lane.bytes+=data.byteLength;lane.items++}
 return true;
}
function release(bytes,items,lane){
 queuedBytes-=bytes;queuedItems-=items;
 if(lane){lane.bytes-=bytes;lane.items-=items}
}
function splitFrames(value){
 const view=new DataView(value),result=[];let offset=0;
 while(offset<value.byteLength){
  if(value.byteLength-offset<8||result.length>=4096)throw new Error('invalid frame batch');
  const type=view.getUint8(offset),id=(view.getUint8(offset+1)<<16)|(view.getUint8(offset+2)<<8)|view.getUint8(offset+3);
  const size=view.getUint32(offset+4),end=offset+8+size;
  if((type===2&&!size)||size>1048576||end>value.byteLength)throw new Error('invalid frame');
  result.push({type,id,data:offset===0&&end===value.byteLength?value:value.slice(offset,end)});offset=end;
 }
 if(!result.length)throw new Error('empty frame batch');
 return result;
}
function frameBound(value,maxFrames,maxBytes){
 const view=new DataView(value);let offset=0,frames=0;
 while(offset<value.byteLength){
  if(value.byteLength-offset<8)throw new Error('invalid frame batch');
  const size=view.getUint32(offset+4),end=offset+8+size;
  if(end>value.byteLength)throw new Error('invalid frame');
  if(frames>0&&(frames>=maxFrames||end>maxBytes))break;
  frames++;offset=end;
 }
 return {frames,bytes:offset};
}
function joinPending(values,lane){
 let total=0,count=0,frames=0;
 while(count<values.length){
  const bound=frameBound(values[count],4096,batchLimit);
  const whole=bound.bytes===values[count].byteLength;
  if(count===0&&!whole){
   const head=new Uint8Array(values[0],0,bound.bytes).slice();
   values[0]=values[0].slice(bound.bytes);
   queuedItems++;if(lane)lane.items++;
   return {body:head.buffer,total:bound.bytes,count:1};
  }
  if(count!==0&&(total+values[count].byteLength>batchLimit||frames+bound.frames>4096))break;
  total+=values[count].byteLength;frames+=bound.frames;count++;
 }
 const joined=new Uint8Array(total);let offset=0;
 for(const data of values.splice(0,count)){joined.set(new Uint8Array(data),offset);offset+=data.byteLength}
 return {body:joined.buffer,total,count};
}
function retryAfterMs(response){
 const header=response.headers.get('Retry-After');
 if(!header)return 0;
 const seconds=Number(header);
 if(Number.isFinite(seconds)&&seconds>=0)return Math.min(seconds*1000,30000);
 const when=Date.parse(header);
 if(Number.isFinite(when)){const delta=when-Date.now();return delta>0?Math.min(delta,30000):0}
 return 0;
}
async function request(path,makeOptions){
 let delay=250,attempt=0;const deadline=Date.now()+90000;
 while(true){
  const requestOptions=makeOptions(),controller=new AbortController();
  const external=requestOptions.signal,abort=()=>controller.abort();
  if(external)external.addEventListener('abort',abort,{once:true});
  requestOptions.signal=controller.signal;
  const timer=setTimeout(abort,90000);
  let wait=0,serviceUnavailable=false;
  try{
   const response=await fetch(relayBase+path,requestOptions);
   if(response.status!==503)return response;
   serviceUnavailable=true;wait=retryAfterMs(response);
   await response.arrayBuffer();
  }catch(error){if(closed||(external&&external.aborted))throw error;attempt++;if(attempt===9)throw new Error('carrier retry limit reached')}
  finally{clearTimeout(timer);if(external)external.removeEventListener('abort',abort)}
  if(serviceUnavailable&&Date.now()>=deadline)throw new Error('carrier retry limit reached');
  status('reconnecting');
  const backoff=wait||(delay+Math.floor(Math.random()*Math.max(1,delay/4)));
  await pause(backoff);
  if(!serviceUnavailable)delay=Math.min(delay*2,5000);
 }
}
function fail(){
 if(closed)return;
 status('failed');
 if(port)port.postMessage({t:'close'});
 close(true);
}
async function createSession(first){
 try{
  status('connecting');
  const response=await request('api/v1/session',()=>options('POST',bootstrap,first));
  if(response.status!==200||response.headers.get('X-Carrier-Mode')!==carrierMode)throw new Error('session creation rejected');
  sessionToken=response.headers.get('X-Session-Token')||'';
  downCursor=response.headers.get('X-Down-Cursor')||'0';
  if(!sessionToken)throw new Error('missing session token');
  if(closed){fetch(relayBase+'api/v1/session',options('DELETE',sessionToken,null,null,undefined,true)).catch(()=>{});return}
  const welcome=await response.arrayBuffer();
  if(carrierMode==='websocket')await openWebSocket();
  if(closed){fetch(relayBase+'api/v1/session',options('DELETE',sessionToken,null,null,undefined,true)).catch(()=>{});return}
  port.postMessage(welcome,[welcome]);
  status('connected');
  for(const data of pending.splice(0)){release(data.byteLength,1,null);queueCarrier(data)}
  if(carrierMode==='https')poll();
  else if(carrierMode==='https-lanes')pollLane(ensureLane(0));
 }catch(error){fail()}
}
function queueCarrier(data){
 try{
  if(carrierMode==='https')queueUp(data);
  else if(carrierMode==='https-lanes')for(const value of splitFrames(data))queueLane(value);
  else if(carrierMode==='websocket')queueWebSocket(data);
  else for(const value of splitFrames(data))queueWebSocketLane(value);
 }catch(error){fail()}
}
function queueUp(data){
 if(!reserve(data,null)){fail();return}
 upPending.push(data);runUp();
}
async function runUp(){
 if(upRunning)return;
 upRunning=true;
 try{
  while(!closed&&sessionToken&&upPending.length){
   const batch=joinPending(upPending,null),sequence=String(upSequence);
   const response=await request('api/v1/up',()=>options('POST',sessionToken,batch.body,{'X-Up-Seq':sequence}));
   if(response.status!==204||response.headers.get('X-Up-Ack')!==sequence)throw new Error('uplink rejected');
   release(batch.total,batch.count,null);port.postMessage({t:'traffic',up:batch.total,down:0});upSequence++;
  }
 }catch(error){fail()}
 finally{upRunning=false;if(!closed&&sessionToken&&upPending.length)runUp()}
}
async function poll(){
 while(!closed&&sessionToken){
  try{
   pollController=new AbortController();
   const response=await request('api/v1/down',()=>options('POST',sessionToken,null,{'X-Down-Cursor':downCursor},pollController.signal));
   if(response.status===204){status('connected');continue}
   if(response.status!==200)throw new Error('downlink rejected');
   const next=response.headers.get('X-Down-Cursor')||'',data=await response.arrayBuffer();
   if(!next||!data.byteLength)throw new Error('invalid downlink response');
   if(closed)return;
   port.postMessage({t:'traffic',up:0,down:data.byteLength});port.postMessage(data,[data]);downCursor=next;status('connected');
  }catch(error){if(!closed)fail();return}
 }
}
function ensureLane(id){
 let lane=lanes.get(id);
 if(!lane){lane={id,sequence:1,cursor:'0',pending:[],bytes:0,items:0,running:false,polling:false,socket:null,timer:0,opened:false,localClosed:false,remoteClosed:false,finished:false};lanes.set(id,lane)}
 return lane;
}
function rememberLaneClosed(id){
 if(!id||closedLanes.has(id))return;
 if(closedLaneOrder.length===closedLaneLimit)closedLanes.delete(closedLaneOrder.shift());
 closedLanes.add(id);closedLaneOrder.push(id);
}
function queueLane(value){
 let lane=lanes.get(value.id);
 if(!lane&&(value.type===2||value.type===3||value.type===4))return;
 if(!lane&&closedLanes.has(value.id))throw new Error('closed lane was reused');
 if(!lane&&value.type!==1)throw new Error('lane did not begin with OPEN');
 lane=lane||ensureLane(value.id);
 if(!reserve(value.data,lane)){fail();return}
 lane.pending.push(value.data);runLaneUp(lane);
}
async function runLaneUp(lane){
 if(lane.running)return;
 lane.running=true;
 try{
  while(!closed&&sessionToken&&lane.pending.length){
   const batch=joinPending(lane.pending,lane),sequence=String(lane.sequence),laneID=String(lane.id);
   const response=await request('api/v1/up',()=>options('POST',sessionToken,batch.body,{'X-Up-Seq':sequence,'X-Lane-ID':laneID}));
   if(response.status!==204||response.headers.get('X-Up-Ack')!==sequence)throw new Error('lane uplink rejected');
   release(batch.total,batch.count,lane);port.postMessage({t:'traffic',up:batch.total,down:0});lane.sequence++;
   if(!lane.polling)pollLane(lane);
  }
 }catch(error){fail()}
 finally{lane.running=false;if(!closed&&sessionToken&&lane.pending.length)runLaneUp(lane)}
}
async function pollLane(lane){
 if(lane.polling)return;
 lane.polling=true;
 try{
  while(!closed&&sessionToken&&lanes.get(lane.id)===lane){
   const controller=new AbortController(),laneID=String(lane.id);
   lane.controller=controller;
   const response=await request('api/v1/down',()=>options('POST',sessionToken,null,{'X-Down-Cursor':lane.cursor,'X-Lane-ID':laneID},controller.signal));
   if(response.status===204){
    if(response.headers.get('X-Lane-Closed')==='1'){lanes.delete(lane.id);rememberLaneClosed(lane.id);return}
    status('connected');continue;
   }
   if(response.status!==200)throw new Error('lane downlink rejected');
   const next=response.headers.get('X-Down-Cursor')||'',data=await response.arrayBuffer();
   if(!next||!data.byteLength)throw new Error('invalid lane downlink response');
   for(const value of splitFrames(data))if(value.id!==lane.id)throw new Error('cross-lane frame');
   if(closed)return;
   port.postMessage({t:'traffic',up:0,down:data.byteLength});port.postMessage(data,[data]);lane.cursor=next;status('connected');
  }
 }catch(error){if(!closed)fail()}
 finally{lane.polling=false;lane.controller=null}
}
function openWebSocket(){
 return new Promise((resolve,reject)=>{
  const target=relayBase.replace(/^https:/,'wss:')+'api/v1/ws',socket=new WebSocket(target,'tproxy-v1.'+sessionToken);
  webSocket=socket;socket.binaryType='arraybuffer';
  socket.onopen=()=>resolve();
  socket.onmessage=event=>{
   if(!(event.data instanceof ArrayBuffer)||!event.data.byteLength){fail();return}
   port.postMessage({t:'traffic',up:0,down:event.data.byteLength});port.postMessage(event.data,[event.data]);status('connected');
  };
  socket.onerror=()=>reject(new Error('websocket failed'));
  socket.onclose=()=>{if(!closed)fail()};
 });
}
function queueWebSocket(data){
 if(!reserve(data,null)){fail();return}
 upPending.push(data);runWebSocketUp();
}
function runWebSocketUp(){
 if(closed||!webSocket||webSocket.readyState!==WebSocket.OPEN||!upPending.length)return;
 if(webSocket.bufferedAmount+queuedBytes>queueLimit||webSocket.bufferedAmount>=batchLimit){
  if(!webSocketTimer)webSocketTimer=setTimeout(()=>{webSocketTimer=0;runWebSocketUp()},10);
  return;
 }
 try{
  const batch=joinPending(upPending,null);webSocket.send(batch.body);release(batch.total,batch.count,null);
  port.postMessage({t:'traffic',up:batch.total,down:0});
  if(upPending.length)queueMicrotask(runWebSocketUp);
 }catch(error){fail()}
}
function closeFrame(id){
 const data=new ArrayBuffer(8),view=new DataView(data);
 view.setUint8(0,3);view.setUint8(1,id>>>16);view.setUint8(2,id>>>8);view.setUint8(3,id);view.setUint32(4,0);
 return data;
}
function finishWebSocketLane(lane,notify){
 if(lane.finished||lanes.get(lane.id)!==lane)return;
 lane.finished=true;if(lane.timer)clearTimeout(lane.timer);
 if(lane.bytes||lane.items)release(lane.bytes,lane.items,lane);
 lane.pending.length=0;lanes.delete(lane.id);rememberLaneClosed(lane.id);
 if(lane.socket&&(lane.socket.readyState===WebSocket.OPEN||lane.socket.readyState===WebSocket.CONNECTING))try{lane.socket.close()}catch(error){}
 if(notify&&port&&!closed){const frame=closeFrame(lane.id);port.postMessage(frame,[frame])}
}
function openWebSocketLane(lane){
 const target=relayBase.replace(/^https:/,'wss:')+'api/v1/ws';
 const socket=new WebSocket(target,'tproxy-lane-v1.'+sessionToken+'.'+lane.id);
 lane.socket=socket;socket.binaryType='arraybuffer';
 socket.onopen=()=>{if(closed||lane.finished){socket.close();return}lane.opened=true;status('connected');runWebSocketLaneUp(lane)};
 socket.onmessage=event=>{
  if(!(event.data instanceof ArrayBuffer)||!event.data.byteLength){fail();return}
  let values;try{values=splitFrames(event.data)}catch(error){fail();return}
  if(values.some(value=>value.id!==lane.id)){fail();return}
  if(values.some(value=>value.type===3))lane.remoteClosed=true;
  port.postMessage({t:'traffic',up:0,down:event.data.byteLength});port.postMessage(event.data,[event.data]);status('connected');
 };
 socket.onerror=()=>{};
 socket.onclose=()=>{
  if(closed||lane.finished)return;
  if(!lane.opened){fail();return}
  finishWebSocketLane(lane,!lane.localClosed&&!lane.remoteClosed);
 };
}
function queueWebSocketLane(value){
 let lane=lanes.get(value.id);
 if(!lane&&(value.type===2||value.type===3||value.type===4))return;
 if(!value.id||(!lane&&closedLanes.has(value.id)))throw new Error('closed lane was reused');
 if(!lane&&value.type!==1)throw new Error('lane did not begin with OPEN');
 lane=lane||ensureLane(value.id);
 if(value.type===3&&!lane.opened){finishWebSocketLane(lane,false);return}
 if(!reserve(value.data,lane)){fail();return}
 lane.pending.push(value.data);if(value.type===3)lane.localClosed=true;
 if(!lane.socket)openWebSocketLane(lane);else runWebSocketLaneUp(lane);
}
function runWebSocketLaneUp(lane){
 const socket=lane.socket;
 if(closed||lane.finished||!socket||socket.readyState!==WebSocket.OPEN||!lane.pending.length)return;
 if(socket.bufferedAmount>=batchLimit){
  if(!lane.timer)lane.timer=setTimeout(()=>{lane.timer=0;runWebSocketLaneUp(lane)},10);
  return;
 }
 try{
  const batch=joinPending(lane.pending,lane);socket.send(batch.body);release(batch.total,batch.count,lane);
  port.postMessage({t:'traffic',up:batch.total,down:0});
  if(lane.pending.length)queueMicrotask(()=>runWebSocketLaneUp(lane));
 }catch(error){fail()}
}
function close(notifyServer){
 if(closed)return;
 closed=true;
 if(pollController)pollController.abort();
 for(const lane of lanes.values()){
  if(lane.controller)lane.controller.abort();
  if(lane.timer)clearTimeout(lane.timer);
  if(lane.socket)try{lane.socket.close()}catch(error){}
 }
 if(webSocketTimer)clearTimeout(webSocketTimer);
 if(webSocket)webSocket.close();
 if(notifyServer&&sessionToken)fetch(relayBase+'api/v1/session',options('DELETE',sessionToken,null,null,undefined,true)).catch(()=>{});
 if(port)port.close();
}
function activatePort(nextPort){
 initialized=true;port=nextPort;
 port.onmessage=message=>{
  if(message.data instanceof ArrayBuffer){
   if(!createStarted){createStarted=true;createSession(message.data)}
   else if(!sessionToken){if(!reserve(message.data,null)){fail();return}pending.push(message.data)}
   else queueCarrier(message.data);
  }else if(message.data&&message.data.t==='close')close(true);
 };
 port.start();status('connecting');
}
addEventListener('message',event=>{
 if(initialized||event.source!==parent||event.data===null||typeof event.data!=='object')return;
 const keys=Object.keys(event.data).sort();
 if(keys.length!==2||keys[0]!=='t'||keys[1]!=='v'||event.data.t!=='tproxy-init'||event.data.v!==1||event.ports.length!==1)return;
 let source;try{source=new URL(event.origin)}catch(error){return}
 if(source.protocol!=='http:'||source.hostname!=='127.0.0.1'||!source.port||source.origin!==event.origin)return;
 activatePort(event.ports[0]);
},{once:false});
const androidBridge=globalThis.TelegramWebProxy;
if(!initialized&&androidNonce&&androidBridge&&typeof androidBridge.postMessage==='function'){
 const androidPort={onmessage:null,start(){},close(){androidBridge.onmessage=null},postMessage(value){
  if(value instanceof ArrayBuffer){
   let frames;try{frames=splitFrames(value)}catch(error){fail();return}
   for(const frame of frames)androidBridge.postMessage(frame.data);
  }else androidBridge.postMessage(JSON.stringify(value));
 }};
 androidBridge.onmessage=event=>{
  let data=event.data;if(typeof data==='string'){try{data=JSON.parse(data)}catch(error){return}}
  if(androidPort.onmessage)androidPort.onmessage({data});
 };
 activatePort(androidPort);androidBridge.postMessage(JSON.stringify({t:'tproxy-android-init',v:1,nonce:androidNonce}));
}
addEventListener('pagehide',()=>close(true),{once:true});
})();
</script>
</body>
</html>
""";
}
