// Cloudflare Worker + Durable Object adapter. One DO instance ("global") holds all sockets, using the
// WebSocket Hibernation API; per-socket state is persisted with serializeAttachment so routing survives
// hibernation. Requires compatibility flag `nodejs_compat` (for node:crypto HMAC used by TURN minting).
/// <reference types="@cloudflare/workers-types" />
import { SignalingCore, type Conn, type ConnState, type CryptoProvider } from '../core/server.ts';
import { base32 } from '../protocol/encoding.ts';

interface Env {
  SIGNAL: DurableObjectNamespace;
  TURN_SECRET?: string;
  TURN_URLS?: string;
  STUN_URLS?: string;
}

const webCrypto: CryptoProvider = {
  random: (n) => crypto.getRandomValues(new Uint8Array(n)),
  async edVerify(pk, msg, sig) {
    const key = await crypto.subtle.importKey('raw', pk, { name: 'Ed25519' }, false, ['verify']);
    return crypto.subtle.verify({ name: 'Ed25519' }, key, sig, msg);
  },
  async deviceId(pk) {
    return base32(new Uint8Array(await crypto.subtle.digest('SHA-256', pk))).slice(0, 26);
  },
};

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);
    if (url.pathname === '/healthz') return Response.json({ ok: true });
    if (url.pathname !== '/signal' || req.headers.get('Upgrade') !== 'websocket') {
      return new Response('not found', { status: 404 });
    }
    return env.SIGNAL.get(env.SIGNAL.idFromName('global')).fetch(req);
  },
};

export class SignalRoom {
  private readonly core: SignalingCore;
  private readonly conns = new Map<WebSocket, Conn>();

  constructor(private readonly state: DurableObjectState, env: Env) {
    const list = (v?: string) => (v ? v.split(',').map((s) => s.trim()).filter(Boolean) : []);
    this.core = new SignalingCore({
      crypto: webCrypto,
      turn: {
        secret: env.TURN_SECRET ?? '',
        urls: list(env.TURN_URLS),
        stunUrls: list(env.STUN_URLS ?? 'stun:stun.cloudflare.com:3478'),
        ttlSeconds: 3600,
      },
    });
    // Rebuild routing after hibernation.
    for (const ws of state.getWebSockets()) {
      const st = ws.deserializeAttachment() as ConnState | null;
      if (st) this.core.restore(this.connFor(ws), st);
    }
  }

  private connFor(ws: WebSocket): Conn {
    let c = this.conns.get(ws);
    if (!c) {
      c = {
        ip: 'cf',
        send: (d) => {
          try {
            ws.send(d);
          } catch {
            /* socket already closed */
          }
        },
        close: (code, reason) => {
          try {
            ws.close(code, reason);
          } catch {
            /* already closed */
          }
        },
      };
      this.conns.set(ws, c);
    }
    return c;
  }

  async fetch(req: Request): Promise<Response> {
    const pair = new WebSocketPair();
    const [client, server] = [pair[0], pair[1]];
    this.state.acceptWebSocket(server);
    const conn = this.connFor(server);
    Object.defineProperty(conn, 'ip', { value: req.headers.get('CF-Connecting-IP') ?? 'unknown' });
    const st = this.core.onOpen(conn);
    if (st) server.serializeAttachment(st);
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    const conn = this.connFor(ws);
    if (typeof message !== 'string') return conn.close(4001, 'binary frames not allowed');
    this.core.tick();
    await this.core.onMessage(conn, message);
    const st = this.core.stateOf(conn);
    if (st) ws.serializeAttachment(st);
  }

  async webSocketClose(ws: WebSocket): Promise<void> {
    this.core.onClose(this.connFor(ws));
    this.conns.delete(ws);
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    await this.webSocketClose(ws);
  }
}
