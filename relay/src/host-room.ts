import { DurableObject } from "cloudflare:workers";
import { newChallenge, verifyAuth } from "./auth";
import type { Env } from "./env";

const ENDPOINT = "/v1/presence";
const AUTH_TIMEOUT_MS = 10_000;
const STALE_MS = 60_000;
const PAIRING_WINDOW_MS = 120_000;
const MAX_LOOKUP_LENGTH = 32;
const MAX_ALLOWED = 1000;

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

  async fetch(request: Request): Promise<Response> {
    if (request.headers.get("Upgrade") !== "websocket") {
      return new Response("expected websocket", { status: 426 });
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
    if (typeof data !== "string") return ws.close(state.authedAt === null ? 4401 : 1003, "text only");
    let msg: any;
    try {
      msg = JSON.parse(data);
    } catch {
      return ws.close(state.authedAt === null ? 4401 : 1007, "bad json");
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
    if (!allowed.includes(caller)) return false;
    const now = Date.now();
    return this.ctx.getWebSockets().some((ws) => {
      const state = ws.deserializeAttachment() as Attachment | null;
      if (!state || state.authedAt === null) return false;
      const lastPing = this.ctx.getWebSocketAutoResponseTimestamp(ws)?.getTime() ?? 0;
      return now - Math.max(lastPing, state.authedAt) <= STALE_MS;
    });
  }

  hasLookup(lookup: string): boolean {
    const pairing = this.ctx.storage.kv.get<PairingState>("pairing");
    return !!pairing && pairing.lookup === lookup && pairing.until > Date.now();
  }

  private async scheduleAuthAlarm(): Promise<void> {
    let next: number | null = null;
    for (const ws of this.ctx.getWebSockets()) {
      const state = ws.deserializeAttachment() as Attachment | null;
      if (state && state.authedAt === null && (next === null || state.deadline < next)) next = state.deadline;
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
    if (typeof lookup !== "string" || lookup.length === 0 || lookup.length > MAX_LOOKUP_LENGTH) return;
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
