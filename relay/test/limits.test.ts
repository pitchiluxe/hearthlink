import { describe, expect, it } from "vitest";
import { AttemptLimiter, clientNetwork, PAIR_ATTEMPTS } from "../src/limits";
import keyPem from "./fixtures/key.pem?raw";
import certPem from "./fixtures/cert.pem?raw";
import { closedWith, openAuthed, openRaw } from "./helpers";

const from = (ip: string) => ({ "CF-Connecting-IP": ip });

describe("pairing lookup limits", () => {
  it("limits pair connects per client address", async () => {
    for (let i = 0; i < PAIR_ATTEMPTS; i++) {
      const ws = await openAuthed("/v1/connect?pair=9999", keyPem, certPem, from("203.0.113.5"));
      expect((await closedWith(ws)).code).toBe(4404);
    }
    // Refused before the challenge: a blocked client learns nothing about lookups.
    const blocked = await openRaw("/v1/connect?pair=9999", from("203.0.113.5"));
    expect((await closedWith(blocked)).code).toBe(4429);
    const other = await openAuthed("/v1/connect?pair=9999", keyPem, certPem, from("198.51.100.7"));
    expect((await closedWith(other)).code).toBe(4404);
  });

  it("counts an IPv6 /64 as one client", async () => {
    for (let i = 0; i < PAIR_ATTEMPTS; i++) {
      const ws = await openAuthed("/v1/connect?pair=9999", keyPem, certPem, from(`2001:db8:0:7::${i + 1}`));
      expect((await closedWith(ws)).code).toBe(4404);
    }
    const blocked = await openRaw("/v1/connect?pair=9999", from("2001:0db8:0000:0007:ffff::9"));
    expect((await closedWith(blocked)).code).toBe(4429);
    const neighbour = await openAuthed("/v1/connect?pair=9999", keyPem, certPem, from("2001:db8:0:8::1"));
    expect((await closedWith(neighbour)).code).toBe(4404);
  });

  it("forgets attempts after the window", () => {
    const limiter = new AttemptLimiter(2, 1000);
    expect(limiter.allow("a", 0)).toBe(true);
    expect(limiter.allow("a", 10)).toBe(true);
    expect(limiter.allow("a", 20)).toBe(false);
    expect(limiter.allow("b", 20)).toBe(true);
    expect(limiter.allow("a", 1001)).toBe(true);
  });

  it("normalises client networks", () => {
    expect(clientNetwork("203.0.113.5")).toBe("203.0.113.5");
    expect(clientNetwork("2001:DB8::1")).toBe("2001:db8:0:0");
    expect(clientNetwork("2001:db8:0:0:1:2:3:4")).toBe("2001:db8:0:0");
    expect(clientNetwork("::ffff:203.0.113.5")).toBe("203.0.113.5");
    expect(clientNetwork(null)).toBe("");
  });
});
