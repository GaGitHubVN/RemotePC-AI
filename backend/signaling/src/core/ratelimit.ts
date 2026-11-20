/** Classic token bucket. `now` is injectable for tests. */
export class TokenBucket {
  private tokens: number;
  private last: number;
  private readonly ratePerSec: number;
  private readonly burst: number;
  private readonly now: () => number;

  constructor(ratePerSec: number, burst: number, now: () => number = Date.now) {
    this.ratePerSec = ratePerSec;
    this.burst = burst;
    this.now = now;
    this.tokens = burst;
    this.last = now();
  }

  take(cost = 1): boolean {
    const t = this.now();
    this.tokens = Math.min(this.burst, this.tokens + ((t - this.last) / 1000) * this.ratePerSec);
    this.last = t;
    if (this.tokens < cost) return false;
    this.tokens -= cost;
    return true;
  }
}

/** Fixed-window counter keyed by e.g. IP address. Bounded in size. */
export class KeyedWindowLimiter {
  private readonly windows = new Map<string, { start: number; count: number }>();
  private readonly limit: number;
  private readonly windowMs: number;
  private readonly now: () => number;
  private readonly maxKeys: number;

  constructor(limit: number, windowMs: number, now: () => number = Date.now, maxKeys = 50_000) {
    this.limit = limit;
    this.windowMs = windowMs;
    this.now = now;
    this.maxKeys = maxKeys;
  }

  hit(key: string): boolean {
    const t = this.now();
    let w = this.windows.get(key);
    if (!w || t - w.start >= this.windowMs) {
      if (this.windows.size >= this.maxKeys) this.prune(t);
      w = { start: t, count: 0 };
      this.windows.set(key, w);
    }
    w.count++;
    return w.count <= this.limit;
  }

  private prune(t: number): void {
    for (const [k, w] of this.windows) if (t - w.start >= this.windowMs) this.windows.delete(k);
    if (this.windows.size >= this.maxKeys) this.windows.clear();
  }
}
