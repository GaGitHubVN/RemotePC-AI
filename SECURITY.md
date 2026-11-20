# Security

## Threat model (summary)

| Adversary | Can | Cannot (by design) |
|---|---|---|
| Network attacker (LAN, Wi-Fi, ISP) | see that devices talk, sizes/timing | read or alter signaling (E2E AES-GCM), media/data (DTLS-SRTP), or impersonate a paired device |
| Malicious / compromised signaling server | drop messages, learn device IDs online | decrypt envelopes, forge SDP/DTLS fingerprints, pair itself as a phone |
| Attacker on the LAN during pairing (code mode) | try to MITM key exchange | succeed unnoticed: the owner must approve on the PC and compare the 6-digit verify code shown on both screens |
| Stolen pairing code / QR | pair once within 5 minutes | pair without the owner clicking **Allow** on the PC; reuse the code (single use, 5 failures lock it) |
| Lost phone | use granted permissions while paired | keep access after **Revoke** on the PC (effective immediately, live sessions are closed) |
| Malicious content (files, web pages, screen text) seen by the AI | inject instructions into model input | grant itself permissions, bypass approvals, use tools outside the sandbox, run shell commands |
| Remote user | everything the granted permissions allow | cross UAC/secure desktop, elevate, persist, run hidden (host runs `asInvoker`, shows a banner, no service) |

## Cryptography (no custom algorithms)

- Ed25519 device signatures (BouncyCastle / Node crypto / package:cryptography), X25519 ECDH, HKDF-SHA256, AES-256-GCM,
  HMAC-SHA256 pairing proofs, SHA-256 checksums. Three implementations cross-checked with shared test vectors.
- WebRTC DTLS 1.2 + SRTP for media and SCTP data channels; DTLS fingerprints are carried inside authenticated envelopes.
- Replay protection: ±120 s timestamp window, nonce cache, strictly increasing per-session sequence numbers.

## Credentials

| Secret | Storage |
|---|---|
| PC identity keys, paired-device list | DPAPI (CurrentUser) files in `data\` — unreadable on another account/PC |
| Phone identity keys, paired PCs | iOS Keychain (`afterFirstUnlockThisDeviceOnly`), Android Keystore-backed EncryptedSharedPreferences |
| TURN shared secret | server environment / `wrangler secret`; never in the apps |
| Android keystore, iOS certs | your machine or GitHub Secrets; `.gitignore` blocks `*.jks`, `*.p12`, `*.mobileprovision`, `key.properties`, `.env` |

No hardcoded passwords or tokens exist in the source.

## Permissions (checked on the PC for every request)

view_screen · control_input · use_ai · ai_agent · send_files · receive_files · modify_files · clipboard · audio.
New devices get view + control + AI chat by default; the owner adjusts them in the approval dialog or later in Devices.

## Logging

Logged: connections, authentication/pairing, permission changes, file transfer metadata, agent tool calls, errors.
Every message passes a redaction filter (tokens, keys, passwords, bearer headers, long opaque strings — unit tested; it
caught and fixed a bearer-token leak during development). File contents, chat content and typed text are never logged.
Retention: 14 days by default (configurable), **Clear logs** and **Export redacted logs** on the Activity Logs page.

## Owner controls

Emergency Disconnect (sidebar and banner) · Disconnect all · Revoke device / Revoke all · Lock remote access ·
Remote input on/off · per-device permissions · agent policy · session banner always visible while connected.

## Reporting vulnerabilities

Please open a private security advisory on the repository rather than a public issue.
