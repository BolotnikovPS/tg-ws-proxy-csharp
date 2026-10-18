// Direct probe: connect to Telegram DC2 WS and send a relay_init (obfuscated2),
// to determine whether Telegram responds at all from this network.
// Uses the `ws` package (installed in tools/node_modules).
import { createRequire } from 'node:module';
import { randomBytes, createCipheriv } from 'node:crypto';

const require = createRequire(import.meta.url);
let WebSocket;
try { WebSocket = require('ws'); } catch { console.error('ws package missing: run `npm install` in tools/'); process.exit(2); }

const DC = parseInt(process.argv[2] ?? '2', 10);
const HOST = process.argv[3] ?? `kws${DC}.web.telegram.org`;
const PROTO = Buffer.from([0xEF, 0xEF, 0xEF, 0xEF]); // abridged

// Mirror of MtProtoInspector.GenerateRelayInit: raw prekey/iv, no secret hash.
function buildRelayInit(dcIdx) {
  for (;;) {
    const rnd = randomBytes(64);
    if (isReserved(rnd)) continue;
    const encKey = rnd.subarray(8, 40);
    const encIv = rnd.subarray(40, 56);
    const cipher = createCipheriv('aes-256-ctr', encKey, encIv);
    const keystream = Buffer.concat([cipher.update(Buffer.alloc(64)), cipher.final()]);
    const tailPlain = Buffer.alloc(8);
    PROTO.copy(tailPlain, 0);
    tailPlain.writeInt16LE(dcIdx, 4);
    randomBytes(2).copy(tailPlain, 6);
    const result = Buffer.from(rnd);
    for (let i = 0; i < 8; i++) result[56 + i] = tailPlain[i] ^ keystream[56 + i];
    return result;
  }
}
function isReserved(rnd) {
  const first = [0xEF];
  if (first.includes(rnd[0])) return true;
  const starts = [
    Buffer.from([0xEE, 0xEE, 0xEE, 0xEE]), Buffer.from([0xDD, 0xDD, 0xDD, 0xDD]),
    Buffer.from([0xEE, 0xEE, 0xEE, 0xEF]), Buffer.from([0xDD, 0xDD, 0xDD, 0xEE]),
  ];
  const f4 = rnd.subarray(0, 4);
  if (starts.some((s) => f4.equals(s))) return true;
  return rnd.subarray(4, 8).equals(Buffer.from([0, 0, 0, 0]));
}

const IP = process.argv[4] ?? '149.154.167.220'; // direct DC2 IP (as the server does)
const url = `wss://${IP}:443/apiws`;
const ws = new WebSocket(url, {
  headers: { Host: HOST },
  servername: HOST,          // SNI
  rejectUnauthorized: false, // IP connect + SNI override
  perMessageDeflate: false,
  protocols: ['binary'],     // mandatory Sec-WebSocket-Protocol: binary
});
ws.binaryType = 'arraybuffer';

const relayInit = buildRelayInit(DC);
console.log(`connecting ${url} ...`);
console.log(`sending relay_init dcIdx=${DC} (${relayInit.length}B)`);

const timer = setTimeout(() => {
  console.log('[RESULT] NO RESPONSE from Telegram in 6s');
  ws.terminate();
  process.exit(1);
}, 6000);

ws.on('open', () => {
  console.log('[WS] open, sending relay_init');
  ws.send(relayInit, { binary: true });
});
ws.on('message', (data) => {
  clearTimeout(timer);
  const buf = Buffer.from(data);
  console.log(`[RESULT] GOT ${buf.length} bytes from Telegram:`);
  console.log('  ' + buf.subarray(0, Math.min(64, buf.length)).toString('hex'));
  ws.close();
  process.exit(0);
});
ws.on('error', (e) => {
  clearTimeout(timer);
  console.log(`[WS] error: ${e.message}`);
  process.exit(1);
});
ws.on('close', (code, reason) => {
  clearTimeout(timer);
  console.log(`[WS] closed code=${code} reason=${reason}`);
  process.exit(1);
});
