# ADR-0003 — Mobile: Flutter + native Liquid Glass components on iOS 26+

## Decision
One Flutter app (`apps/mobile`) produces both the iOS and Android binaries, so feature parity is structural
rather than a discipline. Platform-specific code:

- **iOS 26+:** floating remote toolbar, tab bar chrome and action buttons are SwiftUI views using the native
  `glassEffect` / `GlassEffectContainer` APIs, embedded via `UiKitView` platform views
  (`ios/Runner/Glass/`). They are guarded with `if #available(iOS 26.0, *)`; on iOS 18–25 the Dart side
  never creates the platform view and renders the Cupertino-style Flutter fallback. The Dart side asks
  native code `glass.isSupported` at runtime instead of parsing version strings.
- **Android:** Material 3 with dynamic color (Android 12+), edge-to-edge. No fake glass.
- **WebRTC:** `flutter_webrtc` (Google libwebrtc; hardware H.264 decode via VideoToolbox / MediaCodec).
- **Crypto:** `package:cryptography` (pure-Dart Ed25519, X25519, AES-GCM, HKDF, HMAC) — verified against
  the shared test vectors; keys stored with `flutter_secure_storage` (Keychain / Android Keystore).

## Consequences
- The mobile folder layout is Flutter's (`apps/mobile/{lib,ios,android}`) instead of separate
  `apps/mobile/ios` and `apps/mobile/android` projects; there is no duplicated UI code.
- Liquid Glass must be validated on a real iOS 26 device or simulator with Xcode 26 — impossible on this
  Windows machine; marked **NOT VERIFIED** until a CI simulator screenshot test or device test exists.
- `minSdk 28` (Android 9), iOS deployment target 18.0.
