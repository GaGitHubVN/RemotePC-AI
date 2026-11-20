# Test report

Date: 2026-11-20 · Commit: see `git log -1` · Status values as defined in the project brief.

## Environment

| Item | Value |
|---|---|
| Dev PC | Windows 11 Pro 10.0.26300, Intel Iris Xe (Intel Quick Sync), 1 monitor 1920×1080, 15.7 GB RAM |
| Toolchain | .NET SDK 10.0.115, Node 26.13, Flutter 3.50.1 / Dart 3.14, Android SDK 36/37, NDK 28.2, Android Studio JBR (OpenJDK 25.0.4) |
| Local AI | Ollama 0.40.16 with `qwen2.5:0.5b` (pulled for testing; remove with `ollama rm qwen2.5:0.5b`) |
| Phones | **none available** — no physical device, no emulator system image, no Mac |

## Automated suites (all run on the dev PC)

| Suite | Tests | Result | What it proves |
|---|---|---|---|
| Signaling (vitest) | 24 | **TEST PASSED** | handshake auth, spoofing/bad-sig/id-mismatch rejection, relay, presence, TURN minting, rate limiting, replay guard, E2E envelope, pairing vectors |
| Windows Core (xUnit) | 91 | **TEST PASSED** | cross-language vectors (TS↔C#), pairing (QR, code, lockout, expiry, single use, concurrent offers), router (replay, stale, revoked, impersonation), DPAPI-style store, signaling client ↔ **real Node server**, LAN endpoint + discovery, sessions over **real WebRTC** (video FU-A, RPC, input, permissions, revocation, resume, no duplicates, lock), file transfer over real data channels (upload, download, resume, checksum mismatch, traversal, delete confirmation, permissions), agent engine (approval, reject, sensitive, invented args, revoke mid-task, cancel, plan edit), sandbox, extractor, ABR, coordinate mapping, colour conversion, log redaction |
| Windows Platform (xUnit, real hardware) | 9 | **TEST PASSED** | DPAPI; monitor/encoder enumeration; **real desktop capture + Intel QSV and software H.264 encode**; **real desktop streamed over WebRTC to a client**; PNG screenshot; Unicode clipboard; **Vietnamese text typed with SendInput and read back via UI Automation**; screenshot/CPU/app-list/file tools |
| Local AI (xUnit, real Ollama) | 1 | **TEST PASSED** | discovery, real metadata (ctx 32768, tools ✓, vision ✗), streaming (first token 1073 ms cold, 114.5 tok/s), real tool call `screen_capture` |
| Local AI agent (real Ollama, qwen2.5:0.5b) | 1 | **TEST FAILED** | the 0.5B model called `file_create` instead of `file_create_folder` and then claimed success; the engine's step log recorded the truth. Needs a ≥7B tool model — **NOT VERIFIED** |
| Mobile (flutter test) | 6 | **TEST PASSED** | Dart crypto byte-identical to TS/C# vectors (keys, ids, Ed25519 sig, envelope key, ciphertext, pairing proofs, verify code), replay guard |
| Static analysis | — | **PASSED** | .NET builds with warnings-as-errors + analyzers; `tsc --noEmit`; `flutter analyze` 0 errors/0 warnings (15 deprecation infos, see TASKS) |

Measured numbers (dev PC, not targets): QSV encode 9.0 ms/frame and CPU BGRA→NV12 7.9 ms at 1080p→720p; software MFT 2.6 ms;
SCTP file throughput between two SIPSorcery peers on loopback **205 KiB/s** (slow — known limitation); `ctl` ping during a
bulk upload 57 ms.

## Builds / artifacts

| Artifact | Status | Evidence |
|---|---|---|
| `releases/windows/RemotePC-AI-Windows-x64-Portable.zip` (single 89 MB exe) | **BUILD PASSED**, smoke-tested | started from an isolated temp folder with `--smoke-test`: exit 0, DPAPI identity created, LAN endpoint up; UI rendered (screenshot reviewed) |
| `releases/android/RemotePC-AI-Android-release.apk` (122 MB, debug-key signed) | **BUILD PASSED** | `aapt2`: minSdk 28, targetSdk 36, ABIs arm64-v8a/armeabi-v7a/x86_64, permissions listed in ANDROID_BUILD.md |
| iOS unsigned IPA | **NOT VERIFIED** | workflow written; cannot run without GitHub push (needs your go-ahead) and macOS runner |
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
