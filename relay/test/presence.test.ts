import { env, runInDurableObject, SELF } from "cloudflare:test";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { HostRoom } from "../src/host-room";
import certPem from "./fixtures/cert.pem?raw";
import keyPem from "./fixtures/key.pem?raw";
import otherCertPem from "./fixtures/other-cert.pem?raw";
import otherKeyPem from "./fixtures/other-key.pem?raw";
import {
  answerChallenge,
  closedWith,
  fingerprintOfPem,
  nextJson,
  nextText,
  openAuthed,
  openRaw,
} from "./helpers";

const CONTROLLER = "C".repeat(64);
const STRANGER = "D".repeat(64);

async function openPresence() {
  const fp = await fingerprintOfPem(certPem);
  return { fp, ws: await openAuthed(`/v1/presence?id=${fp}`, keyPem, certPem) };
}
function room(hostId: string) {
  return env.HOSTS.get(env.HOSTS.idFromName(hostId));
}
function index() {
  return env.INDEX.get(env.INDEX.idFromName("index"));
}
async function settle() {
  await new Promise((r) => setTimeout(r, 50));
}

afterEach(() => {
  vi.useRealTimers();
});

describe("presence", () => {
  it("presence goes online for allowed caller", async () => {
    const { fp, ws } = await openPresence();
    ws.send(JSON.stringify({ t: "allow", ids: [CONTROLLER] }));
    await settle();
    expect(await room(fp).isOnlineFor(CONTROLLER)).toBe(true);
    expect(await room(fp).isOnlineFor(STRANGER)).toBe(false);
    ws.close();
  });

  it("presence with wrong id rejected", async () => {
    const other = await fingerprintOfPem(otherCertPem);
    const ws = await openRaw(`/v1/presence?id=${other}`);
    await answerChallenge(ws, await nextJson(ws), "/v1/presence", keyPem, certPem);
    expect((await closedWith(ws)).code).toBe(4401);
  });

  it("bad signature closes 4401", async () => {
    const fp = await fingerprintOfPem(certPem);
    const ws = await openRaw(`/v1/presence?id=${fp}`);
    await answerChallenge(ws, await nextJson(ws), "/v1/presence", otherKeyPem, certPem);
    expect((await closedWith(ws)).code).toBe(4401);
    expect(await room(fp).isOnlineFor(CONTROLLER)).toBe(false);
  });

  it("message before auth closes 4401", async () => {
    const fp = await fingerprintOfPem(certPem);
    const ws = await openRaw(`/v1/presence?id=${fp}`);
    await nextJson(ws);
    ws.send(JSON.stringify({ t: "allow", ids: [CONTROLLER] }));
    expect((await closedWith(ws)).code).toBe(4401);
  });

  it("replacement presence closes old socket", async () => {
    const { fp, ws: first } = await openPresence();
    const second = await openAuthed(`/v1/presence?id=${fp}`, keyPem, certPem);
    expect((await closedWith(first)).code).toBe(4409);
    second.send(JSON.stringify({ t: "allow", ids: [CONTROLLER] }));
    await settle();
    expect(await room(fp).isOnlineFor(CONTROLLER)).toBe(true);
    second.close();
  });

  it("stale presence reports offline", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    const { fp, ws } = await openPresence();
    ws.send(JSON.stringify({ t: "allow", ids: [CONTROLLER] }));
    await settle();
    expect(await room(fp).isOnlineFor(CONTROLLER)).toBe(true);
    vi.setSystemTime(Date.now() + 61_000);
    expect(await room(fp).isOnlineFor(CONTROLLER)).toBe(false);
    ws.send("ping");
    expect(await nextText(ws)).toBe("pong");
    ws.close();
  });

  it("a ping revives a stale presence and it goes stale 60 s later", async () => {
    // The runtime stamps auto-responses with the real clock, so authenticate in the (faked) past,
    // then return to real time: the socket is stale until a real ping arrives.
    vi.useFakeTimers({ toFake: ["Date"] });
    const real = Date.now();
    vi.setSystemTime(real - 100_000);
    const { fp, ws } = await openPresence();
    ws.send(JSON.stringify({ t: "allow", ids: [CONTROLLER] }));
    await settle();
    vi.setSystemTime(real);
    expect(await room(fp).isOnlineFor(CONTROLLER)).toBe(false);
    ws.send("ping");
    expect(await nextText(ws)).toBe("pong");
    expect(await room(fp).isOnlineFor(CONTROLLER)).toBe(true);
    vi.setSystemTime(real + 55_000);
    expect(await room(fp).isOnlineFor(CONTROLLER)).toBe(true);
    vi.setSystemTime(real + 65_000);
    expect(await room(fp).isOnlineFor(CONTROLLER)).toBe(false);
    ws.close();
  });

  it("replacement waits for the new socket to authenticate", async () => {
    const { fp, ws: first } = await openPresence();
    first.send(JSON.stringify({ t: "allow", ids: [CONTROLLER] }));
    const second = await openRaw(`/v1/presence?id=${fp}`);
    const challenge = await nextJson(second);
    await settle();
    expect(first.readyState).toBe(WebSocket.READY_STATE_OPEN);
    expect(await room(fp).isOnlineFor(CONTROLLER)).toBe(true);
    await answerChallenge(second, challenge, "/v1/presence", keyPem, certPem);
    expect((await nextJson(second)).t).toBe("ok");
    expect((await closedWith(first)).code).toBe(4409);
    second.close();
  });

  it("schedules an auth alarm at the deadline that closes with 4408", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    const fp = await fingerprintOfPem(certPem);
    const start = Date.now();
    const ws = await openRaw(`/v1/presence?id=${fp}`);
    await nextJson(ws);
    const alarm = await runInDurableObject(room(fp), (_i: HostRoom, state) => state.storage.getAlarm());
    expect(alarm).not.toBeNull();
    expect(Math.abs((alarm as number) - (start + 10_000))).toBeLessThan(2_000);
    vi.setSystemTime(start + 11_000);
    // Fire the alarm the way the runtime does: the pending alarm is consumed first.
    await runInDurableObject(room(fp), async (inst: HostRoom, state) => {
      await state.storage.deleteAlarm();
      await inst.alarm();
    });
    expect((await closedWith(ws)).code).toBe(4408);
    // The expired socket must not re-arm an alarm in the past.
    const after = await runInDurableObject(room(fp), (_i: HostRoom, state) => state.storage.getAlarm());
    expect(after).toBeNull();
  });

  it("binary and malformed frames from an authenticated host close 4400", async () => {
    const { ws } = await openPresence();
    ws.send(new Uint8Array([1, 2, 3]));
    expect((await closedWith(ws)).code).toBe(4400);
    const { ws: ws2 } = await openPresence();
    ws2.send("{not json");
    expect((await closedWith(ws2)).code).toBe(4400);
  });

  it("malformed lookup gets pairing-taken", async () => {
    const { ws } = await openPresence();
    for (const lookup of ["123", "12345", "12a4", "", 1234, "１２３４"]) {
      ws.send(JSON.stringify({ t: "pairing", lookup }));
      expect(await nextJson(ws)).toEqual({ t: "pairing-taken" });
    }
    ws.close();
  });

  it("lookup held by one host is taken for another and re-claimable by its owner", async () => {
    const { ws: a } = await openPresence();
    const otherId = await fingerprintOfPem(otherCertPem);
    const b = await openAuthed(`/v1/presence?id=${otherId}`, otherKeyPem, otherCertPem);
    a.send(JSON.stringify({ t: "pairing", lookup: "7777" }));
    expect(await nextJson(a)).toEqual({ t: "pairing-ok" });
    b.send(JSON.stringify({ t: "pairing", lookup: "7777" }));
    expect(await nextJson(b)).toEqual({ t: "pairing-taken" });
    a.send(JSON.stringify({ t: "pairing", lookup: "7777" }));
    expect(await nextJson(a)).toEqual({ t: "pairing-ok" });
    a.close();
    b.close();
  });

  it("rejects a malformed id with 400 and no upgrade", async () => {
    for (const id of ["", "abc", "a".repeat(64), "G".repeat(64), "A".repeat(63), "A".repeat(65)]) {
      const res = await SELF.fetch(`https://relay.test/v1/presence?id=${id}`, {
        headers: { Upgrade: "websocket" },
      });
      expect(res.status).toBe(400);
      expect(res.webSocket).toBeNull();
    }
    const res = await SELF.fetch("https://relay.test/v1/presence", { headers: { Upgrade: "websocket" } });
    expect(res.status).toBe(400);
  });

  it("pairing lookup registered", async () => {
    const { fp, ws } = await openPresence();
    ws.send(JSON.stringify({ t: "pairing", lookup: "1234" }));
    expect(await nextJson(ws)).toEqual({ t: "pairing-ok" });
    expect(await index().resolve("1234")).toBe(fp);
    ws.close();
  });

  it("taken lookup rejected", async () => {
    const { ws } = await openPresence();
    ws.send(JSON.stringify({ t: "pairing", lookup: "1234" }));
    expect(await nextJson(ws)).toEqual({ t: "pairing-ok" });
    const otherId = await fingerprintOfPem(otherCertPem);
    const ws2 = await openAuthed(`/v1/presence?id=${otherId}`, otherKeyPem, otherCertPem);
    ws2.send(JSON.stringify({ t: "pairing", lookup: "1234" }));
    expect(await nextJson(ws2)).toEqual({ t: "pairing-taken" });
    ws.close();
    ws2.close();
  });

  it("lookup expires", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    const { ws } = await openPresence();
    ws.send(JSON.stringify({ t: "pairing", lookup: "4321" }));
    expect(await nextJson(ws)).toEqual({ t: "pairing-ok" });
    expect(await index().resolve("4321")).not.toBeNull();
    vi.setSystemTime(Date.now() + 121_000);
    expect(await index().resolve("4321")).toBeNull();
    ws.close();
  });

  it("pairing-close releases the lookup", async () => {
    const { fp, ws } = await openPresence();
    ws.send(JSON.stringify({ t: "pairing", lookup: "5555" }));
    await nextJson(ws);
    expect(await room(fp).hasLookup("5555")).toBe(true);
    ws.send(JSON.stringify({ t: "pairing-close" }));
    await settle();
    expect(await room(fp).hasLookup("5555")).toBe(false);
    expect(await index().resolve("5555")).toBeNull();
    ws.close();
  });
});
