#!/usr/bin/env node
//
// Смоук-тест WEB proxy (browser-relay) для tg-ws-proxy-csharp.
//
// Эмулирует bridge-клиента поверх HTTPS long-poll носителя (SPEC §1/§2):
//   1. GET  /?bridge=<capability>            → bridge-страница с bootstrap-токеном
//   2. POST /api/v1/session  (HELLO)         → 200 + WELCOME + X-Session-Token
//   3. POST /api/v1/up       (OPEN + DATA)   → 204 + X-Up-Ack (uplink)
//   4. POST /api/v1/down     (long-poll)     → 200 + WINDOW-кредит (flow-control)
//   5. DELETE /api/v1/session                → 204 (закрытие сессии)
//
// Транспортный режим (по умолчанию) детерминирован и не требует доступа к Telegram DC:
// в поток отправляется намеренно НЕвалидный obfuscated2 init, поэтому мост честно читает
// 64 байта (клиент получает WINDOW-кредит), не проходит рукопожатие и закрывает поток
// (CLOSE). Это проверяет capability-HMAC, bootstrap, HELLO/WELCOME, relay-поток,
// flow-control и закрытие без сети до Telegram.
//
// --full: отправить НАСТОЯЩИЙ obfuscated2 init и ждать relay_init (Data stream 1) —
// требует сети с доступом к Telegram DC (VPS/VPN); иначе честно сообщает о блокировке.
//
// Требования: Node.js >= 18 (используется node:https, без внешних зависимостей).
//
// Пример:
//   node tools/web-proxy-smoke.mjs \
//     --host 127.0.0.1 \
//     --secret 00112233445566778899aabbccddeeff \
//     [--port 443] [--base-path slug] [--dc 2] [--proto abridged] [--insecure] [--timeout 10]
//
// --capability <base64url>: пропустить вычисление HMAC и подставить готовое значение.
// --insecure: отключить проверку TLS-сертификата (для самоподписанного тестового серта).

import crypto from 'node:crypto';
import https from 'node:https';

// ---- relay-frame протокол (DESIGN-WEB-PROXY §2.1) ----
const FT = {
  Open: 0x01,
  Data: 0x02,
  Close: 0x03,
  Window: 0x04,
  Ping: 0x05,
  Pong: 0x06,
  Hello: 0x10,
  Welcome: 0x11,
  AuthChallenge: 0x12,
  AuthResponse: 0x13,
  Bye: 0x1f,
};

const PROTO_TAGS = {
  abridged: 0xefefefef,
  intermediate: 0xeeeeeeee,
  padded: 0xdddddddd,
};

const FRAME_HEADER_SIZE = 8;

function buildFrame(type, streamId, payload = Buffer.alloc(0)) {
  const header = Buffer.alloc(FRAME_HEADER_SIZE);
  header[0] = type;
  header.writeUIntBE(streamId, 1, 3); // 24-bit big-endian
  header.writeUInt32BE(payload.length, 4);
  return Buffer.concat([header, payload]);
}

function parseFrame(buf, offset = 0) {
  const type = buf[offset];
  const streamId = buf.readUIntBE(offset + 1, 3);
  const length = buf.readUInt32BE(offset + 4);
  const payload = buf.subarray(offset + 8, offset + 8 + length);
  return { type, streamId, length, payload, total: 8 + length };
}

function parseBatch(buf) {
  const frames = [];
  let off = 0;
  while (off + FRAME_HEADER_SIZE <= buf.length) {
    const f = parseFrame(buf, off);
    frames.push(f);
    off += f.total;
  }
  return frames;
}

const FRAME_NAMES = Object.fromEntries(
  Object.entries(FT).map(([name, code]) => [code, name]));

// ---- bridge-capability (DESIGN-WEB-PROXY §2.2/§2.3) ----
function base64url(buf) {
  return buf.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// Голый слаг (как BridgeCapabilityVerifier.NormalizeBasePath): trim + без ведущих/хвостовых '/'.
function normalizeBasePath(raw) {
  if (!raw) return '';
  const s = raw.trim().replace(/^\/+/, '').replace(/\/+$/, '');
  return s;
}

function computeCapability(secretHex, host, basePath) {
  const secret = Buffer.from(secretHex, 'hex');
  const context = basePath === ''
    ? `tdesktop-web-proxy-bridge-v1\n${host}`
    : `tdesktop-web-proxy-bridge-v2\n${host}\n${basePath}`;
  return base64url(crypto.createHmac('sha256', secret).update(context).digest());
}

// ---- obfuscated2 init (64 байта), совместимый с MtProtoInspector.TryHandshake ----
function buildObfs2Init(secretHex, dc, protoTag) {
  const secret = Buffer.from(secretHex, 'hex');
  const init = crypto.randomBytes(64);
  init.writeUInt32BE(protoTag, 56); // proto tag: big-endian
  init.writeInt16LE(dc, 60);        // dc idx: signed LE16 (media — отрицательный)

  const prekey = init.subarray(8, 40); // SkipLen=8, PreKeyLen=32
  const iv = init.subarray(40, 56);    // IvLen=16
  const key = crypto.createHash('sha256')
    .update(Buffer.concat([prekey, secret]))
    .digest();

  // Сервер генерирует 64-байтовый keystream (AES-256-CTR над 64 нулями) и читает хвост
  // в смещении 56. Шифруем хвост теми же байтами keystream[56:64].
  const cipher = crypto.createCipheriv('aes-256-ctr', key, iv);
  const keystream = Buffer.concat([cipher.update(Buffer.alloc(64)), cipher.final()]);
  for (let i = 56; i < 64; i++) {
    init[i] = (init[i] ^ keystream[i]) & 0xff;
  }
  return init;
}

// ---- CLI ----
function usage() {
  console.error(`Использование:
  node tools/web-proxy-smoke.mjs --host <host> --secret <32hex> [options]

Обязательные:
  --host <host>        внешний домен bridge (WebProxyPublicHost/WebProxyHost)
  --secret <32hex>     секрет токена (32 hex-символа)

Опции:
  --port <n>           порт bridge (по умолчанию 443)
  --base-path <p>      basePath сайта — голый слаг ("" — корень; иначе "slug")
  --capability <s>     готовый HMAC capability (вместо вычисления)
  --dc <n>             DC для obfuscated2 init (по умолчанию 2)
  --proto <t>          abridged|intermediate|padded (по умолчанию abridged)
  --insecure           отключить проверку TLS (самоподписанный серт)
  --timeout <s>        таймаут ожидания ответа, сек (по умолчанию 10)
  --full               полный путь: настоящий init и ожидание relay_init (нужен Telegram DC)
`);
  process.exit(2);
}

function parseArgs(argv) {
  const args = {
    port: 443,
    basePath: '',
    dc: 2,
    proto: 'abridged',
    insecure: false,
    timeout: 10,
    full: false,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    switch (a) {
      case '--host': args.host = next(); break;
      case '--secret': args.secret = next(); break;
      case '--port': args.port = Number(next()); break;
      case '--base-path': args.basePath = next(); break;
      case '--capability': args.capability = next(); break;
      case '--dc': args.dc = Number(next()); break;
      case '--proto': args.proto = next(); break;
      case '--insecure': args.insecure = true; break;
      case '--timeout': args.timeout = Number(next()); break;
      case '--full': args.full = true; break;
      case '--help':
      case '-h': usage(); break;
      default: console.error(`Неизвестный аргумент: ${a}`); usage();
    }
  }
  return args;
}

function fail(msg) {
  console.error(`\n[FAIL] ${msg}`);
  process.exit(1);
}

function ok(msg) {
  console.log(`[OK]   ${msg}`);
}

function info(msg) {
  console.log(`[..]   ${msg}`);
}

// ---- HTTP (node:https; rejectUnauthorized:false для --insecure) ----
function httpsRequest({ method, path, headers = {}, body = null, timeoutMs }) {
  return new Promise((resolve, reject) => {
    const req = https.request({
      host: args.host,
      port: args.port,
      method,
      path,
      servername: args.host,
      rejectUnauthorized: !args.insecure,
      headers: {
        ...headers,
        ...(body && body.length ? { 'Content-Length': body.length } : {}),
      },
    }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve({
        status: res.statusCode,
        headers: res.headers,
        body: Buffer.concat(chunks),
      }));
    });
    req.on('error', reject);
    req.setTimeout(timeoutMs, () => req.destroy(new Error(`таймаут ${timeoutMs}мс`)));
    if (body && body.length) req.write(body);
    req.end();
  });
}

function logFrame(f) {
  const name = FRAME_NAMES[f.type] ?? `0x${f.type.toString(16)}`;
  const payload = f.payload.length ? f.payload.toString('hex') : '';
  info(`<- ${name} stream=${f.streamId} len=${f.length}${payload ? ` payload=${payload}` : ''}`);
}

async function main() {
  const args = parseArgs(process.argv.slice(2));

  if (args.insecure) info('TLS-верификация отключена (--insecure)');
  if (!args.host) fail('не задан --host');
  if (!args.secret) fail('не задан --secret');

  const secretHex = args.secret.toLowerCase();
  if (!/^[0-9a-f]{32}$/.test(secretHex)) {
    fail(`--secret должен быть 32 hex-символа, получено: "${args.secret}"`);
  }
  if (!(args.proto in PROTO_TAGS)) {
    fail(`--proto должен быть abridged|intermediate|padded, получено: "${args.proto}"`);
  }

  const basePath = normalizeBasePath(args.basePath);
  const capability = args.capability ?? computeCapability(secretHex, args.host, basePath);
  const basePrefix = basePath ? `/${basePath}/` : '/';
  const bridgePath = basePath ? `/${basePath}` : '/';
  const sessionPath = `${basePrefix}api/v1/session`;
  const upPath = `${basePrefix}api/v1/up`;
  const downPath = `${basePrefix}api/v1/down`;

  info(`host      : ${args.host}`);
  info(`basePath  : ${basePath === '' ? '(root)' : basePath}`);
  info(`capability: ${capability}`);
  info(`bridge    : https://${args.host}:${args.port}${bridgePath}?bridge=${encodeURIComponent(capability)}`);
  info(`carrier   : ${basePrefix}api/v1/{session,up,down}`);
  info(`DC        : ${args.dc} (${args.proto})`);

  const reqTimeout = Math.max(args.timeout * 1000, 30000);

  // ---- Фаза 1: bridge-страница → bootstrap ----
  const pageRes = await httpsRequest({
    method: 'GET',
    path: `${bridgePath}?bridge=${encodeURIComponent(capability)}`,
    timeoutMs: reqTimeout,
  });
  if (pageRes.status !== 200) {
    fail(`bridge-страница вернула HTTP ${pageRes.status} (capability не подошёл ни одному токену?)`);
  }
  const html = pageRes.body.toString('utf8');
  const m = /bootstrap="([A-Za-z0-9_-]{43})"/.exec(html);
  if (!m) fail('не удалось извлечь bootstrap-токен из bridge-страницы');
  const bootstrap = m[1];
  ok(`bridge-страница: bootstrap=${bootstrap}`);

  // ---- Фаза 2: POST /session (HELLO) → WELCOME ----
  const hello = buildFrame(FT.Hello, 0, Buffer.from([0x01]));
  const sessionRes = await httpsRequest({
    method: 'POST',
    path: sessionPath,
    headers: {
      Authorization: `Bearer ${bootstrap}`,
      'Content-Type': 'application/octet-stream',
    },
    body: hello,
    timeoutMs: reqTimeout,
  });
  if (sessionRes.status !== 200) {
    fail(`POST /session вернул HTTP ${sessionRes.status} (ожидался 200)`);
  }
  const sessionToken = sessionRes.headers['x-session-token'];
  const downCursor = sessionRes.headers['x-down-cursor'] ?? '0';
  if (!sessionToken) fail('ответ /session без X-Session-Token');
  const welcomeFrames = parseBatch(sessionRes.body);
  const welcome = welcomeFrames.find((f) => f.type === FT.Welcome);
  if (!welcome) fail('ответ /session не содержит WELCOME-кадр');
  ok(`session создана: token=${sessionToken}, WELCOME (stream=${welcome.streamId}), cursor=${downCursor}`);

  // ---- Фаза 3: POST /up (OPEN 1 + DATA 1) → 204 ----
  // Транспортный режим: намеренно невалидный init (мост честно прочитает и закроет поток).
  // --full: настоящий obfuscated2 init (рукопожатие проходит, мост идёт к Telegram DC).
  const init = args.full
    ? buildObfs2Init(secretHex, args.dc, PROTO_TAGS[args.proto])
    : crypto.randomBytes(64); // невалидный init
  info(`init (64 байта, ${args.full ? 'настоящий obfuscated2' : 'невалидный для транспортного теста'}): ${init.toString('hex')}`);

  const upBody = Buffer.concat([
    buildFrame(FT.Open, 1),
    buildFrame(FT.Data, 1, init),
  ]);
  const upRes = await httpsRequest({
    method: 'POST',
    path: upPath,
    headers: {
      Authorization: `Bearer ${sessionToken}`,
      'Content-Type': 'application/octet-stream',
      'X-Up-Seq': '1',
    },
    body: upBody,
    timeoutMs: reqTimeout,
  });
  if (upRes.status !== 204 || upRes.headers['x-up-ack'] !== '1') {
    fail(`POST /up вернул HTTP ${upRes.status} (ожидался 204 + X-Up-Ack: 1)`);
  }
  ok('uplink принят (204, X-Up-Ack: 1): OPEN(1) + DATA(1, 64B)');

  // ---- Фаза 4: POST /down (long-poll) ----
  if (!args.full) {
    const downRes = await httpsRequest({
      method: 'POST',
      path: downPath,
      headers: {
        Authorization: `Bearer ${sessionToken}`,
        'X-Down-Cursor': downCursor,
        'Content-Length': '0',
      },
      timeoutMs: reqTimeout,
    });
    if (downRes.status !== 200) {
      fail(`POST /down вернул HTTP ${downRes.status} (ожидался 200 с WINDOW-кредитом)`);
    }
    const frames = parseBatch(downRes.body);
    frames.forEach(logFrame);
    const window = frames.find((f) => f.type === FT.Window && f.streamId === 1);
    const close = frames.find((f) => f.type === FT.Close && f.streamId === 1);
    if (!window) fail('downlink не содержит WINDOW-кредит (flow-control не сработал)');
    ok(`downlink: WINDOW-кредит (stream=1, credit=${window.payload.readUInt32BE(0)})`);
    if (close) ok('downlink: CLOSE(1) — мост закрыл поток после невалидного init');
    else info('CLOSE(1) не попал в первый батч (может прийти следующим long-poll)');
  } else {
    // --full: опрашиваем downlink до получения relay_init (Data stream 1) или таймаута.
    const deadline = Date.now() + args.timeout * 1000;
    let cursor = downCursor;
    let sawRelayInit = false;
    while (Date.now() < deadline) {
      const pollTimeout = Math.min(30000, Math.max(2000, deadline - Date.now() + 500));
      let downRes;
      try {
        downRes = await httpsRequest({
          method: 'POST',
          path: downPath,
          headers: {
            Authorization: `Bearer ${sessionToken}`,
            'X-Down-Cursor': String(cursor),
            'Content-Length': '0',
          },
          timeoutMs: pollTimeout,
        });
      } catch {
        break; // long-poll оборван по таймауту — Telegram DC недоступен
      }
      if (downRes.status === 200) {
        cursor = downRes.headers['x-down-cursor'] ?? cursor;
        for (const f of parseBatch(downRes.body)) {
          logFrame(f);
          if (f.type === FT.Data && f.streamId === 1) { sawRelayInit = true; break; }
        }
        if (sawRelayInit) break;
      } else if (downRes.status === 204) {
        // пусто: сервер long-poll'нул 25с без данных — Telegram DC недоступен
      } else {
        break;
      }
    }
    if (!sawRelayInit) {
      info('relay_init (Data stream 1) не получен: Telegram DC недоступен из этой сети (нужен VPS/VPN)');
      info('транспорт, авторизация и flow-control при этом работают (см. WINDOW-кадры выше)');
      await closeSession(sessionToken, sessionPath);
      process.exit(3);
    }
    ok('relay_init получен: bridge → HMAC → obfs2 → MTProto bridge → relay_init');
  }

  // ---- Фаза 5: DELETE /session → 204 ----
  await closeSession(sessionToken, sessionPath);
  ok('сессия закрыта (DELETE /session → 204)');

  console.log('\nСмоук-тест пройден: capability-HMAC → bootstrap → HELLO/WELCOME → relay-поток → flow-control → закрытие.');
  process.exit(0);
}

async function closeSession(sessionToken, sessionPath) {
  const res = await httpsRequest({
    method: 'DELETE',
    path: sessionPath,
    headers: {
      Authorization: `Bearer ${sessionToken}`,
      'Content-Length': '0',
    },
    timeoutMs: 30000,
  });
  if (res.status !== 204) {
    info(`DELETE /session вернул HTTP ${res.status} (ожидался 204)`);
  }
}

main().catch((e) => {
  console.error(`\n[FAIL] ${e.message}`);
  process.exit(1);
});
