/** `?pair=` connects allowed per client network in PAIR_WINDOW_MS. Finding an open 4-digit lookup by scanning then needs many networks. */
export const PAIR_ATTEMPTS = 10;
export const PAIR_WINDOW_MS = 10 * 60_000;
const MAX_TRACKED = 10_000;

/**
 * Counts attempts per key in a sliding window. Held in memory: an evicted Durable Object starts empty, which only
 * ever loosens the limit for a moment.
 */
export class AttemptLimiter {
  private readonly hits = new Map<string, number[]>();

  constructor(
    private readonly max: number,
    private readonly windowMs: number,
  ) {}

  /** Records an attempt for `key`; false, without recording, once its budget for the window is used. */
  allow(key: string, now = Date.now()): boolean {
    const since = now - this.windowMs;
    const recent = (this.hits.get(key) ?? []).filter((t) => t > since);
    const allowed = recent.length < this.max;
    if (allowed) recent.push(now);
    this.hits.set(key, recent);
    if (this.hits.size > MAX_TRACKED) {
      for (const [k, times] of this.hits) if (!times.some((t) => t > since)) this.hits.delete(k);
    }
    return allowed;
  }
}

/** The client's network for rate limits: the IPv4 address, or the /64 prefix of an IPv6 address (one subscriber's usual allocation). */
export function clientNetwork(ip: string | null): string {
  if (!ip) return "";
  const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/i.exec(ip);
  if (mapped) return mapped[1];
  if (!ip.includes(":")) return ip;
  const [head, tail] = ip.toLowerCase().split("::", 2);
  const left = head ? head.split(":") : [];
  const right = tail === undefined ? [] : tail ? tail.split(":") : [];
  const groups = tail === undefined ? left : [...left, ...Array(Math.max(0, 8 - left.length - right.length)).fill("0"), ...right];
  return groups
    .slice(0, 4)
    .map((g) => parseInt(g, 16).toString(16))
    .join(":");
}
