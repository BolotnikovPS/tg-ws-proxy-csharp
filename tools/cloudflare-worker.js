// Cloudflare Worker — релей MTProto-over-WebSocket для tg-ws-proxy-csharp.
//
// Назначение: сервер, где запущен прокси, не имеет прямого доступа к Telegram
// (заблокированы 149.154.x.x и kws*.web.telegram.org). Воркер запускается на
// Cloudflare (у которого доступ к Telegram есть) и туннелирует трафик:
//
//   прокси  --WSS-->  worker  --raw TCP-->  <dst>:443   (plain MTProto-эндпоинт Telegram)
//
// Прокси обращается к воркеру так (см. TcpBridgeService.CfWorkerFallbackAsync):
//   wss://<worker-domain>/apiws?dst=<IP>&dc=<номер>
//
// После WS-апгрейда прокси шлёт первый байтовый фрейм (RelayInit — сырые
// MTProto-байты клиента), затем двусторонний поток ре-шифрованных MTProto-байт.
// Воркер открывает СОКЕТ (не TLS) к dst:443 и прозрачно перекладывает байты:
// именно поэтому нужен cloudflare:sockets — это TCP-туннель, а не WSS-релей.
//
// Развёртывание (Cloudflare → Workers & Pages → Create → Worker):
//   1) вставить этот код, нажать Deploy;
//   2) запомнить URL вида https://<name>.<subdomain>.workers.dev;
//   3) в config.json добавить hostname (без схемы и пути) в cfProxy.workerDomains.
//
// Требование: disableSecure должен быть false (воркер слушает только HTTPS/443).

import { connect } from "cloudflare:sockets";

function toBytes(data) {
  if (data instanceof ArrayBuffer) {
    return new Uint8Array(data);
  }
  if (typeof data === "string") {
    return new TextEncoder().encode(data);
  }
  if (data && typeof data.arrayBuffer === "function") {
    return data.arrayBuffer().then((ab) => new Uint8Array(ab));
  }
  return new Uint8Array();
}

export default {
  async fetch(request) {
    if ((request.headers.get("Upgrade") || "").toLowerCase() !== "websocket") {
      return new Response("Expected websocket", { status: 426 });
    }

    const url = new URL(request.url);
    if (url.pathname !== "/apiws") {
      return new Response("Not found", { status: 404 });
    }

    const dst = url.searchParams.get("dst");
    if (!dst) {
      return new Response("Missing dst", { status: 400 });
    }

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    server.accept();

    const socket = connect({ hostname: dst, port: 443 });
    const tcpReader = socket.readable.getReader();
    const tcpWriter = socket.writable.getWriter();

    server.addEventListener("message", async (event) => {
      try {
        await tcpWriter.write(await toBytes(event.data));
      } catch {
        try {
          server.close(1011, "tcp write failed");
        } catch {
          /* ignore */
        }
      }
    });

    server.addEventListener("close", async () => {
      try {
        await tcpWriter.close();
      } catch {
        /* ignore */
      }
      try {
        socket.close();
      } catch {
        /* ignore */
      }
    });

    (async () => {
      try {
        while (true) {
          const { value, done } = await tcpReader.read();
          if (done) {
            break;
          }
          if (value) {
            server.send(value);
          }
        }
      } catch {
        /* ignore */
      } finally {
        try {
          server.close();
        } catch {
          /* ignore */
        }
        try {
          tcpReader.releaseLock();
        } catch {
          /* ignore */
        }
        try {
          socket.close();
        } catch {
          /* ignore */
        }
      }
    })();

    return new Response(null, { status: 101, webSocket: client });
  },
};
