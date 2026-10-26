import { describe, expect, it, vi } from "vitest";
import { authenticate } from "../src/auth";

describe("authenticate", () => {
  it("resolves null and leaves no timer or listeners when send throws", async () => {
    vi.useFakeTimers();
    try {
      const added: string[] = [];
      const removed: string[] = [];
      const close = vi.fn();
      const ws = {
        send: () => {
          throw new Error("socket closed");
        },
        close,
        addEventListener: (t: string) => void added.push(t),
        removeEventListener: (t: string) => void removed.push(t),
      } as unknown as WebSocket;

      expect(await authenticate(ws, "/v1/presence")).toBeNull();
      expect(vi.getTimerCount()).toBe(0);
      expect(removed.sort()).toEqual(added.sort());
      vi.advanceTimersByTime(20_000);
      expect(close).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });
});
