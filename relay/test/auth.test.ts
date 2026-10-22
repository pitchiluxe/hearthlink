import { describe, expect, it } from "vitest";
import { fingerprintOf, verifyAuth } from "../src/auth";
import certPem from "./fixtures/cert.pem?raw";
import keyPem from "./fixtures/key.pem?raw";
import otherKeyPem from "./fixtures/other-key.pem?raw";

const pemBody = (pem: string) => pem.replace(/-----[^-]+-----/g, "").replace(/\s+/g, "");
const b64ToBytes = (b64: string) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
const bytesToB64 = (b: Uint8Array) => btoa(String.fromCharCode(...b));
const hex = (b: ArrayBuffer) =>
  [...new Uint8Array(b)].map((x) => x.toString(16).padStart(2, "0")).join("").toUpperCase();

const certB64 = pemBody(certPem);
const certDer = b64ToBytes(certB64);

async function sign(keyPemText: string, endpoint: string, nonce: Uint8Array): Promise<string> {
  const key = await crypto.subtle.importKey(
    "pkcs8",
    b64ToBytes(pemBody(keyPemText)),
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

describe("relay auth", () => {
  const nonce = crypto.getRandomValues(new Uint8Array(32));

  it("fingerprint matches .NET definition", async () => {
    const expected = hex(await crypto.subtle.digest("SHA-256", certDer));
    expect(expected).toHaveLength(64);
    expect(await fingerprintOf(certDer)).toBe(expected);
  });

  it("valid signature accepted", async () => {
    const sig = await sign(keyPem, "/v1/presence", nonce);
    expect(await verifyAuth("/v1/presence", nonce, certB64, sig)).toBe(await fingerprintOf(certDer));
  });

  it("signature for another endpoint rejected", async () => {
    const sig = await sign(keyPem, "/v1/status", nonce);
    expect(await verifyAuth("/v1/presence", nonce, certB64, sig)).toBeNull();
  });

  it("signature by other key rejected", async () => {
    const sig = await sign(otherKeyPem, "/v1/presence", nonce);
    expect(await verifyAuth("/v1/presence", nonce, certB64, sig)).toBeNull();
  });

  it("malformed certificate rejected", async () => {
    const junk = bytesToB64(crypto.getRandomValues(new Uint8Array(200)));
    const sig = await sign(keyPem, "/v1/presence", nonce);
    expect(await verifyAuth("/v1/presence", nonce, junk, sig)).toBeNull();
  });
});
