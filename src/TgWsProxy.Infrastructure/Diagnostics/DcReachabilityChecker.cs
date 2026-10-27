using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.Diagnostics;

/// <summary>
/// Проверяет достижимость дата-центров Telegram с хоста прокси по четырём маршрутам
/// (прямой WebSocket, прямой TCP, Cloudflare-фронтинг, Cloudflare Worker). Результаты
/// используются панелью для диагностики «какие DC доступны из моего хоста».
/// Цели совпадают с тем, куда реально ходит прокси: <c>kws{dc}.web.telegram.org</c>,
/// <c>Protocol.DcDefaultIps</c> (TCP-fallback и <c>dst</c> воркера), <c>kws{dc}.{домен}</c>
/// и <c>{worker}/apiws?dst=…&amp;dc=…</c>. Проверка повторяет подход upstream
/// <c>ui/connectivity.py</c>: TCP(+TLS) и WS-апгрейд с проверкой ответа «101».
/// </summary>
internal sealed class DcReachabilityChecker(Config cfg) : IDcReachabilityChecker
{
    /// <summary>Проверяемые дата-центры (203 — media-DC, проверяется отдельной строкой).</summary>
    private static readonly int[] TestDcs = [1, 2, 3, 4, 5, 203];

    /// <summary>Таймаут одной попытки соединения (TCP+TLS+WS-апгрейд).</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Сколько доменов встроенного пула пробовать, когда cfProxy.domains пуст.</summary>
    private const int BuiltinCfProxySample = 3;

    public async Task<DcCheckReport> CheckAsync(CancellationToken ct = default)
    {
        var cfProxyDomains = cfg.CfProxy.Domains.Count > 0
            ? cfg.CfProxy.Domains
            : [.. CfDomains.DefaultDomains.Take(BuiltinCfProxySample)];

        var workerDomains = cfg.CfProxy.WorkerDomains;

        // «Прямой TCP» проверяет IP из настройки DC → IP, а если DC там нет — дефолтный IP Telegram.
        var configuredDcIp = cfg.MtProto.DcIp.Count > 0
            ? ConfigDefaults.ParseDcIpList(cfg.MtProto.DcIp)
            : [];

        var directWs = BuildRouteAsync("directWs", "Прямой WebSocket", ProbeDirectWsAsync, null, ct);
        var directTcp = BuildRouteAsync(
            "directTcp",
            "Прямой TCP",
            (dc, c) => ProbeDirectTcpAsync(dc, configuredDcIp, c),
            null,
            ct);
        var cfProxy = BuildRouteAsync(
            "cfProxy",
            "Cloudflare-фронтинг",
            (dc, c) => ProbeAnyHostAsync(dc, [.. cfProxyDomains.Select(d => $"kws{dc}.{d}")], c),
            cfg.CfProxy.Domains.Count == 0 ? "использован встроенный пул (сэмпл из 3)" : null,
            ct);
        var cfWorker = BuildRouteAsync(
            "cfWorker",
            "Cloudflare Worker",
            (dc, c) => ProbeWorkerAsync(dc, workerDomains, c),
            workerDomains.Count == 0 ? "домены не настроены" : null,
            ct);

        await Task.WhenAll(directWs, directTcp, cfProxy, cfWorker).ConfigureAwait(false);

        return new DcCheckReport(
            DateTimeOffset.UtcNow,
            TestDcs,
            [await directWs, await directTcp, await cfProxy, await cfWorker]);
    }

    private static async Task<DcRouteCheck> BuildRouteAsync(
        string route,
        string label,
        Func<int, CancellationToken, Task<DcResult>> probe,
        string? note,
        CancellationToken ct)
    {
        var results = await Task.WhenAll(TestDcs.Select(dc => probe(dc, ct))).ConfigureAwait(false);
        return new DcRouteCheck(route, label, [.. results.OrderBy(static r => r.Dc)], note);
    }

    private async Task<DcResult> ProbeDirectWsAsync(int dc, CancellationToken ct)
    {
        var wsDc = dc == 203 ? 2 : dc;
        var host = $"kws{wsDc}.web.telegram.org";
        var outcome = await ProbeWsAsync(host, host, Protocol.WsPath, ct).ConfigureAwait(false);
        return new DcResult(dc, outcome.Ok, outcome.Ms, outcome.Err, null, null);
    }

    private static async Task<DcResult> ProbeDirectTcpAsync(int dc, IReadOnlyDictionary<int, string> configured, CancellationToken ct)
    {
        var ip = configured.TryGetValue(dc, out var custom) && !string.IsNullOrEmpty(custom)
            ? custom
            : Protocol.DcDefaultIps[dc];
        var outcome = await ProbeTcpAsync(ip, Protocol.WsPort, ct).ConfigureAwait(false);
        return new DcResult(dc, outcome.Ok, outcome.Ms, outcome.Err, null, ip);
    }

    private async Task<DcResult> ProbeWorkerAsync(int dc, IReadOnlyList<string> domains, CancellationToken ct)
    {
        if (domains.Count == 0)
        {
            return new DcResult(dc, false, null, "не настроено", null, null);
        }

        var dst = Protocol.DcDefaultIps[dc];
        var path = $"{Protocol.WsPath}?dst={Uri.EscapeDataString(dst)}&dc={dc}";
        var targets = domains.Select(d => (Host: d, Sni: d, Path: path)).ToList();
        return await ProbeManyAsync(dc, targets, ct).ConfigureAwait(false);
    }

    private async Task<DcResult> ProbeAnyHostAsync(int dc, IReadOnlyList<string> hosts, CancellationToken ct)
    {
        if (hosts.Count == 0)
        {
            return new DcResult(dc, false, null, "не настроено", null, null);
        }

        var targets = hosts.Select(h => (Host: h, Sni: h, Path: Protocol.WsPath)).ToList();
        return await ProbeManyAsync(dc, targets, ct).ConfigureAwait(false);
    }

    private async Task<DcResult> ProbeManyAsync(
        int dc,
        IReadOnlyList<(string Host, string Sni, string Path)> targets,
        CancellationToken ct)
    {
        var outcomes = await Task.WhenAll(targets.Select(t => ProbeWsAsync(t.Host, t.Sni, t.Path, ct))).ConfigureAwait(false);
        var ok = outcomes.FirstOrDefault(static o => o.Ok);
        if (ok.Ok)
        {
            return new DcResult(dc, true, ok.Ms, null, $"ok: {ok.Host}", null);
        }

        var firstErr = outcomes.Select(static o => o.Err).FirstOrDefault(static e => e is not null);
        return new DcResult(dc, false, null, firstErr ?? "недоступен", $"проверено {targets.Count} адрес(ов)", null);
    }

    private async Task<ProbeOutcome> ProbeWsAsync(string host, string sni, string path, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        TcpClient? tcp = null;
        SslStream? ssl = null;
        try
        {
            tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            await tcp.ConnectAsync(host, Protocol.WsPort, cts.Token).ConfigureAwait(false);

            ssl = new SslStream(
                tcp.GetStream(),
                false,
                (_, _, _, errors) => cfg.MtProto.AllowInvalidCertificates || errors == SslPolicyErrors.None);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = sni,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                cts.Token).ConfigureAwait(false);

            var wsKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request = $"GET {path} HTTP/1.1\r\nHost: {sni}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                          $"Sec-WebSocket-Key: {wsKey}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n\r\n";
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token).ConfigureAwait(false);
            await ssl.FlushAsync(cts.Token).ConfigureAwait(false);

            var head = await ReadHeadAsync(ssl, cts.Token).ConfigureAwait(false);
            var statusLine = head.Split("\r\n", 2)[0];
            var ok = statusLine.Contains("101", StringComparison.Ordinal);
            sw.Stop();
            return new ProbeOutcome(ok, (int)sw.ElapsedMilliseconds, ok ? null : statusLine, host);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeOutcome(false, null, ShortError(ex), host);
        }
        finally
        {
            ssl?.Dispose();
            tcp?.Dispose();
        }
    }

    private static async Task<ProbeOutcome> ProbeTcpAsync(string host, int port, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var tcp = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return new ProbeOutcome(true, (int)sw.ElapsedMilliseconds, null, host);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeOutcome(false, null, ShortError(ex), host);
        }
    }

    private static async Task<string> ReadHeadAsync(SslStream ssl, CancellationToken ct)
    {
        var buffer = new byte[512];
        var sb = new StringBuilder();
        while (sb.Length <= 16 * 1024)
        {
            var read = await ssl.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return sb.ToString();
    }

    private static string ShortError(Exception ex)
    {
        if (ex is OperationCanceledException or TimeoutException)
        {
            return "timeout";
        }

        // TLS-ошибки часто оборачивают IOException («unexpected EOF» / «0 bytes from the transport»).
        var inner = ex;
        while (inner.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        var message = inner is SocketException se ? se.SocketErrorCode.ToString() : inner.Message;
        return message.Length > 80 ? message[..80] : message;
    }

    private readonly record struct ProbeOutcome(bool Ok, int? Ms, string? Err, string Host);
}
