import { SELF } from "cloudflare:test";
import { fingerprintOf } from "../src/auth";

export const pemBody = (pem: string) => pem.replace(/-----[^-]+-----/g, "").replace(/\s+/g, "");
const b64ToBytes = (b64: string) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
const bytesToB64 = (b: Uint8Array) => btoa(String.fromCharCode(...b));

export async function fingerprintOfPem(certPem: string): Promise<string> {
  return fingerprintOf(b64ToBytes(pemBody(certPem)));
}

export async function sign(keyPem: string, endpoint: string, nonce: Uint8Array): Promise<string> {
  const key = await crypto.subtle.importKey(
    "pkcs8",
    b64ToBytes(pemBody(keyPem)),
    { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const prefix = new TextEncoder().encode(`hearthlink-relay-v1\n${endpoint}\n`);
  const data = new Uint8Array(prefix.length + nonce.length);
  data.set(prefix);
  data.set(nonce, prefix.length);
  return bytesToB64(new Uint8Array(await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, data)));
}

interface Inbox {
  queue: string[];
  waiters: ((m: string) => void)[];
  closed: Promise<{ code: number; reason: string }>;
}
const inboxes = new WeakMap<WebSocket, Inbox>();

/** Opens a WebSocket upgrade against the worker and starts queueing its messages. No auth is performed. */
export async function openRaw(path: string, headers: Record<string, string> = {}): Promise<WebSocket> {
  const res = await SELF.fetch(`https://relay.test${path}`, { headers: { Upgrade: "websocket", ...headers } });
  const ws = res.webSocket;
  if (!ws) throw new Error(`upgrade failed: ${res.status}`);
  ws.accept();
  const inbox: Inbox = {
    queue: [],
    waiters: [],
    closed: new Promise((resolve) =>
      ws.addEventListener("close", (ev) => resolve({ code: ev.code, reason: ev.reason })),
    ),
  };
  ws.addEventListener("message", (ev) => {
    const data = String(ev.data);
    const waiter = inbox.waiters.shift();
    if (waiter) waiter(data);
    else inbox.queue.push(data);
  });
  inboxes.set(ws, inbox);
  return ws;
}

export function nextText(ws: WebSocket): Promise<string> {
  const inbox = inboxes.get(ws)!;
  const queued = inbox.queue.shift();
  if (queued !== undefined) return Promise.resolve(queued);
  return new Promise((resolve) => inbox.waiters.push(resolve));
}

export async function nextJson(ws: WebSocket): Promise<any> {
  return JSON.parse(await nextText(ws));
}

export function closedWith(ws: WebSocket): Promise<{ code: number; reason: string }> {
  return inboxes.get(ws)!.closed;
}

/** Answers a challenge already received on `ws`, signing with `keyPem` for `endpoint`. */
export async function answerChallenge(
  ws: WebSocket,
  challenge: { nonce: string },
  endpoint: string,
  keyPem: string,
  certPem: string,
): Promise<void> {
  const nonce = b64ToBytes(challenge.nonce);
  ws.send(JSON.stringify({ t: "auth", cert: pemBody(certPem), sig: await sign(keyPem, endpoint, nonce) }));
}

/** Opens `path`, answers the challenge (signed for the path without its query string) and waits for ok. */
export async function openAuthed(
  path: string,
  keyPem: string,
  certPem: string,
  headers: Record<string, string> = {},
): Promise<WebSocket> {
  const ws = await openRaw(path, headers);
  await answerChallenge(ws, await nextJson(ws), path.split("?")[0], keyPem, certPem);
  const ok = await nextJson(ws);
  if (ok.t !== "ok") throw new Error(`auth failed: ${JSON.stringify(ok)}`);
  return ws;
}
