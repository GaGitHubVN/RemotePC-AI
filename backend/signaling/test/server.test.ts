import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import WebSocket from 'ws';
import { deviceId, edSign, generateDeviceKeys, type DeviceKeys } from '../src/protocol/crypto.ts';
import { b64u, utf8 } from '../src/protocol/encoding.ts';
import { envelopeKey, openEnvelope, sealEnvelope } from '../src/protocol/envelope.ts';
import { startNodeServer } from '../src/node/server.ts';
import { createHmac } from 'node:crypto';

type Srv = Awaited<ReturnType<typeof startNodeServer>>;
let srv: Srv;
let url: string;

beforeAll(async () => {
  srv = await startNodeServer({
    port: 0,
    host: '127.0.0.1',
    helloTimeoutMs: 300,
    handshakesPerIpPerMin: 1000,
    msgRatePerSec: 50,
    msgBurst: 60,
    turn: { secret: 'test-secret-0123456789abcdef0123456789', urls: ['turn:turn.example:3478'], stunUrls: ['stun:stun.example:3478'], ttlSeconds: 600 },
  });
  url = `ws://127.0.0.1:${srv.port}/signal`;
});
afterAll(() => srv.close());

class TestClient {
  ws: WebSocket;
  queue: any[] = [];
  waiters: ((m: any) => void)[] = [];
  closed: Promise<{ code: number; reason: string }>;
  constructor() {
    this.ws = new WebSocket(url);
    this.ws.on('message', (d) => {
      const m = JSON.parse(d.toString());
      const w = this.waiters.shift();
      if (w) w(m);
      else this.queue.push(m);
    });
    this.closed = new Promise((r) => this.ws.on('close', (code, reason) => r({ code, reason: reason.toString() })));
  }
  next(timeout = 2000): Promise<any> {
    const q = this.queue.shift();
    if (q) return Promise.resolve(q);
    return new Promise((res, rej) => {
      const t = setTimeout(() => rej(new Error('timeout')), timeout);
      this.waiters.push((m) => (clearTimeout(t), res(m)));
    });
  }
  send(m: unknown) {
    this.ws.send(typeof m === 'string' ? m : JSON.stringify(m));
  }
  static async auth(keys: DeviceKeys, role: 'host' | 'client'): Promise<TestClient> {
    const c = new TestClient();
    const ch = await c.next();
    expect(ch.t).toBe('challenge');
    const id = deviceId(keys.edPk);
    const sig = edSign(keys.edSk, utf8(`remotepc-signal-v1\n${ch.nonce}\n${id}\n${role}`));
    c.send({ t: 'hello', v: 1, role, id, edPk: b64u(keys.edPk), sig: b64u(sig) });
    const w = await c.next();
    expect(w).toMatchObject({ t: 'welcome', id });
    return c;
  }
}

describe('signaling server (integration over real WebSockets)', () => {
  it('health endpoint', async () => {
    const r = await fetch(`http://127.0.0.1:${srv.port}/healthz`);
    expect(await r.json()).toMatchObject({ ok: true });
  });

  it('authenticates and relays an end-to-end envelope the server cannot read', async () => {
    const hk = generateDeviceKeys();
    const ck = generateDeviceKeys();
    const host = await TestClient.auth(hk, 'host');
    const client = await TestClient.auth(ck, 'client');
    const hid = deviceId(hk.edPk);
    const cid = deviceId(ck.edPk);

    client.send({ t: 'presence', ids: [hid, 'a'.repeat(26)] });
    expect(await client.next()).toEqual({ t: 'presence', online: [hid] });

    const key = envelopeKey(ck.xSk, hk.xPk, cid, hid);
    const env = sealEnvelope(key, { typ: 'session.open', from: cid, to: hid, sid: '', seq: 1, ts: Date.now() }, { reqId: 'x' });
    client.send({ t: 'relay', to: hid, env });
    const got = await host.next();
    expect(got.t).toBe('relay');
    expect(got.from).toBe(cid);
    expect(openEnvelope(envelopeKey(hk.xSk, ck.xPk, hid, cid), got.env)).toEqual({ reqId: 'x' });
    host.ws.close();
    client.ws.close();
  });

  it('rejects spoofed sender in envelope', async () => {
    const a = await TestClient.auth(generateDeviceKeys(), 'client');
    const victim = generateDeviceKeys();
    const target = generateDeviceKeys();
    const t = await TestClient.auth(target, 'host');
    const env = { v: 1, typ: 'x', from: deviceId(victim.edPk), to: deviceId(target.edPk), sid: '', seq: 1, ts: Date.now(), n: 'AAAAAAAAAAAAAAAA', ct: 'A'.repeat(22) };
    a.send({ t: 'relay', to: deviceId(target.edPk), env });
    expect(await a.next()).toMatchObject({ t: 'error', code: 'bad_request' });
    await expect(t.next(300)).rejects.toThrow('timeout');
    a.ws.close();
    t.ws.close();
  });

  it('reports peer_offline', async () => {
    const k = generateDeviceKeys();
    const a = await TestClient.auth(k, 'client');
    const to = deviceId(generateDeviceKeys().edPk);
    a.send({ t: 'relay', to, env: { v: 1, typ: 'x', from: deviceId(k.edPk), to, sid: '', seq: 1, ts: 1, n: 'AAAAAAAAAAAAAAAA', ct: 'A'.repeat(22) } });
    expect(await a.next()).toMatchObject({ t: 'error', code: 'peer_offline' });
    a.ws.close();
  });

  it('rejects a bad signature', async () => {
    const k = generateDeviceKeys();
    const c = new TestClient();
    const ch = await c.next();
    const id = deviceId(k.edPk);
    const sig = edSign(k.edSk, utf8(`remotepc-signal-v1\n${ch.nonce}\n${id}\nclient`));
    c.send({ t: 'hello', v: 1, role: 'host', id, edPk: b64u(k.edPk), sig: b64u(sig) }); // role mismatch
    expect((await c.closed).code).toBe(4004);
  });

  it('rejects id not derived from key', async () => {
    const k = generateDeviceKeys();
    const c = new TestClient();
    const ch = await c.next();
    const id = 'b'.repeat(26);
    const sig = edSign(k.edSk, utf8(`remotepc-signal-v1\n${ch.nonce}\n${id}\nclient`));
    c.send({ t: 'hello', v: 1, role: 'client', id, edPk: b64u(k.edPk), sig: b64u(sig) });
    expect((await c.closed).code).toBe(4004);
  });

  it('closes sockets that never say hello', async () => {
    const c = new TestClient();
    await c.next();
    expect((await c.closed).code).toBe(4002);
  });

  it('a second login with the same identity replaces the first (no duplicate sessions)', async () => {
    const k = generateDeviceKeys();
    const first = await TestClient.auth(k, 'host');
    const second = await TestClient.auth(k, 'host');
    expect((await first.closed).code).toBe(4003);
    expect(srv.core.isOnline(deviceId(k.edPk))).toBe(true);
    second.ws.close();
  });

  it('mints coturn REST credentials', async () => {
    const k = generateDeviceKeys();
    const c = await TestClient.auth(k, 'client');
    c.send({ t: 'turn' });
    const m = await c.next();
    expect(m.ttl).toBe(600);
    const turn = m.iceServers.find((s: any) => s.username);
    expect(turn.username).toMatch(new RegExp(`^\\d+:${deviceId(k.edPk)}$`));
    const expected = createHmac('sha1', 'test-secret-0123456789abcdef0123456789').update(turn.username).digest('base64');
    expect(turn.credential).toBe(expected);
    c.ws.close();
  });

  it('rate limits floods', async () => {
    const c = await TestClient.auth(generateDeviceKeys(), 'client');
    for (let i = 0; i < 80; i++) c.send({ t: 'ping' });
    const msgs = [];
    for (let i = 0; i < 80; i++) msgs.push(await c.next());
    expect(msgs.some((m) => m.code === 'rate_limited')).toBe(true);
    c.ws.close();
  });

  it('rejects malformed messages', async () => {
    const c = await TestClient.auth(generateDeviceKeys(), 'client');
    c.send({ t: 'relay', to: 'not-an-id', env: {} });
    expect(await c.next()).toMatchObject({ t: 'error', code: 'bad_request' });
    c.send('{not json');
    expect((await c.closed).code).toBe(4001);
  });
});
