# RemotePC AI

Control your Windows PC from iPhone or Android, chat with the **AI models running on that PC**, and let an
**AI agent operate Windows** for you — always inside permissions you grant on the PC, with every risky action
confirmed on your phone.

| Component | Tech | Location |
|---|---|---|
| Windows Host (portable `.exe`) | .NET 10, WPF, DXGI Desktop Duplication, Media Foundation H.264 (QSV/NVENC/AMF + software), SIPSorcery WebRTC, UI Automation | `apps/windows-host` |
| Mobile app (iOS 18+, Android 9+) | Flutter 3.50, flutter_webrtc (hardware H.264 decode), SwiftUI Liquid Glass on iOS 26+ | `apps/mobile` |
| Signaling relay | TypeScript — Node (Docker) or Cloudflare Workers + Durable Objects | `backend/signaling` |
| TURN relay | coturn + Caddy (TLS) via Docker Compose | `backend/turn` |
| Protocol spec + cross-language test vectors | Markdown + JSON | `packages/protocol` |

## Features (status in [TEST_REPORT.md](TEST_REPORT.md))

- **Remote desktop**: real-time H.264 over WebRTC, multi-monitor switching without reconnecting, adaptive bitrate/fps
  from RTCP, keyframe on PLI/FIR, quality presets (Auto / Data Saver / Balanced / High / Low Latency / Custom).
- **Input**: trackpad and direct-touch modes, tap/double-tap/long-press, drag, two-finger scroll (both axes), pinch zoom +
  pan, Unicode text (Vietnamese IME), special-key bar, sticky modifiers, Bluetooth keyboards. Coordinates are mapped in
  physical pixels across multi-monitor/HiDPI layouts, removing letterboxing and viewport zoom.
- **Local AI**: Ollama and any OpenAI-compatible server (LM Studio, llama.cpp…) discovered on the PC's loopback; real model
  metadata (context, tools, vision) when the provider reports it; streaming chat, Markdown, code copy, stop, regenerate,
  edit, attachments (TXT/MD/CSV/JSON/XML/YAML/code, PDF, DOCX, XLSX extracted on the PC; images for vision models).
  Chat history stays on the phone.
- **AI agent**: typed, schema-validated tools (screen, window, mouse, keyboard, apps, UI Automation, files, system),
  plan → approval → execute → observe → verify loop, pause/resume/cancel, per-device policy, sensitive actions always
  confirmed, untrusted-content handling against prompt injection, full audit log.
- **Files**: chunked, resumable, SHA-256-verified transfers both ways; file manager limited to folders the PC owner allows;
  never overwrites, never executes received files.
- **Security**: Ed25519/X25519 device identities, QR (256-bit) or one-time-code pairing with owner approval and a
  6-digit verify code, end-to-end encrypted signaling (AES-256-GCM), WebRTC DTLS-SRTP, replay protection, host-side
  permission checks on every request, revocation, lock, emergency disconnect, DPAPI/Keychain/Keystore secret storage,
  redacted logs.

## Quick start

1. **Windows**: download `RemotePC-AI-Windows-x64-Portable.zip`, extract anywhere (USB works), run `RemotePC-AI.exe`.
2. **Phone**: install the APK (Android) or sign and install the IPA (iOS) — see below.
3. On the PC click **Pair a phone**, scan the QR code, compare the 6-digit code, choose permissions, **Allow**.
4. Same Wi-Fi works immediately (LAN). For other networks deploy the signaling server (+ TURN) and enter its `wss://` URL in
   **Settings** — see [NETWORK_SETUP](docs/deployment/NETWORK_SETUP.md).
5. For AI, run Ollama or LM Studio on the PC and pick a model in **Local AI** (or from the phone).

## Building

| Target | Guide | One-liner |
|---|---|---|
| Windows portable | [WINDOWS_BUILD](docs/development/WINDOWS_BUILD.md) | `pwsh scripts/release/package-windows.ps1` |
| Android APK | [ANDROID_BUILD](docs/development/ANDROID_BUILD.md) | `cd apps/mobile && flutter build apk --release` |
| iOS IPA (no Mac) | [IOS_BUILD_GITHUB](docs/development/IOS_BUILD_GITHUB.md) | GitHub Actions → *Build iOS IPA* |
| Signaling / TURN | [NETWORK_SETUP](docs/deployment/NETWORK_SETUP.md) | `docker compose up -d` in `backend/turn` |

Run all tests: `pwsh scripts/test/run-all.ps1`.

## Documentation

[ARCHITECTURE](docs/architecture/ARCHITECTURE.md) · [ADRs](docs/architecture/adr/README.md) · [PROTOCOL](packages/protocol/PROTOCOL.md) ·
[AI_SETUP](docs/user-guide/AI_SETUP.md) · [NETWORK_SETUP](docs/deployment/NETWORK_SETUP.md) · [SECURITY](SECURITY.md) ·
[TROUBLESHOOTING](docs/troubleshooting/TROUBLESHOOTING.md) · [TEST_REPORT](TEST_REPORT.md) · [ROADMAP](ROADMAP.md) · [TASKS](TASKS.md) · [CHANGELOG](CHANGELOG.md)

## Requirements

- Windows 11 x64 (Media Foundation present; "N" editions need the Media Feature Pack). No admin rights, no runtimes to install.
- iPhone with iOS 18 or later (Liquid Glass UI on iOS 26+). Android 9 (API 28) or later.
- Optional: Ollama / LM Studio / llama.cpp on the PC; a VPS for signaling + TURN for cross-network use.
