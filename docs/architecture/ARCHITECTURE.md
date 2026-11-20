# Architecture

## System overview

```
 ┌─────────────── Phone (Flutter) ───────────────┐            ┌────────────── Windows Host (.NET 10) ──────────────┐
 │ UI: Home · Remote · AI Chat · Agent · Files    │            │ WPF UI · session banner · approval dialogs          │
 │ HostLink: E2E envelopes, RPC, input, files     │            │ HostRuntime (composition root)                      │
 │ flutter_webrtc (HW H.264 decode)               │            │  PeerRouter (E2E envelopes) · PairingManager        │
 │ Keychain / Android Keystore                    │            │  SessionManager → HostSession (SIPSorcery WebRTC)   │
 └───────┬───────────────────────────▲────────────┘            │  DesktopScreenSource: DXGI → NV12 → MF H.264        │
         │ (1) signaling: wss:// or LAN ws://                   │  InputInjector (SendInput) · Clipboard · DPAPI      │
         │     authenticated, E2E-encrypted envelopes           │  AiService (Ollama / OpenAI-compatible, loopback)   │
         ▼                           │                          │  AgentEngine + ToolRegistry (UIA, files, apps…)     │
 ┌──────────────────────┐            │                          │  FileTransferService · ActivityLog (redacted)       │
 │ Signaling relay      │◄───────────┼────────────────────────► │  LanSignalingServer :45822 · UDP discovery :45821   │
 │ Node or CF DO        │  routes    │                          └──────────────────────────────▲──────────────────────┘
 │ mints TURN creds     │  only      │ (2) WebRTC: direct (host/srflx) or via TURN relay        │
 └──────────────────────┘            └──────────────────────────────────────────────────────────┘
                                          DTLS-SRTP video · SCTP data channels ctl / mv / in / file
```

1. **Signaling** carries only envelopes (pairing, session open, SDP, ICE). Envelopes are AES-256-GCM encrypted with a
   key derived from X25519 keys exchanged at pairing, so the relay — and anyone on the LAN — can neither read nor forge
   them. This also authenticates the DTLS fingerprints inside SDP, so WebRTC cannot be MITM'd by the relay.
2. **WebRTC** carries everything else. ICE tries host candidates (LAN), STUN (most home NATs), and TURN (symmetric/CGNAT).

## Data channels

| Label | Mode | Purpose |
|---|---|---|
| `ctl` | reliable, ordered | RPC (`{id,m,p}` → `{id,r}`/`{id,e}`), streamed events (`{ev,p,id}`) |
| `mv` | unordered, no retransmit | pointer moves with sequence numbers (late moves are dropped) |
| `in` | reliable, ordered | buttons, wheel, keys, Unicode text, combos (each with absolute coordinates) |
| `file` | reliable, ordered, binary | `[16 B id][8 B offset][≤60 KiB]` chunks, sender keeps ≤256 KiB buffered so `ctl` stays responsive |

## AI architecture

```
phone ── ai.chat (ctl) ──► AiService ──► provider (http://127.0.0.1:11434 Ollama | :1234 LM Studio | :8080 llama.cpp)
phone ── agent.run ──► AgentEngine: plan ─► [approval] ─► loop { model(tools) ─► validate args ─► policy/approval
                                       ─► execute (timeout, cancel) ─► observe (untrusted wrapper) } ─► verify ─► answer
```

- Provider APIs are never exposed off the PC; the phone reaches them only through an authenticated session with the
  `use_ai` permission.
- Tools carry a risk class: **Read** (auto by default), **Write** (confirmation by default), **Sensitive** (always confirmed,
  e.g. delete, overwrite, Win+key combos). Arguments are validated against a strict schema (unknown arguments rejected).
- Permission to use the agent is re-read from the device store for every tool call.

## Security boundaries

| Boundary | Enforcement |
|---|---|
| Phone ↔ PC identity | Ed25519/X25519 keys; pairing proof + owner approval + verify code |
| Relay / LAN attacker | E2E envelopes, replay guard (timestamp, nonce cache, sequence), DTLS fingerprint binding |
| Phone → PC capabilities | Per-device permissions checked on the host for every RPC/input/chunk |
| AI → PC | Tool schema validation, file sandbox (roots, no traversal/junctions/ADS/device names), approvals, no shell tool |
| PC → secure desktop/UAC | Not crossed: host runs `asInvoker`; SendInput cannot reach secure desktop or elevated windows |
| Secrets at rest | DPAPI (Windows), Keychain (iOS), Keystore-backed storage (Android) |

## Trade-offs (summary — details in ADRs)

- SIPSorcery instead of Google libwebrtc on the host: no native toolchain needed, but simpler congestion control (we add our
  own RTCP-driven ABR) and **slower SCTP** (≈205 KiB/s measured on loopback between two SIPSorcery peers).
- CPU colour conversion (BGRA→NV12, ~8–10 ms at 1080p→720p) instead of a GPU video processor: simpler and verified; GPU
  path is an optimisation target.
- Static-static DH for envelopes (no forward secrecy for signaling payloads); media/data are forward-secret via DTLS.
