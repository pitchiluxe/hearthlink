import { DurableObject } from "cloudflare:workers";
import { authenticate, newChallenge, verifyAuth } from "./auth";
import { pipe } from "./pipe";
import type { Env } from "./env";

const ENDPOINT = "/v1/presence";
const AUTH_TIMEOUT_MS = 10_000;
const STALE_MS = 60_000;
const PAIRING_WINDOW_MS = 120_000;
const LOOKUP_PATTERN = /^[0-9]{4}$/;
const CLOSE_BAD_FRAME = 4400;
const MAX_ALLOWED = 1000;

interface Pending {
  controller: WebSocket;
  timer: ReturnType<typeof setTimeout>;
  /** Removes the listeners that watch the waiting controller. */
  detach: () => void;
}

const toHex = (bytes: Uint8Array) => Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

function closeQuietly(ws: WebSocket, code: number, reason: string): void {
  try {
    ws.close(code, reason);
  } catch {
    /* already closed */
  }
}

/** Per-socket state kept across hibernation with serializeAttachment. */
interface Attachment {
  /** Fingerprint named in the URL; the authenticated fingerprint must equal it. */
  expected: string;
  /** Challenge nonce (base64) until the socket is authenticated. */
  nonce: string;
  /** Epoch ms by which the socket must authenticate. */
  deadline: number;
  /** Epoch ms of authentication; staleness baseline until the first ping. */
  authedAt: number | null;
}

interface PairingState {
  lookup: string;
  until: number;
}

const fromBase64 = (b64: string) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));

/** One instance per host fingerprint: presence socket, allow list and pairing lookup. */
export class HostRoom extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  /** Controllers waiting for the host to open its data socket, by session id. Lives while those sockets do. */
  private pending = new Map<string, Pending>();

  /** How long a controller waits for the host to accept. Instance field so tests can shorten it. */
  acceptTimeoutMs = 15_000;

  async fetch(request: Request): Promise<Response> {
    if (request.headers.get("Upgrade") !== "websocket") {
      return new Response("expected websocket", { status: 426 });
    }
    const url = new URL(request.url);
    if (url.pathname === "/v1/connect" || url.pathname === "/v1/accept") {
      const pair = new WebSocketPair();
      const [client, server] = Object.values(pair);
      server.accept();
      const run = url.pathname === "/v1/connect" ? this.runConnect(server, url) : this.runAccept(server, url);
      run.catch(() => closeQuietly(server, 1011, "internal error"));
      return new Response(null, { status: 101, webSocket: client });
    }
    const expected = new URL(request.url).searchParams.get("id") ?? "";
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    this.ctx.acceptWebSocket(server);
    const { nonce, message } = newChallenge();
    const deadline = Date.now() + AUTH_TIMEOUT_MS;
    const attachment: Attachment = {
      expected,
      nonce: btoa(String.fromCharCode(...nonce)),
      deadline,
      authedAt: null,
    };
    server.serializeAttachment(attachment);
    server.send(message);
    await this.scheduleAuthAlarm();
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws: WebSocket, data: string | ArrayBuffer): Promise<void> {
    const state = ws.deserializeAttachment() as Attachment | null;
    if (!state) return ws.close(4401, "unauthorized");
    if (typeof data !== "string") return ws.close(state.authedAt === null ? 4401 : CLOSE_BAD_FRAME, "text only");
    let msg: any;
    try {
      msg = JSON.parse(data);
    } catch {
      return ws.close(state.authedAt === null ? 4401 : CLOSE_BAD_FRAME, "bad json");
    }
    if (state.authedAt === null) return this.handleAuth(ws, state, msg);
    switch (msg?.t) {
      case "allow":
        return this.handleAllow(msg);
      case "pairing":
        return this.handlePairing(ws, state, msg);
      case "pairing-close":
        return this.handlePairingClose(state);
    }
  }

  /** Closes sockets that failed to authenticate in time (4408) and re-arms for any remaining. */
  async alarm(): Promise<void> {
    const now = Date.now();
    for (const ws of this.ctx.getWebSockets()) {
      const state = ws.deserializeAttachment() as Attachment | null;
      if (state && state.authedAt === null && state.deadline <= now) {
        try {
          ws.close(4408, "auth timeout");
        } catch {
          /* already closed */
        }
      }
    }
    await this.scheduleAuthAlarm();
  }

  async isOnlineFor(caller: string): Promise<boolean> {
    const allowed = this.ctx.storage.kv.get<string[]>("allow") ?? [];
    return allowed.includes(caller) && this.livePresence() !== null;
  }

  /** The authenticated presence socket with a fresh heartbeat, if any. */
  private livePresence(): WebSocket | null {
    const now = Date.now();
    return (
      this.ctx.getWebSockets().find((ws) => {
        if (ws.readyState !== WebSocket.READY_STATE_OPEN) return false;
        const state = ws.deserializeAttachment() as Attachment | null;
        if (!state || state.authedAt === null) return false;
        const lastPing = this.ctx.getWebSocketAutoResponseTimestamp(ws)?.getTime() ?? 0;
        return now - Math.max(lastPing, state.authedAt) <= STALE_MS;
      }) ?? null
    );
  }

  hasLookup(lookup: string): boolean {
    const pairing = this.ctx.storage.kv.get<PairingState>("pairing");
    return !!pairing && pairing.lookup === lookup && pairing.until > Date.now();
  }

  /** Controller side: authenticate, check access, announce to the host and wait for it to accept. */
  private async runConnect(ws: WebSocket, url: URL): Promise<void> {
    const to = url.searchParams.get("to");
    const pairLookup = url.searchParams.get("pair");
    const caller = await authenticate(ws, "/v1/connect");
    if (caller === null) return;
    const presence = this.livePresence();
    if (presence === null || (pairLookup !== null && !this.hasLookup(pairLookup))) {
      return ws.close(4404, "host offline");
    }
    if (to !== null) {
      const allowed = this.ctx.storage.kv.get<string[]>("allow") ?? [];
      if (!allowed.includes(caller)) return ws.close(4403, "not allowed");
    }
    const session = toHex(crypto.getRandomValues(new Uint8Array(16)));
    const onEarlyMessage = () => this.dropPending(session, 4400, "wait for ready");
    const onEnd = () => this.dropPending(session);
    const detach = () => {
      ws.removeEventListener("message", onEarlyMessage);
      ws.removeEventListener("close", onEnd);
      ws.removeEventListener("error", onEnd);
    };
    const timer = setTimeout(() => this.dropPending(session, 4408, "host did not accept"), this.acceptTimeoutMs);
    this.pending.set(session, { controller: ws, timer, detach });
    ws.addEventListener("message", onEarlyMessage);
    ws.addEventListener("close", onEnd);
    ws.addEventListener("error", onEnd);
    try {
      presence.send(JSON.stringify({ t: "incoming", session, from: caller }));
    } catch {
      this.dropPending(session, 4404, "host offline");
    }
  }

  /** Removes a pending session and, if a code is given, closes its controller. */
  private dropPending(session: string, code?: number, reason?: string): void {
    const entry = this.pending.get(session);
    if (!entry) return;
    this.pending.delete(session);
    clearTimeout(entry.timer);
    entry.detach();
    if (code !== undefined) closeQuietly(entry.controller, code, reason ?? "");
  }

  /** Host side: authenticate, claim the waiting session and pipe it to its controller. */
  private async runAccept(ws: WebSocket, url: URL): Promise<void> {
    const host = url.searchParams.get("host") ?? "";
    const session = url.searchParams.get("session") ?? "";
    const caller = await authenticate(ws, "/v1/accept");
    if (caller === null) return;
    if (caller !== host) return ws.close(4401, "unauthorized");
    const entry = this.pending.get(session);
    if (!entry) return ws.close(4404, "unknown session");
    // Claim synchronously so a session is consumed by exactly one accept.
    this.pending.delete(session);
    clearTimeout(entry.timer);
    entry.detach();
    if (entry.controller.readyState !== WebSocket.READY_STATE_OPEN) return ws.close(4404, "controller gone");
    try {
      entry.controller.send(JSON.stringify({ t: "ready" }));
    } catch {
      return ws.close(4404, "controller gone");
    }
    pipe(entry.controller, ws);
  }

  private async scheduleAuthAlarm(): Promise<void> {
    const now = Date.now();
    let next: number | null = null;
    for (const ws of this.ctx.getWebSockets()) {
      if (ws.readyState !== WebSocket.READY_STATE_OPEN) continue;
      const state = ws.deserializeAttachment() as Attachment | null;
      if (!state || state.authedAt !== null || state.deadline <= now) continue;
      if (next === null || state.deadline < next) next = state.deadline;
    }
    if (next !== null) await this.ctx.storage.setAlarm(next);
  }

  private async handleAuth(ws: WebSocket, state: Attachment, msg: any): Promise<void> {
    if (msg?.t !== "auth" || typeof msg.cert !== "string" || typeof msg.sig !== "string") {
      return ws.close(4401, "unauthorized");
    }
    if (Date.now() > state.deadline) return ws.close(4408, "auth timeout");
    const id = await verifyAuth(ENDPOINT, fromBase64(state.nonce), msg.cert, msg.sig);
    if (id === null || id !== state.expected) return ws.close(4401, "unauthorized");
    // The socket may have been closed or authenticated while the signature was being checked.
    const current = ws.deserializeAttachment() as Attachment | null;
    if (!current || current.authedAt !== null || ws.readyState !== WebSocket.READY_STATE_OPEN) return;
    for (const other of this.ctx.getWebSockets()) {
      const otherState = other.deserializeAttachment() as Attachment | null;
      if (other !== ws && otherState && otherState.authedAt !== null) {
        try {
          other.close(4409, "replaced");
        } catch {
          /* already closed */
        }
      }
    }
    ws.serializeAttachment({ ...current, authedAt: Date.now() } satisfies Attachment);
    ws.send(JSON.stringify({ t: "ok", id }));
  }

  private handleAllow(msg: any): void {
    if (!Array.isArray(msg.ids)) return;
    const ids = msg.ids.filter((x: unknown): x is string => typeof x === "string").slice(0, MAX_ALLOWED);
    this.ctx.storage.kv.put("allow", ids);
  }

  private async handlePairing(ws: WebSocket, state: Attachment, msg: any): Promise<void> {
    const lookup = msg.lookup;
    if (typeof lookup !== "string" || !LOOKUP_PATTERN.test(lookup)) {
      ws.send(JSON.stringify({ t: "pairing-taken" }));
      return;
    }
    const index = this.env.INDEX.get(this.env.INDEX.idFromName("index"));
    const previous = this.ctx.storage.kv.get<PairingState>("pairing");
    const until = Date.now() + PAIRING_WINDOW_MS;
    if (!(await index.claim(lookup, state.expected, until))) {
      ws.send(JSON.stringify({ t: "pairing-taken" }));
      return;
    }
    if (previous && previous.lookup !== lookup) await index.release(previous.lookup, state.expected);
    this.ctx.storage.kv.put<PairingState>("pairing", { lookup, until });
    ws.send(JSON.stringify({ t: "pairing-ok" }));
  }

  private async handlePairingClose(state: Attachment): Promise<void> {
    const previous = this.ctx.storage.kv.get<PairingState>("pairing");
    if (!previous) return;
    this.ctx.storage.kv.delete("pairing");
    await this.env.INDEX.get(this.env.INDEX.idFromName("index")).release(previous.lookup, state.expected);
  }
}
