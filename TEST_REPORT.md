# Test report

Date: 2026-11-20 · Commit: see `git log -1` · Status values as defined in the project brief.

## Environment

| Item | Value |
|---|---|
| Dev PC | Windows 11 Pro 10.0.26300, Intel Iris Xe (Intel Quick Sync), 1 monitor 1920×1080, 15.7 GB RAM |
| Toolchain | .NET SDK 10.0.115, Node 26.13, Flutter 3.50.1 / Dart 3.14, Android SDK 36/37, NDK 28.2, Android Studio JBR (OpenJDK 25.0.4) |
| Local AI | Ollama 0.40.16 with `qwen2.5:0.5b` (pulled for testing; remove with `ollama rm qwen2.5:0.5b`) |
| Phones | none physical — mobile E2E runs on GitHub-hosted iOS Simulators (macOS 26 runner) |

## Automated suites (all run on the dev PC)

| Suite | Tests | Result | What it proves |
|---|---|---|---|
| Signaling (vitest) | 24 | **TEST PASSED** | handshake auth, spoofing/bad-sig/id-mismatch rejection, relay, presence, TURN minting, rate limiting, replay guard, E2E envelope, pairing vectors |
| Windows Core (xUnit) | 91 | **TEST PASSED** | cross-language vectors (TS↔C#), pairing (QR, code, lockout, expiry, single use, concurrent offers), router (replay, stale, revoked, impersonation), DPAPI-style store, signaling client ↔ **real Node server**, LAN endpoint + discovery, sessions over **real WebRTC** (video FU-A, RPC, input, permissions, revocation, resume, no duplicates, lock), file transfer over real data channels (upload, download, resume, checksum mismatch, traversal, delete confirmation, permissions), agent engine (approval, reject, sensitive, invented args, revoke mid-task, cancel, plan edit), sandbox, extractor, ABR, coordinate mapping, colour conversion, log redaction |
| Windows Platform (xUnit, real hardware) | 9 | **TEST PASSED** | DPAPI; monitor/encoder enumeration; **real desktop capture + Intel QSV and software H.264 encode**; **real desktop streamed over WebRTC to a client**; PNG screenshot; Unicode clipboard; **Vietnamese text typed with SendInput and read back via UI Automation**; screenshot/CPU/app-list/file tools |
| Local AI (xUnit, real Ollama) | 1 | **TEST PASSED** | discovery, real metadata (ctx 32768, tools ✓, vision ✗), streaming (first token 1073 ms cold, 114.5 tok/s), real tool call `screen_capture` |
| Local AI agent (real Ollama, qwen2.5:0.5b) | 1 | **TEST FAILED** | the 0.5B model called `file_create` instead of `file_create_folder` and then claimed success; the engine's step log recorded the truth. Needs a ≥7B tool model — **NOT VERIFIED** |
| Mobile (flutter test) | 6 | **TEST PASSED** | Dart crypto byte-identical to TS/C# vectors (keys, ids, Ed25519 sig, envelope key, ciphertext, pairing proofs, verify code), replay guard |
| CI on GitHub Actions (ubuntu) | signaling + core + mobile | **TEST PASSED** | after fixing CA2027 raised by the newer SDK on the runner |
| Static analysis | — | **PASSED** | .NET builds with warnings-as-errors + analyzers; `tsc --noEmit`; `flutter analyze` 0 errors/0 warnings (15 deprecation infos, see TASKS) |

Measured numbers (dev PC, not targets): QSV encode 9.0 ms/frame and CPU BGRA→NV12 7.9 ms at 1080p→720p; software MFT 2.6 ms;
SCTP file throughput between two SIPSorcery peers on loopback **205 KiB/s** (slow — known limitation); `ctl` ping during a
bulk upload 57 ms.

## Mobile end-to-end on iOS Simulators (GitHub Actions, workflow `mobile-e2e`)

The real app runs on a simulator against `tools/RemotePC.TestHost` (real RemotePC.Core over loopback; CI-only,
auto-approves pairing; replays a synthetic H.264 clip). Screenshots are taken natively (`xcrun simctl io`) by the host.

| Simulator | UI branch chosen at runtime | Result | Evidence |
|---|---|---|---|
| iPhone 17 Pro Max, iOS 26.5 | `glass` — system UITabBarController tab bar, SwiftUI `.glass`/`.glassProminent` buttons, GlassEffectContainer toolbar | **TEST PASSED** (2 consecutive runs) | pair → WebRTC connected (direct LAN) → H.264 1280×720 decoded → tap reached host (`INPUT button left`) → chat RPC → fs.roots/fs.list → download 300 KiB SHA-256 ok → upload SHA-256 ok → native tab switch → settings shows "Liquid Glass · iOS 26" |
| iPhone 16 Pro Max, iOS 18.6 | `cupertino` — iOS 18 design | **TEST PASSED** (2 consecutive runs) | same flow; settings shows "iOS 18" |
| Android 14 emulator (Pixel 7 AVD, x86_64, on the dev PC; `scripts/test/mobile-e2e-android.ps1`) | `material` — Material 3 | **TEST PASSED** | same flow incl. video (1280×720, direct STUN path), tap → `INPUT button left`, download/upload SHA-256 ok, light + dark screenshots. CI job: emulator did not boot (runner disk space; fixed, not re-run — Actions billing limit) |

Bugs found and fixed by this suite: iOS crash on Connect (`"username": null` → NSNull inside flutter_webrtc), duplicate
`connect()` from `initState` (notify during build), ICE candidates applied before the SDP answer (envelopes now handled in
order, candidates queued), list rows hidden under translucent navigation bars, missing Cupertino icon font.

## Builds / artifacts

| Artifact | Status | Evidence |
|---|---|---|
| `releases/windows/RemotePC-AI-Windows-x64-Portable.zip` (single 89 MB exe) | **BUILD PASSED**, smoke-tested | started from an isolated temp folder with `--smoke-test`: exit 0, DPAPI identity created, LAN endpoint up; UI rendered (screenshot reviewed) |
| `releases/android/RemotePC-AI-Android-release.apk` (122 MB, debug-key signed) | **BUILD PASSED** | `aapt2`: minSdk 28, targetSdk 36, ABIs arm64-v8a/armeabi-v7a/x86_64, permissions listed in ANDROID_BUILD.md |
| `releases/ios/RemotePC-AI-iOS-unsigned.ipa` (14 MB, unsigned) | **BUILD PASSED** on GitHub Actions `macos-26`, Xcode 26.6, iOS SDK 26.5 | `flutter test` 6/6 on macOS; `lipo`: arm64 device binary; `otool LC_BUILD_VERSION`: platform 2 (iOS, not simulator), minos 18.0, sdk 26.5; bundle id `ai.remotepc.app`; frameworks App/Flutter/WebRTC/objective_c all arm64; SHA-256 `fe235a20…3a640e`. Installed on a device: **NOT VERIFIED** (needs your signing tool + iPhone) |
| Cloudflare Worker | **BUILD PASSED** (bundle) | `wrangler deploy --dry-run`; runtime **NOT VERIFIED** |
| Signaling Docker image / TURN compose | **NOT VERIFIED** | Docker not installed on the dev PC |

## End-to-end scenarios from the brief

| # | Scenario | Status | Notes |
|---|---|---|---|
| 1 | Start host, open app, pair, connect, receive screen | **NOT VERIFIED on phone** | host side verified with a protocol-complete WebRTC test client (real capture → QSV → WebRTC → frames with SPS/IDR) |
| 2 | Move mouse, click, type Vietnamese | **NOT VERIFIED on phone** | input channels verified end-to-end with the test client; SendInput Unicode + UIA readback verified on Windows |
| 3 | AI tab, list models, select, prompt, streaming | **NOT VERIFIED on phone** | host RPC + real Ollama streaming verified |
| 4 | AI screenshot to phone | **NOT VERIFIED on phone** | `screen.capture` tool + PNG verified |
| 5 | Send/receive file + checksum | **TEST PASSED** (test client) / NOT VERIFIED on phone | |
| 6 | Revoke device, reconnect denied | **TEST PASSED** (test client) | |
| 7 | Wi-Fi → 4G switch, no duplicate session | **NOT VERIFIED** | session replacement/resume logic tested; real network switch needs a phone |
| 8 | TURN fallback across networks | **NOT VERIFIED** | needs a VPS with coturn |

## Compatibility matrix

| Platform | Status |
|---|---|
| Windows 11 x64 + Intel GPU (QSV) | TEST PASSED (dev PC) |
| Windows software encoder path | TEST PASSED (dev PC, forced) |
| NVIDIA / AMD encoders, multi-monitor, rotated displays | NOT VERIFIED (no hardware); coordinate mapping unit-tested |
| iOS 18 / iOS 26 (portrait, landscape, dark/light, Liquid Glass) | NOT VERIFIED |
| Android 9 and later, various screen sizes | NOT VERIFIED (APK built, not installed) |

## Known limitations

See [TASKS.md](TASKS.md#known-limitations).
