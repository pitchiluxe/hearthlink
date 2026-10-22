import { DurableObject } from "cloudflare:workers";

interface Entry {
  host: string;
  until: number;
}

/** Maps short pairing lookup codes to the host that registered them. A single global instance. */
export class PairingIndex extends DurableObject {
  private get(lookup: string): Entry | null {
    const entry = this.ctx.storage.kv.get<Entry>(lookup);
    if (!entry) return null;
    if (entry.until <= Date.now()) {
      this.ctx.storage.kv.delete(lookup);
      return null;
    }
    return entry;
  }

  /** Claims `lookup` for `host` until the given epoch ms. False if another host holds it. */
  async claim(lookup: string, host: string, until: number): Promise<boolean> {
    const existing = this.get(lookup);
    if (existing && existing.host !== host) return false;
    this.ctx.storage.kv.put<Entry>(lookup, { host, until });
    return true;
  }

  async resolve(lookup: string): Promise<string | null> {
    return this.get(lookup)?.host ?? null;
  }

  async release(lookup: string, host: string): Promise<void> {
    const existing = this.ctx.storage.kv.get<Entry>(lookup);
    if (existing && existing.host === host) this.ctx.storage.kv.delete(lookup);
  }
}
