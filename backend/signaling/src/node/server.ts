// Node adapter: HTTP health endpoint + WebSocket signaling on /signal.
import { createServer, type IncomingMessage, type Server } from 'node:http';
import { WebSocketServer, type WebSocket } from 'ws';
import { deviceId, edVerify, random } from '../protocol/crypto.ts';
import { SignalingCore, type Conn, type CoreOptions, type Logger } from '../core/server.ts';

export interface NodeServerOptions extends Omit<CoreOptions, 'crypto'> {
  port: number;
  host?: string;
  /** Trust X-Forwarded-For (only behind your own reverse proxy). */
  trustProxy?: boolean;
}

export const nodeCrypto = { random, edVerify, deviceId };

export function jsonLogger(): Logger {
  const out = (level: string) => (event: string, fields?: Record<string, unknown>) =>
    process.stdout.write(JSON.stringify({ ts: new Date().toISOString(), level, event, ...fields }) + '\n');
  return { info: out('info'), warn: out('warn') };
}

function clientIp(req: IncomingMessage, trustProxy: boolean): string {
  if (trustProxy) {
    const xff = req.headers['x-forwarded-for'];
    const first = (Array.isArray(xff) ? xff[0] : xff)?.split(',')[0]?.trim();
    if (first) return first;
  }
  return req.socket.remoteAddress ?? 'unknown';
}

export async function startNodeServer(opts: NodeServerOptions): Promise<{ server: Server; core: SignalingCore; port: number; close(): Promise<void> }> {
  const core = new SignalingCore({ ...opts, crypto: nodeCrypto });
  const server = createServer((req, res) => {
    if (req.url === '/healthz') {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ ok: true, connections: core.connectionCount }));
      return;
    }
    res.writeHead(404).end();
  });
  const wss = new WebSocketServer({ noServer: true, maxPayload: opts.maxFrameBytes ?? 65_536 });

  server.on('upgrade', (req, socket, head) => {
    if (req.url !== '/signal') {
      socket.destroy();
      return;
    }
    wss.handleUpgrade(req, socket, head, (ws) => attach(ws, clientIp(req, !!opts.trustProxy)));
  });

  function attach(ws: WebSocket, ip: string): void {
    const conn: Conn = {
      ip,
      send: (d) => ws.readyState === ws.OPEN && ws.send(d),
      close: (code, reason) => ws.close(code, reason),
    };
    let alive = true;
    ws.on('pong', () => (alive = true));
    const hb = setInterval(() => {
      if (!alive) return ws.terminate();
      alive = false;
      ws.ping();
    }, 20_000);
    ws.on('message', (data, isBinary) => {
      if (isBinary) return conn.close(4001, 'binary frames not allowed');
      void core.onMessage(conn, data.toString());
    });
    ws.on('close', () => {
      clearInterval(hb);
      core.onClose(conn);
    });
    ws.on('error', () => ws.terminate());
    core.onOpen(conn);
  }

  const ticker = setInterval(() => core.tick(), 2_000);
  await new Promise<void>((resolve) => server.listen(opts.port, opts.host ?? '0.0.0.0', resolve));
  const addr = server.address();
  const port = typeof addr === 'object' && addr ? addr.port : opts.port;
  return {
    server,
    core,
    port,
    close: () =>
      new Promise<void>((resolve) => {
        clearInterval(ticker);
        for (const c of wss.clients) c.terminate();
        wss.close();
        server.close(() => resolve());
      }),
  };
}
