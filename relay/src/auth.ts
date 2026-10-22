// Certificate-signature authentication for relay WebSockets.
// A device ID ("fingerprint") is the uppercase hex SHA-256 of the certificate DER,
// matching .NET Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).

const AUTH_TIMEOUT_MS = 10_000;
const DOMAIN = "hearthlink-relay-v1";

const encoder = new TextEncoder();

function toBase64(bytes: Uint8Array): string {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s);
}

function fromBase64(b64: string): Uint8Array {
  const s = atob(b64);
  const out = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) out[i] = s.charCodeAt(i);
  return out;
}

export function newChallenge(): { nonce: Uint8Array; message: string } {
  const nonce = crypto.getRandomValues(new Uint8Array(32));
  return { nonce, message: JSON.stringify({ t: "challenge", nonce: toBase64(nonce) }) };
}

export async function fingerprintOf(der: Uint8Array): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", der));
  let hex = "";
  for (const b of digest) hex += b.toString(16).padStart(2, "0");
  return hex.toUpperCase();
}

/** Reads one DER element header at `pos`; returns its total extent. */
function readElement(der: Uint8Array, pos: number): { start: number; end: number; bodyStart: number } {
  if (pos + 2 > der.length) throw new Error("DER truncated");
  let p = pos + 1; // skip tag (single-byte tags only, sufficient for X.509)
  let len = der[p++];
  if (len & 0x80) {
    const n = len & 0x7f;
    if (n === 0 || n > 4 || p + n > der.length) throw new Error("DER bad length");
    len = 0;
    for (let i = 0; i < n; i++) len = len * 256 + der[p++];
  }
  const end = p + len;
  if (end > der.length) throw new Error("DER truncated");
  return { start: pos, end, bodyStart: p };
}

/** Returns the SubjectPublicKeyInfo (with tag and length) of an X.509 certificate. Throws on malformed DER. */
export function spkiFromCertificate(der: Uint8Array): Uint8Array {
  const cert = readElement(der, 0);
  if (der[0] !== 0x30) throw new Error("certificate is not a SEQUENCE");
  const tbs = readElement(der, cert.bodyStart);
  if (der[tbs.start] !== 0x30) throw new Error("tbsCertificate is not a SEQUENCE");
  let pos = tbs.bodyStart;
  if (der[pos] === 0xa0) pos = readElement(der, pos).end; // optional [0] version
  // serialNumber, signature, issuer, validity, subject
  for (let i = 0; i < 5; i++) pos = readElement(der, pos).end;
  const spki = readElement(der, pos);
  if (der[spki.start] !== 0x30 || spki.end > tbs.end) throw new Error("bad SubjectPublicKeyInfo");
  return der.slice(spki.start, spki.end);
}

/** Verifies the client's signature; returns the device fingerprint, or null on any failure. */
export async function verifyAuth(
  endpoint: string,
  nonce: Uint8Array,
  certB64: string,
  sigB64: string,
): Promise<string | null> {
  try {
    const der = fromBase64(certB64);
    const sig = fromBase64(sigB64);
    const key = await crypto.subtle.importKey(
      "spki",
      spkiFromCertificate(der),
      { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" },
      false,
      ["verify"],
    );
    const prefix = encoder.encode(`${DOMAIN}\n${endpoint}\n`);
    const data = new Uint8Array(prefix.length + nonce.length);
    data.set(prefix);
    data.set(nonce, prefix.length);
    const ok = await crypto.subtle.verify("RSASSA-PKCS1-v1_5", key, sig, data);
    return ok ? await fingerprintOf(der) : null;
  } catch {
    return null;
  }
}

/**
 * Runs the challenge/response on an accepted server-side WebSocket.
 * Resolves to the fingerprint, or null after closing with 4401 (bad auth) or 4408 (timeout).
 * On success the "ok" reply has been sent.
 */
export function authenticate(ws: WebSocket, endpoint: string): Promise<string | null> {
  const { nonce, message } = newChallenge();
  return new Promise((resolve) => {
    let done = false;
    const finish = (id: string | null, code?: number, reason?: string) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      ws.removeEventListener("message", onMessage);
      ws.removeEventListener("close", onClose);
      if (id === null) {
        try {
          ws.close(code, reason);
        } catch {
          /* already closed */
        }
      }
      resolve(id);
    };
    const onClose = () => finish(null);
    const onMessage = async (ev: MessageEvent) => {
      try {
        if (typeof ev.data !== "string") return finish(null, 4401, "unauthorized");
        const msg = JSON.parse(ev.data);
        if (msg?.t !== "auth" || typeof msg.cert !== "string" || typeof msg.sig !== "string") {
          return finish(null, 4401, "unauthorized");
        }
        const id = await verifyAuth(endpoint, nonce, msg.cert, msg.sig);
        if (done) return;
        if (id === null) return finish(null, 4401, "unauthorized");
        ws.send(JSON.stringify({ t: "ok", id }));
        finish(id);
      } catch {
        finish(null, 4401, "unauthorized");
      }
    };
    const timer = setTimeout(() => finish(null, 4408, "auth timeout"), AUTH_TIMEOUT_MS);
    ws.addEventListener("message", onMessage);
    ws.addEventListener("close", onClose);
    ws.send(message);
  });
}
