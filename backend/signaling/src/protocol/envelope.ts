// End-to-end envelope (PROTOCOL.md §3) and pairing proof (§4) — reference implementation.
import { aesGcmOpen, aesGcmSeal, deviceId, hkdf, hmacSha256, random, sha256, x25519 } from './crypto.ts';
import { b64u, fromB64u, utf8 } from './encoding.ts';

export interface Envelope {
  v: 1;
  typ: string;
  from: string;
  to: string;
  sid: string;
  seq: number;
  ts: number;
  n: string;
  kx?: { edPk: string; xPk: string };
  ct: string;
}

export function envelopeKey(mySk: Uint8Array, peerXPk: Uint8Array, idA: string, idB: string): Uint8Array {
  const [lo, hi] = idA < idB ? [idA, idB] : [idB, idA];
  const salt = sha256(utf8(`${lo}|${hi}`));
  return hkdf(x25519(mySk, peerXPk), salt, 'remotepc/v1/envelope');
}

export function envelopeAad(e: Omit<Envelope, 'ct'>): Uint8Array {
  return utf8(
    `remotepc-env-v1|${e.v}|${e.typ}|${e.from}|${e.to}|${e.sid}|${e.seq}|${e.ts}|${e.n}|${e.kx?.edPk ?? ''}|${e.kx?.xPk ?? ''}`,
  );
}

export function sealEnvelope(
  key: Uint8Array,
  header: Omit<Envelope, 'ct' | 'n' | 'v'> & { n?: string },
  payload: unknown,
): Envelope {
  const h: Omit<Envelope, 'ct'> = { v: 1, ...header, n: header.n ?? b64u(random(12)) };
  const ct = aesGcmSeal(key, fromB64u(h.n), utf8(JSON.stringify(payload)), envelopeAad(h));
  return { ...h, ct: b64u(ct) };
}

export function openEnvelope<T = unknown>(key: Uint8Array, env: Envelope): T {
  const { ct, ...h } = env;
  const pt = aesGcmOpen(key, fromB64u(env.n), fromB64u(ct), envelopeAad(h));
  return JSON.parse(new TextDecoder().decode(pt)) as T;
}

/** Stateful receiver checks from PROTOCOL.md §3 (steps 3–5). Decryption is step 6, done by the caller. */
export class ReplayGuard {
  private readonly nonces = new Map<string, number>();
  private readonly seqs = new Map<string, number>();
  private readonly now: () => number;
  private readonly windowMs: number;
  constructor(now: () => number = Date.now, windowMs = 120_000) {
    this.now = now;
    this.windowMs = windowMs;
  }

  check(env: Envelope): 'ok' | 'stale' | 'replay' | 'seq' {
    const now = this.now();
    if (Math.abs(env.ts - now) > this.windowMs) return 'stale';
    const nk = `${env.from}|${env.n}`;
    if (this.nonces.has(nk)) return 'replay';
    const sk = `${env.from}|${env.sid}`;
    if ((this.seqs.get(sk) ?? 0) >= env.seq) return 'seq';
    return 'ok';
  }

  /** Call only after successful decryption. */
  accept(env: Envelope): void {
    const now = this.now();
    this.nonces.set(`${env.from}|${env.n}`, now);
    this.seqs.set(`${env.from}|${env.sid}`, env.seq);
    if (this.nonces.size > 4096) {
      for (const [k, t] of this.nonces) if (now - t > 600_000) this.nonces.delete(k);
    }
  }
}

export function pairTranscript(p: {
  hostId: string;
  clientId: string;
  clientEdPk: string;
  clientXPk: string;
  hostEdPk: string;
  hostXPk: string;
  pairNonce: string;
}): Uint8Array {
  return utf8(
    `remotepc-pair-v1|${p.hostId}|${p.clientId}|${p.clientEdPk}|${p.clientXPk}|${p.hostEdPk}|${p.hostXPk}|${p.pairNonce}`,
  );
}

export function pairProof(secret: Uint8Array, transcript: Uint8Array): Uint8Array {
  return hmacSha256(hkdf(secret, new Uint8Array(0), 'remotepc/v1/pair-proof'), transcript);
}

export function verifyCode(transcript: Uint8Array): string {
  const h = sha256(transcript);
  const n = ((h[0]! << 24) | (h[1]! << 16) | (h[2]! << 8) | h[3]!) >>> 0;
  return String(n % 1_000_000).padStart(6, '0');
}

export const PAIR_CODE_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';

/** Normalizes user input "abcde-fghjk" → "ABCDEFGHJK"; returns null if invalid. */
export function normalizePairCode(input: string): string | null {
  const s = input.toUpperCase().replace(/[\s-]/g, '');
  if (s.length !== 10) return null;
  for (const ch of s) if (!PAIR_CODE_ALPHABET.includes(ch)) return null;
  return s;
}

export { deviceId };
