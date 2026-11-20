# Changelog

## 0.2.0 — 2026-11-20 (pre-release; verified on iOS 18/26 simulators and Android 14 emulator, not on phones)

### Fixed
- iOS: app crashed on every action that connects (Connect, Chat, Files, Tasks). The PC sent `"username": null` in
  ICE servers; flutter_webrtc on iOS passes it to RTCIceServer as NSNull. The PC no longer sends null fields and the
  app sanitises ICE servers and candidates before they reach native code.
- ICE candidates could be applied before the SDP answer (envelopes were handled concurrently) → now in order, queued.
- Remote screen started a second connection during build.
- Rows hidden under translucent navigation bars; missing Cupertino icon font.

### Changed
- UI chosen at runtime from the OS (`#available(iOS 26.0, *)` in native code):
  iOS 26+ Liquid Glass — system UITabBarController tab bar, SwiftUI `.glass`/`.glassProminent` buttons and bar groups,
  GlassEffectContainer remote toolbar, UIAlertController alerts/action sheets/prompts;
  iOS 18–25 — iOS 18 design (Cupertino); Android — Material 3.
- Per-tab navigation stacks; full-screen flows hide the tab bar; compact remote toolbar with a More menu.

### Added
- `tools/RemotePC.TestHost` (CI/test only) and mobile E2E on iOS simulators (GitHub Actions) and Android emulator.

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
