import { env, runInDurableObject, SELF } from "cloudflare:test";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { HostRoom } from "../src/host-room";
import certPem from "./fixtures/cert.pem?raw";
import keyPem from "./fixtures/key.pem?raw";
import otherCertPem from "./fixtures/other-cert.pem?raw";
import otherKeyPem from "./fixtures/other-key.pem?raw";
import { closedWith, fingerprintOfPem, nextJson, openAuthed, openRaw } from "./helpers";

const settle = () => new Promise((r) => setTimeout(r, 50));

afterEach(() => {
  vi.useRealTimers();
});

/** Collects binary messages of a socket opened by openAuthed. */
function binaryInbox(ws: WebSocket) {
  const queue: Uint8Array[] = [];
  const waiters: ((b: Uint8Array) => void)[] = [];
  ws.binaryType = "arraybuffer";
  ws.addEventListener("message", (ev) => {
    if (typeof ev.data === "string") return;
    const bytes = new Uint8Array(ev.data as ArrayBuffer);
    const w = waiters.shift();
    if (w) w(bytes);
    else queue.push(bytes);
  });
  return () => {
    const q = queue.shift();
    return q ? Promise.resolve(q) : new Promise<Uint8Array>((r) => waiters.push(r));
  };
}

async function hostOnline(allow: string[]) {
  const hostFp = await fingerprintOfPem(certPem);
  const presence = await openAuthed(`/v1/presence?id=${hostFp}`, keyPem, certPem);
  presence.send(JSON.stringify({ t: "allow", ids: allow }));
  await settle();
  return { hostFp, presence };
}

const controllerFp = () => fingerprintOfPem(otherCertPem);

function payload(n: number, seed: number) {
  const b = new Uint8Array(n);
  for (let i = 0; i < n; i++) b[i] = (i * 31 + seed) & 0xff;
  return b;
}

/** Controller connects, host accepts; returns both piped sockets. */
async function pipeUp(hostFp: string, presence: WebSocket, query: string) {
  const controller = await openAuthed(`/v1/connect?${query}`, otherKeyPem, otherCertPem);
  const incoming = await nextJson(presence);
  const host = await openAuthed(`/v1/accept?host=${hostFp}&session=${incoming.session}`, keyPem, certPem);
  expect(await nextJson(controller)).toEqual({ t: "ready" });
  return { controller, host, incoming };
}

describe("session", () => {
  it("allowed controller connects and bytes flow both ways", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([cfp]);
    const controller = await openAuthed(`/v1/connect?to=${hostFp}`, otherKeyPem, otherCertPem);
    const incoming = await nextJson(presence);
    expect(incoming.t).toBe("incoming");
    expect(incoming.from).toBe(cfp);
    expect(incoming.session).toMatch(/^[0-9a-f]{32}$/);
    const host = await openAuthed(`/v1/accept?host=${hostFp}&session=${incoming.session}`, keyPem, certPem);
    expect(await nextJson(controller)).toEqual({ t: "ready" });

    const fromHost = binaryInbox(controller);
    const fromController = binaryInbox(host);
    const down = payload(65_536, 7);
    const up = payload(65_536, 99);
    host.send(down);
    controller.send(up);
    expect(await fromHost()).toEqual(down);
    expect(await fromController()).toEqual(up);
    controller.close();
    presence.close();
  });

  it("stranger connect refused", async () => {
    const { hostFp, presence } = await hostOnline(["E".repeat(64)]);
    const controller = await openAuthed(`/v1/connect?to=${hostFp}`, otherKeyPem, otherCertPem);
    expect((await closedWith(controller)).code).toBe(4403);
    presence.close();
  });

  it("offline host", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([cfp]);
    presence.close();
    await settle();
    const controller = await openAuthed(`/v1/connect?to=${hostFp}`, otherKeyPem, otherCertPem);
    expect((await closedWith(controller)).code).toBe(4404);
  });

  it("stranger gets 4403 whether the host is offline or online", async () => {
    const { hostFp, presence } = await hostOnline(["E".repeat(64)]);
    const online = await openAuthed(`/v1/connect?to=${hostFp}`, otherKeyPem, otherCertPem);
    expect((await closedWith(online)).code).toBe(4403);
    presence.close();
    await settle();
    const offline = await openAuthed(`/v1/connect?to=${hostFp}`, otherKeyPem, otherCertPem);
    expect((await closedWith(offline)).code).toBe(4403);
    const never = await openAuthed(`/v1/connect?to=${"9".repeat(64)}`, otherKeyPem, otherCertPem);
    expect((await closedWith(never)).code).toBe(4403);
  });

  it("host never accepts", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([cfp]);
    // Timers inside the Durable Object are not controlled by vitest fake timers, so shorten the wait on the instance.
    await runInDurableObject(env.HOSTS.get(env.HOSTS.idFromName(hostFp)), (room: HostRoom) => {
      expect(room.acceptTimeoutMs).toBe(15_000);
      room.acceptTimeoutMs = 200;
    });
    const controller = await openAuthed(`/v1/connect?to=${hostFp}`, otherKeyPem, otherCertPem);
    const incoming = await nextJson(presence);
    expect((await closedWith(controller)).code).toBe(4408);
    // The session id is gone once it timed out.
    const late = await openAuthed(`/v1/accept?host=${hostFp}&session=${incoming.session}`, keyPem, certPem);
    expect((await closedWith(late)).code).toBe(4404);
    presence.close();
  });

  it("pairing connect by lookup", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([]);
    presence.send(JSON.stringify({ t: "pairing", lookup: "1234" }));
    expect(await nextJson(presence)).toEqual({ t: "pairing-ok" });
    const { controller, host, incoming } = await pipeUp(hostFp, presence, "pair=1234");
    expect(incoming.from).toBe(cfp);
    const fromHost = binaryInbox(controller);
    host.send(new Uint8Array([1, 2, 3]));
    expect(await fromHost()).toEqual(new Uint8Array([1, 2, 3]));
    controller.close();
    presence.close();
  });

  it("unknown lookup closes 4404 only after authentication", async () => {
    const controller = await openAuthed("/v1/connect?pair=9999", otherKeyPem, otherCertPem);
    expect((await closedWith(controller)).code).toBe(4404);
    const raw = await openRaw("/v1/connect?pair=9998");
    expect(raw.readyState).toBe(WebSocket.READY_STATE_OPEN);
    raw.close();
  });

  it("rejects malformed parameters with 400 before upgrading", async () => {
    const up = { headers: { Upgrade: "websocket" } };
    const ok = "A".repeat(64);
    for (const path of [
      "/v1/connect",
      "/v1/connect?to=abc",
      "/v1/connect?pair=12",
      `/v1/connect?to=${ok}&pair=1234`,
      `/v1/accept?host=${ok}`,
      `/v1/accept?host=${ok}&session=XYZ`,
      `/v1/accept?host=abc&session=${"a".repeat(32)}`,
    ]) {
      expect((await SELF.fetch(`https://relay.test${path}`, up)).status, path).toBe(400);
    }
  });

  it("accept for someone else's session refused", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([cfp]);
    const controller = await openAuthed(`/v1/connect?to=${hostFp}`, otherKeyPem, otherCertPem);
    const incoming = await nextJson(presence);
    const thief = await openAuthed(`/v1/accept?host=${hostFp}&session=${incoming.session}`, otherKeyPem, otherCertPem);
    expect((await closedWith(thief)).code).toBe(4401);
    // The session is still available to the real host.
    const host = await openAuthed(`/v1/accept?host=${hostFp}&session=${incoming.session}`, keyPem, certPem);
    expect(await nextJson(controller)).toEqual({ t: "ready" });
    host.close();
    presence.close();
  });

  it("a session id is consumed by the first accept", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([cfp]);
    const { incoming, controller } = await pipeUp(hostFp, presence, `to=${hostFp}`);
    const again = await openAuthed(`/v1/accept?host=${hostFp}&session=${incoming.session}`, keyPem, certPem);
    expect((await closedWith(again)).code).toBe(4404);
    controller.close();
    presence.close();
  });

  it("closing one side closes the other", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([cfp]);
    const { controller, host } = await pipeUp(hostFp, presence, `to=${hostFp}`);
    controller.close();
    expect((await closedWith(host)).code).toBe(1000);
    presence.close();
  });

  it("controller closing while waiting removes the pending session", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([cfp]);
    const controller = await openAuthed(`/v1/connect?to=${hostFp}`, otherKeyPem, otherCertPem);
    const incoming = await nextJson(presence);
    controller.close();
    await settle();
    const host = await openAuthed(`/v1/accept?host=${hostFp}&session=${incoming.session}`, keyPem, certPem);
    expect((await closedWith(host)).code).toBe(4404);
    presence.close();
  });

  it("text frame on a piped socket closes both with 4400", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([cfp]);
    const { controller, host } = await pipeUp(hostFp, presence, `to=${hostFp}`);
    host.send("hello");
    expect((await closedWith(host)).code).toBe(4400);
    expect((await closedWith(controller)).code).toBe(4400);
    presence.close();
  });

  it("second session gets busy message", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([cfp]);
    const first = await pipeUp(hostFp, presence, `to=${hostFp}`);
    const second = await pipeUp(hostFp, presence, `to=${hostFp}`);
    expect(second.incoming.session).not.toBe(first.incoming.session);
    const atFirstHost = binaryInbox(first.host);
    const atSecondHost = binaryInbox(second.host);
    first.controller.send(new Uint8Array([1]));
    second.controller.send(new Uint8Array([2]));
    expect(await atFirstHost()).toEqual(new Uint8Array([1]));
    expect(await atSecondHost()).toEqual(new Uint8Array([2]));
    first.controller.close();
    second.controller.close();
    presence.close();
  });

  it("status lists only hosts that allow the caller", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([cfp]);
    // The second online host is the other identity, which does not allow the caller (itself).
    const otherPresence = await openAuthed(`/v1/presence?id=${cfp}`, otherKeyPem, otherCertPem);
    otherPresence.send(JSON.stringify({ t: "allow", ids: ["F".repeat(64)] }));
    await settle();
    const ws = await openAuthed("/v1/status", otherKeyPem, otherCertPem);
    ws.send(JSON.stringify({ t: "query", ids: [hostFp, cfp, "B".repeat(64)] }));
    expect(await nextJson(ws)).toEqual({ t: "status", online: [hostFp] });
    expect((await closedWith(ws)).code).toBe(1000);
    presence.close();
    otherPresence.close();
  });

  it("status ignores IDs beyond the first 64", async () => {
    const cfp = await controllerFp();
    const { hostFp, presence } = await hostOnline([cfp]);
    const ids = Array.from({ length: 64 }, (_, i) => i.toString(16).toUpperCase().padStart(64, "0"));
    const ws = await openAuthed("/v1/status", otherKeyPem, otherCertPem);
    ws.send(JSON.stringify({ t: "query", ids: [...ids, hostFp] }));
    expect(await nextJson(ws)).toEqual({ t: "status", online: [] });
    presence.close();
  });
});
