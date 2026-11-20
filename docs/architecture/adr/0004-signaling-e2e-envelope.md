# ADR-0004 — Untrusted relay signaling with end-to-end encrypted envelopes

## Context
Signaling carries SDP (DTLS fingerprints, IP addresses) and pairing traffic. If the signaling server could
modify SDP it could MITM the WebRTC session. We also want a LAN mode that works without Internet.

## Decision
- The signaling server **authenticates devices** (Ed25519 challenge) and **routes** envelopes; it never holds
  keys that can read or forge peer traffic.
- Peer-to-peer envelopes are encrypted with AES-256-GCM under a key derived from static X25519 keys exchanged
  during pairing; the header is bound as AAD. Replay protection: timestamp window, nonce cache, per-session sequence numbers.
- Because DTLS fingerprints travel inside authenticated envelopes, a compromised signaling server or LAN
  attacker cannot substitute its own DTLS certificate.
- The Windows Host also serves the same signaling protocol on the LAN (`ws://`), so LAN works offline.
  TLS is not used there because no trusted certificate exists for a LAN IP; confidentiality and integrity
  come from the envelope. Only routing metadata (device IDs, message sizes) is visible on the LAN.

## Trade-offs
- Static-static DH has no forward secrecy for signaling payloads. Acceptable: payloads are SDP/ICE and pairing
  requests; media and data use DTLS with ephemeral keys (forward secret). Upgrading to an ephemeral handshake
  (e.g. Noise IK) is a compatible future `v:2`.
- Code-mode pairing uses a ~49-bit code without a PAKE; a MITM is detected by the mandatory host approval
  with a 6-digit verify code shown on both screens. QR mode uses a 256-bit secret.
