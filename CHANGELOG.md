# Changelog

## 0.4.0 — 2026-11-22 (pre-release)

User report on 0.3.0: wants a steady 60 fps, lowest possible latency; taps on the phone were sometimes late.

### Changed
- 60 fps by default: "Auto" is now 1080p60 (6 Mbps, up to 12), "Balanced" 900p60. It was capped at 30 fps, so the
  phone could never see more than 30. The quality menu now shows resolution and fps for each preset.
- Capture and encoding overlap: the capture thread grabs and converts the next frame while the encode thread
  compresses the current one; only the newest frame is kept (no queue = no added delay). Frames are paced on an
  exact 1/fps grid and a fresh frame waits only until its slot. Dev PC (Intel QSV, changing desktop): 1080p
  57 → 61 fps, 900p 61 fps, 720p 61 fps; capture→encoded p50 14–18 ms, p95 24–26 ms (`StreamBenchmark`).
- Phone shows every video frame as soon as it is decoded (`zeroPlayoutDelay`: WebRTC jitter-buffer playout delay
  pinned to 0 ms) instead of holding frames back to smooth the stream.

### Fixed
- Tap → click was delayed ~300 ms: the remote view listened for double-taps, so Flutter held every single tap to
  see whether a second followed. Taps are now sent immediately; a quick second tap reuses the first tap's point so
  Windows still recognises a double-click.
- iOS 26 toolbar: on narrow iPhones the buttons sat in a ScrollView, which delays touches (~150 ms). It now uses
  smaller buttons instead of scrolling.

## 0.3.0 — 2026-11-21 (pre-release)

First report from a real iPhone (iOS 26, user): pairing + connection + video + mouse worked; ~10 fps max, unstable
latency, wrong-looking app icon.

### Fixed
- Unstable latency: RTP timestamps were a fixed 1/fps step while the desktop only produces frames when it changes,
  so the phone's jitter buffer saw "late" frames and kept growing/shrinking. Timestamps now follow the real capture
  clock (unit-tested).
- Latency spikes every 4 s: a full IDR every 4 s burst over Wi-Fi. Periodic IDR is now every 60 s; the phone's
  PLI/FIR still forces one immediately when needed.
- Frame rate: 1 ms timer resolution on the capture thread (Sleep(1) cost 15.6 ms in the async encoder wait) and
  BGRA→NV12 conversion split across cores (8.8 → 4.1 ms at 1080p). Measured on the dev PC with a changing desktop
  (`StreamBenchmark`, REMOTEPC_BENCH=1): 1080p 27.7 → 29.8 fps (target 30); 720p "Low latency" 37–40 fps (target 60).
- Connect timeout counted from session.open; a slow phone that sent its offer after 43 s was dropped. Each
  negotiation step now gets its own 45 s window.
- E2E test host: a failed screenshot no longer kills its HTTP loop (Android CI hang).
- iOS 26 (reported on the iPhone): two blue stripes of the "Connect" button stayed on top of the remote screen. The
  native Liquid Glass button is a real UIView above Flutter; it now exists only while its page is the current,
  settled route (in every enclosing navigator) and a Flutter-drawn stand-in takes its place during transitions.
  Verified on the iOS 26 simulator screenshot (03-remote).
- iOS 26 remote toolbar: buttons centered, separate glass circles, scrolls instead of clipping on narrow screens.
- Two `session.open` arriving at once from the same phone could create two sessions; they are now serialized
  (regression test fails without the fix). The phone waits up to 25 s for `session.accept`.
- CI: an iOS integration-test attempt that hangs (flutter tool never attaches to the app) is stopped after 20 min
  and retried once.

### Added
- Cloud AI next to local AI: Claude (official Anthropic C# SDK), OpenAI, Google Gemini, OpenRouter, Groq, DeepSeek,
  or any OpenAI-compatible HTTPS endpoint. Added from the phone (AI tab → model → "Add cloud AI") or by the PC owner.
  API keys are DPAPI-encrypted on the PC, never stored on the phone, never returned over the API (only "…last4").
  The UI labels cloud vs on-PC sources and warns that content leaves the PC.
- App icon redesigned. iOS 26: Icon Composer `AppIcon.icon` (layered, Liquid Glass, dark/clear/tinted rendered by
  the system). iOS 18–25: full-bleed 1024 icon with Any/Dark/Tinted appearances (the old pre-rounded PNG produced
  doubled corners). Android: adaptive icon with monochrome layer (themed icons). Windows: new .ico.
  Generated from one geometry by `tools/icons/generate_icons.py`.

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
