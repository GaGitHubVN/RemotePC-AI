# Changelog

## 0.1.0 — 2026-11-20 (pre-release, not device-verified)

### Added
- Protocol v1 with cross-language test vectors (TypeScript, C#, Dart).
- Signaling relay (Node + Cloudflare Durable Objects), TURN credential minting, coturn/Caddy compose.
- Windows Host: portable single-file exe, Fluent-style WPF UI (vi/en, light/dark), QR + one-time-code pairing with
  owner approval, DPAPI storage, LAN endpoint + discovery, WebRTC sessions, DXGI capture, Media Foundation H.264
  (Intel QSV verified, software fallback), adaptive bitrate, SendInput with Unicode, clipboard, screenshots,
  Local AI connector (Ollama, OpenAI-compatible), AI agent with approvals and sandboxed tools, chunked verified file
  transfer, activity log with redaction, emergency disconnect, session banner.
- Mobile app (Flutter): pairing (QR/code), remote desktop (trackpad/touch, zoom, keyboard bar, Bluetooth keyboard),
  diagnostics, AI chat (streaming, Markdown, attachments, model picker), agent tasks with approval sheets, file manager,
  settings; iOS 26 Liquid Glass toolbar via SwiftUI platform view.
- CI/CD workflows for CI, Windows, Android, iOS (unsigned IPA) and releases.

### Fixed during development (found by tests)
- Bearer tokens leaked through the log redaction filter.
- One-time pairing codes stopped working once a QR offer was created.
- Resume failed when the previous session was still open.
- Agent permission checks used a snapshot instead of the live device record.
- COM interop marshalled `byte[]` as SAFEARRAY (crash in Media Foundation `GetBlob`).
