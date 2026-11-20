import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { deviceId, edSign, edVerify, generateDeviceKeys, keysFromSeeds } from '../src/protocol/crypto.ts';
import { base32, b64u, fromB64u, utf8 } from '../src/protocol/encoding.ts';
import {
  envelopeKey,
  normalizePairCode,
  openEnvelope,
  pairProof,
  pairTranscript,
  ReplayGuard,
  sealEnvelope,
  verifyCode,
  type Envelope,
} from '../src/protocol/envelope.ts';

const vectors = JSON.parse(
  readFileSync(new URL('../../../packages/protocol/vectors/v1.json', import.meta.url), 'utf8'),
);

describe('encoding', () => {
  it('base32 matches RFC 4648 test vectors (lowercase, no padding)', () => {
    const cases: [string, string][] = [
      ['', ''], ['f', 'my'], ['fo', 'mzxq'], ['foo', 'mzxw6'], ['foob', 'mzxw6yq'], ['fooba', 'mzxw6ytb'], ['foobar', 'mzxw6ytboi'],
    ];
    for (const [i, o] of cases) expect(base32(utf8(i))).toBe(o);
  });
  it('rejects non-base64url input', () => {
    expect(() => fromB64u('ab+/')).toThrow();
  });
});

describe('identity', () => {
  it('derives keys and ids from the shared vectors', () => {
    const h = vectors.keys.host;
    const k = keysFromSeeds(fromB64u(h.edSeed), fromB64u(h.xSk));
    expect(b64u(k.edPk)).toBe(h.edPk);
    expect(b64u(k.xPk)).toBe(h.xPk);
    expect(deviceId(k.edPk)).toBe(h.id);
    expect(h.id).toMatch(/^[a-z2-7]{26}$/);
  });
  it('Ed25519 sign/verify, rejects tampering', () => {
    const k = generateDeviceKeys();
    const sig = edSign(k.edSk, utf8('hello'));
    expect(edVerify(k.edPk, utf8('hello'), sig)).toBe(true);
    expect(edVerify(k.edPk, utf8('hellp'), sig)).toBe(false);
    expect(edVerify(k.edPk, utf8('hello'), sig.slice(0, 63))).toBe(false);
  });
  it('hello signature vector verifies', () => {
    const v = vectors.hello;
    expect(edVerify(fromB64u(vectors.keys.host.edPk), utf8(v.message), fromB64u(v.sig))).toBe(true);
  });
});

describe('envelope', () => {
  const h = vectors.keys.host;
  const c = vectors.keys.client;
  const hostKey = envelopeKey(fromB64u(h.xSk), fromB64u(c.xPk), h.id, c.id);
  const clientKey = envelopeKey(fromB64u(c.xSk), fromB64u(h.xPk), c.id, h.id);

  it('both sides derive the same key (vector)', () => {
    expect(b64u(hostKey)).toBe(vectors.envelope.key);
    expect(b64u(clientKey)).toBe(vectors.envelope.key);
  });
  it('opens the sealed vector and the pair request vector', () => {
    expect(openEnvelope(hostKey, vectors.envelope.sealed)).toEqual(vectors.envelope.payload);
    expect(openEnvelope(hostKey, vectors.envelope.pairRequest)).toEqual({ pairNonce: 'nonce-1' });
  });
  it('rejects any modified header field (AAD binding)', () => {
    const base: Envelope = vectors.envelope.sealed;
    for (const mut of [{ seq: 8 }, { typ: 'session.close' }, { sid: 'x' }, { ts: base.ts + 1 }, { to: c.id }]) {
      expect(() => openEnvelope(hostKey, { ...base, ...mut } as Envelope)).toThrow();
    }
  });
  it('rejects a wrong key', () => {
    const other = generateDeviceKeys();
    const wrong = envelopeKey(other.xSk, fromB64u(c.xPk), h.id, c.id);
    expect(() => openEnvelope(wrong, vectors.envelope.sealed)).toThrow();
  });
  it('round-trips random payloads', () => {
    const e = sealEnvelope(clientKey, { typ: 'webrtc.offer', from: c.id, to: h.id, sid: 's1', seq: 1, ts: Date.now() }, { sdp: 'v=0' });
    expect(openEnvelope(hostKey, e)).toEqual({ sdp: 'v=0' });
  });
});

describe('ReplayGuard', () => {
  const mk = (over: Partial<Envelope>): Envelope =>
    ({ v: 1, typ: 'x', from: 'a', to: 'b', sid: 's', seq: 1, ts: 1000, n: 'n1', ct: '', ...over }) as Envelope;
  it('enforces time window, nonce uniqueness and sequence', () => {
    let now = 1000;
    const g = new ReplayGuard(() => now);
    const e1 = mk({});
    expect(g.check(e1)).toBe('ok');
    g.accept(e1);
    expect(g.check(e1)).toBe('replay');
    expect(g.check(mk({ n: 'n2', seq: 1 }))).toBe('seq');
    expect(g.check(mk({ n: 'n2', seq: 2 }))).toBe('ok');
    expect(g.check(mk({ n: 'n3', seq: 1, sid: 'other' }))).toBe('ok');
    now = 1000 + 121_000;
    expect(g.check(mk({ n: 'n4', seq: 3 }))).toBe('stale');
  });
});

describe('pairing', () => {
  const p = vectors.pairing;
  const transcript = pairTranscript(p);
  it('transcript, proofs and verify code match vectors', () => {
    expect(new TextDecoder().decode(transcript)).toBe(p.transcript);
    expect(b64u(pairProof(fromB64u(p.qrSecret), transcript))).toBe(p.qrProof);
    expect(b64u(pairProof(utf8(p.code), transcript))).toBe(p.codeProof);
    expect(verifyCode(transcript)).toBe(p.verifyCode);
    expect(p.verifyCode).toMatch(/^\d{6}$/);
  });
  it('normalizes pairing codes', () => {
    expect(normalizePairCode('k7mxq-2rtp9')).toBe('K7MXQ2RTP9');
    expect(normalizePairCode('K7MXQ2RTP1')).toBeNull(); // '1' excluded
    expect(normalizePairCode('SHORT')).toBeNull();
  });
});
