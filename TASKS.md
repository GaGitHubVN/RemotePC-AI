# Tasks & status

Status vocabulary: NOT STARTED · IN PROGRESS · IMPLEMENTED · BUILD PASSED · TEST PASSED · TEST FAILED · NOT VERIFIED · BLOCKED · RELEASE READY.
Nothing is RELEASE READY: the app is verified end-to-end on iOS 18 and iOS 26 **simulators** (and the Android emulator job,
see TEST_REPORT), not yet on physical phones.

| Area | Item | Status |
|---|---|---|
| Protocol | Spec v1, vectors, TS/C#/Dart implementations | TEST PASSED |
| Signaling | Node server | TEST PASSED |
| Signaling | Cloudflare DO adapter | BUILD PASSED (runtime NOT VERIFIED) |
| Signaling | Docker image + coturn compose + Caddy TLS | IMPLEMENTED (NOT VERIFIED — no Docker on dev PC) |
| Host | Identity, DPAPI store, pairing (QR + code + approval + verify code) | TEST PASSED |
| Host | Internet signaling client (backoff, heartbeat, TURN request) | TEST PASSED |
| Host | LAN endpoint + UDP discovery | TEST PASSED |
| Host | Sessions over WebRTC, RPC, permissions, revocation, resume, lock | TEST PASSED |
| Host | DXGI capture + MF H.264 (QSV + software) | TEST PASSED (Intel only) |
| Host | Adaptive bitrate/fps from RTCP, PLI/FIR keyframes | IMPLEMENTED (ABR logic unit-tested; behaviour under real loss NOT VERIFIED) |
| Host | Input (SendInput, Unicode, combos, release on disconnect) | TEST PASSED (Unicode via UIA readback) |
| Host | Clipboard (manual) | TEST PASSED |
| Host | AI connector (Ollama, OpenAI-compatible) | TEST PASSED (Ollama real; OpenAI-compatible with canned SSE) |
| Host/Mobile | Cloud AI: API key (9 services + custom URL) and OpenRouter account sign-in (PKCE) | TEST PASSED (unit + E2E start); real-account sign-in NOT VERIFIED |
| Host | Agent engine + tools | TEST PASSED (scripted model); real 0.5B model TEST FAILED |
| Host | File transfer + file manager | TEST PASSED |
| Host | WPF UI (9 sections, vi/en, themes, banner, dialogs) | BUILD PASSED (manual smoke check of dashboard) |
| Host | Portable single-file package | BUILD PASSED + smoke test |
| Mobile | Crypto/protocol | TEST PASSED |
| Mobile | Crash on Connect (NSNull in iceServers) | FIXED — regression tests in C# and Dart; E2E connects on iOS simulators |
| Mobile | UI selection by OS (native `#available(iOS 26.0, *)`) | TEST PASSED (simulators: iOS 26.5 → Liquid Glass, iOS 18.6 → iOS 18 design) |
| Mobile | iOS 26: system UITabBarController tab bar, SwiftUI `.glass`/`.glassProminent` buttons, GlassEffectContainer toolbar, UIAlertController | TEST PASSED on simulator (screenshots) — NOT VERIFIED on device |
| Mobile | iOS 18: Cupertino design, system alerts | TEST PASSED on simulator — NOT VERIFIED on device |
| Mobile | Pair → WebRTC video → input → chat → files → tabs (E2E vs real Core) | TEST PASSED on iOS 26.5 and 18.6 simulators |
| Mobile | Android Material 3 UI | BUILD PASSED; emulator E2E see TEST_REPORT — NOT VERIFIED on device |
| CI/CD | ci, build-windows, build-android, build-ios, mobile-e2e, release | ci / windows / android / iOS IPA / iOS E2E: PASSED on GitHub Actions |

## Known limitations

1. **No physical phone testing yet** — simulators prove code paths and UI branches, not performance, camera QR scanning,
   real Wi-Fi/cellular switching or the signing step.
2. **Simulator video is ~8–17 fps** (software decode in the simulator); real-device decode is hardware and NOT VERIFIED.
3. **SCTP throughput** of the host WebRTC stack ≈ 205 KiB/s (loopback, SIPSorcery↔SIPSorcery). Plan: tune SCTP window /
   multiple channels, or a direct authenticated TLS channel for bulk LAN transfers.
4. **Remote audio** is not implemented (permission exists; WASAPI loopback → Opus is the planned module).
5. **Automatic clipboard sync** is not implemented; manual send/get works.
6. **HEVC/AV1**: encoder enumeration supports them, but negotiation/packetisation is H.264-only.
7. **GPU colour conversion / zero-copy** not implemented (CPU NV12 conversion ~8–10 ms/frame).
8. **Host cursor shape** is not streamed; the phone draws its own pointer in trackpad mode.
9. **iOS LAN discovery** needs Apple's multicast entitlement (not requested) — use the QR code.
10. **Small local models** are poor agents; ≥7B tool models recommended (unverified).
11. Android R8/minification disabled (needs keep rules for flutter_webrtc).
12. Flutter 3.50 deprecates `package:flutter/material.dart`; migrate to `material_ui` when plugins do.
13. Signaling envelopes use static-static DH (no forward secrecy for SDP/ICE); media/data are forward-secret.

## Next steps (in order)

1. Sign the IPA with your tool; install on iOS 18 and iOS 26 iPhones; run scenarios 1–7.
2. Install APK on an Android 9+ phone and the signed IPA on iOS 18 and 26 devices; run scenarios 1–7.
3. Deploy signaling + coturn on a VPS; run scenario 8 (cellular ↔ home NAT).
4. Pull a 7B+ tool model and re-run the agent examples 1–5.
5. Remote audio module; automatic clipboard; SCTP throughput work; GPU conversion.
