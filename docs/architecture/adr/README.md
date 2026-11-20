# Architecture Decision Records

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-windows-host-dotnet-wpf.md) | Windows Host on .NET 10 + WPF, self-contained portable | Accepted |
| [0002](0002-screen-pipeline.md) | DXGI Desktop Duplication → Media Foundation H.264 → WebRTC (SIPSorcery) | Accepted |
| [0003](0003-mobile-flutter-native-glass.md) | Flutter for iOS/Android, SwiftUI platform views for iOS 26 Liquid Glass | Accepted |
| [0004](0004-signaling-e2e-envelope.md) | Untrusted relay signaling with end-to-end encrypted envelopes | Accepted |
| [0005](0005-signaling-runtime.md) | TypeScript signaling core, Node adapter + Cloudflare Durable Objects adapter; coturn for TURN | Accepted |
| [0006](0006-local-ai-agent.md) | Local AI connector (Ollama + OpenAI-compatible) and permissioned agent tool system | Accepted |
| [0007](0007-ios-build-github-actions.md) | iOS builds on GitHub-hosted macOS runners, unsigned IPA from device build | Accepted |

Toolchain verified on the dev machine on 2026-11-20: .NET SDK 10.0.115, Node 26.13, Flutter 3.50.1 / Dart 3.14,
Android SDK platform 37 / build-tools 36, Android Studio JBR, Rust 1.99, gh 2.103. No MSVC toolchain is installed — this
is one reason ADR-0001/0002 avoid a custom C++ build.
