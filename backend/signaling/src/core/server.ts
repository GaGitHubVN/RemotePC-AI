// Transport-agnostic signaling core (PROTOCOL.md §2). Adapters feed it socket events.
import { b64u, fromB64u, utf8 } from '../protocol/encoding.ts';
import { ClientMessageSchema, HelloSchema } from './schema.ts';
import { KeyedWindowLimiter, TokenBucket } from './ratelimit.ts';
import { mintTurnCredentials, type TurnConfig } from './turn.ts';

export interface Conn {
  send(data: string): void;
  close(code: number, reason: string): void;
  readonly ip: string;
}

export interface CryptoProvider {
  random(n: number): Uint8Array;
  edVerify(pk: Uint8Array, msg: Uint8Array, sig: Uint8Array): boolean | Promise<boolean>;
  deviceId(edPk: Uint8Array): string | Promise<string>;
}

export interface Logger {
  info(event: string, fields?: Record<string, unknown>): void;
  warn(event: string, fields?: Record<string, unknown>): void;
}

export interface CoreOptions {
  crypto: CryptoProvider;
  turn?: TurnConfig;
  log?: Logger;
  now?: () => number;
  helloTimeoutMs?: number;
  msgRatePerSec?: number;
  msgBurst?: number;
  handshakesPerIpPerMin?: number;
  maxFrameBytes?: number;
}

/** Serializable per-socket state (lets the Durable Object adapter survive hibernation). */
export interface ConnState {
  nonce: string;
  openedAt: number;
  id?: string;
  role?: 'host' | 'client';
}

export const CloseCode = {
  ProtocolError: 4001,
  HelloTimeout: 4002,
  Replaced: 4003,
  AuthFailed: 4004,
  RateLimited: 4005,
} as const;

const silent: Logger = { info() {}, warn() {} };

export class SignalingCore {
  private readonly states = new Map<Conn, ConnState>();
  private readonly buckets = new Map<Conn, TokenBucket>();
  private readonly byId = new Map<string, Conn>();
  private readonly ipLimiter: KeyedWindowLimiter;
  private readonly now: () => number;
  private readonly log: Logger;
  private readonly helloTimeoutMs: number;
  private readonly maxFrameBytes: number;

  private readonly opts: CoreOptions;

  constructor(opts: CoreOptions) {
    this.opts = opts;
    this.now = opts.now ?? Date.now;
    this.log = opts.log ?? silent;
    this.helloTimeoutMs = opts.helloTimeoutMs ?? 10_000;
    this.maxFrameBytes = opts.maxFrameBytes ?? 65_536;
    this.ipLimiter = new KeyedWindowLimiter(opts.handshakesPerIpPerMin ?? 20, 60_000, this.now);
  }

  get connectionCount(): number {
    return this.states.size;
  }

  isOnline(id: string): boolean {
    return this.byId.has(id);
  }

  onOpen(conn: Conn): ConnState | undefined {
    if (!this.ipLimiter.hit(conn.ip)) {
      this.log.warn('handshake_rate_limited', { ip: conn.ip });
      conn.close(CloseCode.RateLimited, 'rate limited');
      return undefined;
    }
    const st: ConnState = { nonce: b64u(this.opts.crypto.random(32)), openedAt: this.now() };
    this.track(conn, st);
    conn.send(JSON.stringify({ t: 'challenge', v: 1, nonce: st.nonce, time: this.now() }));
    return st;
  }

  /** Re-attach a socket after Durable Object hibernation. */
  restore(conn: Conn, st: ConnState): void {
    this.track(conn, st);
    if (st.id && !this.byId.has(st.id)) this.byId.set(st.id, conn);
  }

  stateOf(conn: Conn): ConnState | undefined {
    return this.states.get(conn);
  }

  private track(conn: Conn, st: ConnState): void {
    this.states.set(conn, st);
    this.buckets.set(conn, new TokenBucket(this.opts.msgRatePerSec ?? 40, this.opts.msgBurst ?? 120, this.now));
  }

  onClose(conn: Conn): void {
    const st = this.states.get(conn);
    this.states.delete(conn);
    this.buckets.delete(conn);
    if (st?.id && this.byId.get(st.id) === conn) {
      this.byId.delete(st.id);
      this.log.info('device_offline', { id: st.id, role: st.role });
    }
  }

  /** Close sockets that never authenticated. Adapters call this periodically. */
  tick(): void {
    const t = this.now();
    for (const [conn, st] of this.states) {
      if (!st.id && t - st.openedAt > this.helloTimeoutMs) {
        conn.close(CloseCode.HelloTimeout, 'hello timeout');
        this.onClose(conn);
      }
    }
  }

  async onMessage(conn: Conn, data: string): Promise<void> {
    const st = this.states.get(conn);
    if (!st) return;
    if (data.length > this.maxFrameBytes) {
      this.err(conn, 'too_large');
      return;
    }
    if (!this.buckets.get(conn)!.take()) {
      this.err(conn, 'rate_limited');
      return;
    }
    let msg: unknown;
    try {
      msg = JSON.parse(data);
    } catch {
      return this.fail(conn, CloseCode.ProtocolError, 'invalid json');
    }
    if (!st.id) return this.handleHello(conn, st, msg);

    const parsed = ClientMessageSchema.safeParse(msg);
    if (!parsed.success) {
      this.err(conn, 'bad_request');
      return;
    }
    const m = parsed.data;
    switch (m.t) {
      case 'ping':
        conn.send(JSON.stringify({ t: 'pong', time: this.now() }));
        return;
      case 'presence':
        conn.send(JSON.stringify({ t: 'presence', online: m.ids.filter((id) => this.byId.has(id)) }));
        return;
      case 'turn':
        if (!this.opts.turn) {
          conn.send(JSON.stringify({ t: 'turn', iceServers: [], ttl: 0 }));
          return;
        }
        conn.send(JSON.stringify({ t: 'turn', ...mintTurnCredentials(this.opts.turn, st.id, this.now()) }));
        return;
      case 'relay': {
        if (m.env.from !== st.id || m.env.to !== m.to) {
          this.err(conn, 'bad_request', m.env.n, 'envelope addressing mismatch');
          return;
        }
        const target = this.byId.get(m.to);
        if (!target) {
          this.err(conn, 'peer_offline', m.env.n);
          return;
        }
        target.send(JSON.stringify({ t: 'relay', from: st.id, env: m.env }));
        return;
      }
    }
  }

  private async handleHello(conn: Conn, st: ConnState, msg: unknown): Promise<void> {
    const parsed = HelloSchema.safeParse(msg);
    if (!parsed.success) return this.fail(conn, CloseCode.ProtocolError, 'expected hello');
    const h = parsed.data;
    let ok = false;
    try {
      const pk = fromB64u(h.edPk);
      const expectedId = await this.opts.crypto.deviceId(pk);
      const signed = utf8(`remotepc-signal-v1\n${st.nonce}\n${h.id}\n${h.role}`);
      ok = expectedId === h.id && (await this.opts.crypto.edVerify(pk, signed, fromB64u(h.sig)));
    } catch {
      ok = false;
    }
    if (!this.states.has(conn)) return; // closed while verifying
    if (!ok) {
      this.log.warn('auth_failed', { ip: conn.ip });
      return this.fail(conn, CloseCode.AuthFailed, 'authentication failed');
    }
    const prev = this.byId.get(h.id);
    if (prev && prev !== conn) {
      prev.close(CloseCode.Replaced, 'replaced by newer connection');
      this.onClose(prev);
    }
    st.id = h.id;
    st.role = h.role;
    this.byId.set(h.id, conn);
    this.log.info('device_online', { id: h.id, role: h.role });
    conn.send(JSON.stringify({ t: 'welcome', v: 1, id: h.id, time: this.now() }));
  }

  private err(conn: Conn, code: string, ref?: string, msg?: string): void {
    conn.send(JSON.stringify({ t: 'error', code, ...(ref ? { ref } : {}), ...(msg ? { msg } : {}) }));
  }

  private fail(conn: Conn, code: number, reason: string): void {
    conn.close(code, reason);
    this.onClose(conn);
  }
}
