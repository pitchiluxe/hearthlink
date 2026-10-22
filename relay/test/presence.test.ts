import { env, runInDurableObject } from "cloudflare:test";
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

  it("no auth within 10 s closes 4408", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    const fp = await fingerprintOfPem(certPem);
    const ws = await openRaw(`/v1/presence?id=${fp}`);
    await nextJson(ws);
    vi.setSystemTime(Date.now() + 11_000);
    await runInDurableObject(room(fp), (inst: HostRoom) => inst.alarm());
    expect((await closedWith(ws)).code).toBe(4408);
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
