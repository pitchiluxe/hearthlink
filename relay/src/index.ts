import { authenticate } from "./auth";
import type { Env } from "./env";
import { clientNetwork } from "./limits";

export { HostRoom } from "./host-room";
export { PairingIndex } from "./pairing-index";

const FINGERPRINT = /^[0-9A-F]{64}$/;
const LOOKUP = /^[0-9]{4}$/;
const SESSION = /^[0-9a-f]{32}$/;
const MAX_STATUS_IDS = 64;
const QUERY_TIMEOUT_MS = 10_000;

const bad = (what: string) => new Response(`bad ${what}`, { status: 400 });

/** Accepts the upgrade in the Worker, runs `handler` on the server socket and returns the client side. */
function upgrade(handler: (server: WebSocket) => Promise<void>): Response {
  const [client, server] = Object.values(new WebSocketPair());
  server.accept();
  handler(server).catch(() => {
    try {
      server.close(1011, "internal error");
    } catch {
      /* already closed */
    }
  });
  return new Response(null, { status: 101, webSocket: client });
}

/** Authenticates, then answers one status query and closes. */
async function serveStatus(server: WebSocket, env: Env): Promise<void> {
  const caller = await authenticate(server, "/v1/status");
  if (caller === null) return;
  const query = await new Promise<unknown>((resolve) => {
    const timer = setTimeout(() => {
      server.close(4408, "query timeout");
      resolve(null);
    }, QUERY_TIMEOUT_MS);
    server.addEventListener("close", () => {
      clearTimeout(timer);
      resolve(null);
    });
    server.addEventListener(
      "message",
      (ev) => {
        clearTimeout(timer);
        try {
          resolve(typeof ev.data === "string" ? JSON.parse(ev.data) : undefined);
        } catch {
          resolve(undefined);
        }
      },
      { once: true },
    );
  });
  if (query === null) return;
  const ids = (query as { t?: unknown; ids?: unknown } | undefined)?.ids;
  if ((query as { t?: unknown } | undefined)?.t !== "query" || !Array.isArray(ids)) {
    return server.close(4400, "bad query");
  }
  const wanted = [...new Set(ids.slice(0, MAX_STATUS_IDS))].filter(
    (id): id is string => typeof id === "string" && FINGERPRINT.test(id),
  );
  const flags = await Promise.all(
    wanted.map((id) => env.HOSTS.get(env.HOSTS.idFromName(id)).isOnlineFor(caller)),
  );
  server.send(JSON.stringify({ t: "status", online: wanted.filter((_, i) => flags[i]) }));
  server.close(1000, "done");
}

/** Controller connect: forward to the host's room, or fail after authentication if the lookup is unknown. */
async function routeConnect(request: Request, url: URL, env: Env): Promise<Response> {
  const to = url.searchParams.get("to");
  const pair = url.searchParams.get("pair");
  if ((to === null) === (pair === null)) return bad("target");
  if (to !== null) {
    if (!FINGERPRINT.test(to)) return bad("to");
    return env.HOSTS.get(env.HOSTS.idFromName(to)).fetch(request);
  }
  if (!LOOKUP.test(pair!)) return bad("pair");
  const index = env.INDEX.get(env.INDEX.idFromName("index"));
  // Refused before the challenge, so a blocked client cannot keep probing which lookups are open.
  if (!(await index.allowPairAttempt(clientNetwork(request.headers.get("CF-Connecting-IP"))))) {
    return upgrade(async (server) => server.close(4429, "too many pairing attempts"));
  }
  const host = await index.resolve(pair!);
  if (host === null) {
    return upgrade(async (server) => {
      if ((await authenticate(server, "/v1/connect")) !== null) server.close(4404, "unknown lookup");
    });
  }
  return env.HOSTS.get(env.HOSTS.idFromName(host)).fetch(request);
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const known = ["/v1/presence", "/v1/connect", "/v1/accept", "/v1/status"];
    if (!known.includes(url.pathname)) return new Response("not found", { status: 404 });
    if (request.headers.get("Upgrade") !== "websocket") {
      return new Response("expected websocket", { status: 426 });
    }
    switch (url.pathname) {
      case "/v1/presence": {
        const id = url.searchParams.get("id");
        if (!id || !FINGERPRINT.test(id)) return bad("id");
        return env.HOSTS.get(env.HOSTS.idFromName(id)).fetch(request);
      }
      case "/v1/connect":
        return routeConnect(request, url, env);
      case "/v1/accept": {
        const host = url.searchParams.get("host");
        const session = url.searchParams.get("session");
        if (!host || !FINGERPRINT.test(host)) return bad("host");
        if (!session || !SESSION.test(session)) return bad("session");
        return env.HOSTS.get(env.HOSTS.idFromName(host)).fetch(request);
      }
      default:
        return upgrade((server) => serveStatus(server, env));
    }
  },
} satisfies ExportedHandler<Env>;
