// Reference implementation of the RemotePC v1 cryptography using Node's built-in crypto.
import {
  createCipheriv,
  createDecipheriv,
  createHash,
  createHmac,
  createPrivateKey,
  createPublicKey,
  diffieHellman,
  generateKeyPairSync,
  hkdfSync,
  randomBytes,
  sign,
  timingSafeEqual,
  verify,
  type KeyObject,
} from 'node:crypto';
import { base32, b64u, fromB64u, utf8 } from './encoding.ts';

const ED_PKCS8 = Buffer.from('302e020100300506032b657004220420', 'hex');
const ED_SPKI = Buffer.from('302a300506032b6570032100', 'hex');
const X_PKCS8 = Buffer.from('302e020100300506032b656e04220420', 'hex');
const X_SPKI = Buffer.from('302a300506032b656e032100', 'hex');

export interface DeviceKeys {
  edSk: Uint8Array; // 32-byte seed
  edPk: Uint8Array;
  xSk: Uint8Array;
  xPk: Uint8Array;
}

const edPriv = (seed: Uint8Array): KeyObject =>
  createPrivateKey({ key: Buffer.concat([ED_PKCS8, seed]), format: 'der', type: 'pkcs8' });
const edPub = (pk: Uint8Array): KeyObject =>
  createPublicKey({ key: Buffer.concat([ED_SPKI, pk]), format: 'der', type: 'spki' });
const xPriv = (sk: Uint8Array): KeyObject =>
  createPrivateKey({ key: Buffer.concat([X_PKCS8, sk]), format: 'der', type: 'pkcs8' });
const xPub = (pk: Uint8Array): KeyObject =>
  createPublicKey({ key: Buffer.concat([X_SPKI, pk]), format: 'der', type: 'spki' });

const rawPub = (k: KeyObject): Uint8Array =>
  new Uint8Array(k.export({ format: 'der', type: 'spki' }).subarray(12));

export function keysFromSeeds(edSeed: Uint8Array, xSeed: Uint8Array): DeviceKeys {
  if (edSeed.length !== 32 || xSeed.length !== 32) throw new Error('seed must be 32 bytes');
  return {
    edSk: edSeed,
    edPk: rawPub(createPublicKey(edPriv(edSeed))),
    xSk: xSeed,
    xPk: rawPub(createPublicKey(xPriv(xSeed))),
  };
}

export function generateDeviceKeys(): DeviceKeys {
  const ed = generateKeyPairSync('ed25519');
  const x = generateKeyPairSync('x25519');
  return {
    edSk: new Uint8Array(ed.privateKey.export({ format: 'der', type: 'pkcs8' }).subarray(16)),
    edPk: rawPub(ed.publicKey),
    xSk: new Uint8Array(x.privateKey.export({ format: 'der', type: 'pkcs8' }).subarray(16)),
    xPk: rawPub(x.publicKey),
  };
}

export const sha256 = (data: Uint8Array): Uint8Array => new Uint8Array(createHash('sha256').update(data).digest());

export function deviceId(edPk: Uint8Array): string {
  if (edPk.length !== 32) throw new Error('edPk must be 32 bytes');
  return base32(sha256(edPk)).slice(0, 26);
}

export function edSign(seed: Uint8Array, msg: Uint8Array): Uint8Array {
  return new Uint8Array(sign(null, msg, edPriv(seed)));
}

export function edVerify(pk: Uint8Array, msg: Uint8Array, sig: Uint8Array): boolean {
  if (pk.length !== 32 || sig.length !== 64) return false;
  try {
    return verify(null, msg, edPub(pk), sig);
  } catch {
    return false;
  }
}

export function x25519(sk: Uint8Array, peerPk: Uint8Array): Uint8Array {
  return new Uint8Array(diffieHellman({ privateKey: xPriv(sk), publicKey: xPub(peerPk) }));
}

export function hkdf(ikm: Uint8Array, salt: Uint8Array, info: string, len = 32): Uint8Array {
  return new Uint8Array(hkdfSync('sha256', ikm, salt, utf8(info), len));
}

export const hmacSha256 = (key: Uint8Array, msg: Uint8Array): Uint8Array =>
  new Uint8Array(createHmac('sha256', key).update(msg).digest());

export function ctEqual(a: Uint8Array, b: Uint8Array): boolean {
  return a.length === b.length && timingSafeEqual(a, b);
}

export function aesGcmSeal(key: Uint8Array, nonce: Uint8Array, plaintext: Uint8Array, aad: Uint8Array): Uint8Array {
  const c = createCipheriv('aes-256-gcm', key, nonce);
  c.setAAD(aad);
  const body = Buffer.concat([c.update(plaintext), c.final()]);
  return new Uint8Array(Buffer.concat([body, c.getAuthTag()]));
}

export function aesGcmOpen(key: Uint8Array, nonce: Uint8Array, sealed: Uint8Array, aad: Uint8Array): Uint8Array {
  if (sealed.length < 16) throw new Error('ciphertext too short');
  const d = createDecipheriv('aes-256-gcm', key, nonce);
  d.setAAD(aad);
  d.setAuthTag(sealed.subarray(sealed.length - 16));
  return new Uint8Array(Buffer.concat([d.update(sealed.subarray(0, sealed.length - 16)), d.final()]));
}

export const random = (n: number): Uint8Array => new Uint8Array(randomBytes(n));

export { b64u, fromB64u };
