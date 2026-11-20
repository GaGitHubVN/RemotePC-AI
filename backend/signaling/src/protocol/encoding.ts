// Encoding helpers shared by the protocol implementation (see packages/protocol/PROTOCOL.md).

export function b64u(data: Uint8Array): string {
  return Buffer.from(data).toString('base64url');
}

export function fromB64u(s: string): Uint8Array {
  if (!/^[A-Za-z0-9_-]*$/.test(s)) throw new Error('invalid base64url');
  return new Uint8Array(Buffer.from(s, 'base64url'));
}

const B32 = 'abcdefghijklmnopqrstuvwxyz234567';

/** RFC 4648 base32, lowercase, no padding. */
export function base32(data: Uint8Array): string {
  let bits = 0;
  let value = 0;
  let out = '';
  for (const byte of data) {
    value = (value << 8) | byte;
    bits += 8;
    while (bits >= 5) {
      out += B32[(value >>> (bits - 5)) & 31];
      bits -= 5;
    }
    value &= (1 << bits) - 1;
  }
  if (bits > 0) out += B32[(value << (5 - bits)) & 31];
  return out;
}

export const utf8 = (s: string): Uint8Array => new TextEncoder().encode(s);
