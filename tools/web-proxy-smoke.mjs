#!/usr/bin/env node
//
// Смоук-тест WEB proxy (browser-relay) для tg-ws-proxy-csharp.
//
// Эмулирует bridge-клиента (то, что делает tdesktop в dev-ветке) без самого клиента:
//   1. WSS → /transport?bridge=<capability>   (HMAC-SHA256 от секрета токена)
//   2. Hello → ожидаем Welcome                (сессионное приветствие)
//   3. Ping  → ожидаем Pong                   (keepalive / живость relay)
//   4. Open(stream 1) + Data(64-байтный obfuscated2 init) → ожидаем Data(relay_init)
//
// Фаза 4 проверяет полный путь: разбор relay-frame → HMAC-авторизация → чтение 64 байт
// init → TryHandshake → MtProtoBridgeEngine → отправка relay_init обратно в поток.
//
// Требования: Node.js >= 20 и пакет `ws` (выполни `npm install` в папке tools/).
// Встроенный WebSocket (undici) не умеет отключать проверку самоподписанного
// TLS-сертификата, поэтому для --insecure используется `ws` с rejectUnauthorized:false.
//
// Пример:
//   node tools/web-proxy-smoke.mjs \
//     --host bridge.example.com \
//     --secret 00112233445566778899aabbccddeeff \
//     [--port 443] [--base-path /v1] [--dc 2] [--proto abridged] [--insecure] [--timeout 10]
//
// --capability <base64url>: пропустить вычисление HMAC и подставить готовое значение
//   (можно скопировать часть после "?bridge=" из bridge-URL панели).
// --insecure: отключить проверку TLS-сертификата (для самоподписанного тестового серта).

import crypto from 'node:crypto';

async function loadWs() {
  const mod = await import('ws');
  return mod.default ?? mod.WebSocket;
}

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

const FRAME_NAMES = Object.fromEntries(
  Object.entries(FT).map(([name, code]) => [code, name]));

// ---- bridge-capability (DESIGN-WEB-PROXY §2.2/§2.3) ----
function base64url(buf) {
  return buf.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function normalizeBasePath(raw) {
  if (!raw) return '';
  let s = raw.trim();
  if (s === '' || s === '/') return '';
  if (!s.startsWith('/')) s = '/' + s;
  s = s.replace(/\/+$/, '');
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
  // init[62:64] уже случайные

  const prekey = init.subarray(8, 40); // SkipLen=8, PreKeyLen=32
  const iv = init.subarray(40, 56);    // IvLen=16
  const key = crypto.createHash('sha256')
    .update(Buffer.concat([prekey, secret]))
    .digest();

  // Сервер (MtProtoInspector.TryHandshake) генерирует 64-байтовый keystream
  // (AES-256-CTR над 64 нулями) и читает хвост в смещении 56. Шифруем хвост
  // теми же байтами keystream[56:64], а не первыми 8 байтами потока.
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
  --base-path <p>      basePath сайта ("" — корень; иначе "/v1")
  --capability <s>     готовый HMAC capability (вместо вычисления)
  --dc <n>             DC для obfuscated2 init (по умолчанию 2)
  --proto <t>          abridged|intermediate|padded (по умолчанию abridged)
  --insecure           отключить проверку TLS (самоподписанный серт)
  --timeout <s>        таймаут ожидания ответа, сек (по умолчанию 10)
  --no-full            пропустить фазу 4 (только Hello/Ping)
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
    full: true,
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
      case '--no-full': args.full = false; break;
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

async function main() {
  const args = parseArgs(process.argv.slice(2));

  if (args.insecure) {
    info('TLS-верификация отключена (--insecure)');
  }
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
  const path = `${basePath}/transport`;
  const url = `wss://${args.host}:${args.port}${path}?bridge=${encodeURIComponent(capability)}`;

  info(`host      : ${args.host}`);
  info(`basePath  : ${basePath === '' ? '(root)' : basePath}`);
  info(`capability: ${capability}`);
  info(`url       : ${url}`);
  info(`DC        : ${args.dc} (${args.proto})`);

  const WS = await loadWs().catch(() => null);
  if (!WS) {
    fail('не найден пакет "ws". Выполни в папке tools/:  npm install');
  }

  const ws = new WS(url, {
    rejectUnauthorized: !args.insecure,
    handshakeTimeout: (args.timeout + 5) * 1000,
  });
  const received = [];
  const listeners = [];

  ws.onmessage = (ev) => {
    const buf = Buffer.from(ev.data);
    let off = 0;
    while (off + FRAME_HEADER_SIZE <= buf.length) {
      const f = parseFrame(buf, off);
      off += f.total;
      for (const l of [...listeners]) l(f);
      received.push(f);
    }
  };

  const closeError = { reason: null };
  ws.onclose = (ev) => {
    closeError.reason = new Error(`соединение закрыто (code=${ev.code}, reason=${ev.reason})`);
    for (const l of [...listeners]) l(null); // разбудить ожидающих
  };
  ws.onerror = () => {
    closeError.reason = new Error('ошибка WebSocket (TLS? см. --insecure)');
    for (const l of [...listeners]) l(null);
  };

  function waitFor(predicate, timeoutMs, label) {
    return new Promise((resolve, reject) => {
      const idx = received.findIndex(predicate);
      if (idx >= 0) {
        const f = received[idx];
        received.splice(idx, 1);
        resolve(f);
        return;
      }
      const timer = setTimeout(() => {
        const i = listeners.indexOf(handler);
        if (i >= 0) listeners.splice(i, 1);
        reject(new Error(`таймаут ${timeoutMs}мс в ожидании ${label}${closeError.reason ? ` (${closeError.reason.message})` : ''}`));
      }, timeoutMs);
      function handler(f) {
        if (f !== null && predicate(f)) {
          clearTimeout(timer);
          const i = listeners.indexOf(handler);
          if (i >= 0) listeners.splice(i, 1);
          resolve(f);
        } else if (f === null) {
          clearTimeout(timer);
          const i = listeners.indexOf(handler);
          if (i >= 0) listeners.splice(i, 1);
          reject(closeError.reason ?? new Error('соединение закрыто'));
        }
      }
      listeners.push(handler);
    });
  }

  await new Promise((resolve, reject) => {
    ws.onopen = resolve;
    ws.onerror = () => reject(new Error('не удалось открыть WebSocket (TLS? см. --insecure)'));
  });
  ok(`WebSocket открыт: ${path}`);

  // Фаза 2: Hello → Welcome
  ws.send(buildFrame(FT.Hello, 0));
  const welcome = await waitFor(
    (f) => f.type === FT.Welcome,
    args.timeout * 1000,
    'Welcome');
  ok(`Hello → Welcome (payload ${welcome.length} байт)`);

  // Фаза 3: Ping → Pong
  ws.send(buildFrame(FT.Ping, 0));
  const pong = await waitFor(
    (f) => f.type === FT.Pong,
    args.timeout * 1000,
    'Pong');
  ok(`Ping → Pong (payload ${pong.length} байт)`);

  if (!args.full) {
    info('--no-full: фаза 4 (MTProto handshake) пропущена');
    ws.send(buildFrame(FT.Bye, 0));
    ws.close(1000, 'smoke done');
    console.log('\nСмоук-тест (transport) пройден: relay-frame + HMAC-авторизация работают.');
    process.exit(0);
  }

  // Фаза 4: Open(1) + Data(obfuscated2 init) → Data(relay_init)
  const init = buildObfs2Init(secretHex, args.dc, PROTO_TAGS[args.proto]);
  info(`obfs2 init (64 байта): ${init.toString('hex')}`);

  ws.send(buildFrame(FT.Open, 1));
  ws.send(buildFrame(FT.Data, 1, init));

  try {
    const relay = await waitFor(
      (f) => f.type === FT.Data && f.streamId === 1,
      args.timeout * 1000,
      'relay_init (Data stream 1)');
    ok(`relay_init получен (${relay.length} байт): ${relay.payload.toString('hex')}`);
    console.log('\nСмоук-тест пройден: bridge → HMAC → obfs2 handshake → MTProto bridge → relay_init.');
  } catch (e) {
    ws.send(buildFrame(FT.Close, 1));
    ws.send(buildFrame(FT.Bye, 0));
    ws.close(1000, 'smoke done');
    console.error(`\n[WARN] Фаза 4 не прошла: ${e.message}`);
    console.error('Транспорт и авторизация работают (Hello/Ping ок), но relay_init не пришёл.');
    console.error('Проверьте логи прокси: доступен ли Telegram DC (ws-пул / fallback), интернет, а также --dc и --secret.');
    process.exit(3);
  }

  ws.send(buildFrame(FT.Close, 1));
  ws.send(buildFrame(FT.Bye, 0));
  ws.close(1000, 'smoke done');
  process.exit(0);
}

main().catch((e) => {
  console.error(`\n[FAIL] ${e.message}`);
  process.exit(1);
});
