# Roadmap

| Phase | Scope | State |
|---|---|---|
| 0 | Environment inspection, toolchain install (Flutter, cmdline-tools, NDK) | done |
| 1 | ADRs, protocol, threat model | done |
| 2 | Core infrastructure: protocol in 3 languages, signaling, auth, logging | done, tested |
| 3 | Windows Host: GUI, pairing, sessions, config, security controls, portable package | done, built |
| 4 | Screen streaming: capture, H.264, WebRTC, diagnostics | host done & tested; phone decode unverified |
| 5 | Remote input, multi-monitor, clipboard | host done & tested; phone gestures unverified |
| 6 | Mobile apps (shared Flutter UI) | implemented, Android built |
| 7 | iOS specifics (18 compat, 26 Liquid Glass, build workflow) | implemented, not built |
| 8 | AI integration | done, verified with Ollama |
| 9 | AI agent | done; needs a capable model for real-world verification |
| 10 | File transfer | done, tested; throughput work pending |
| 11 | WAN reliability (STUN/TURN, reconnect) | implemented; field test pending |
| 12 | UI polish, localisation (vi/en), accessibility | first pass |
| 13 | Optimisation (GPU conversion, SCTP, audio) | planned |
| 14 | CI/CD | workflows written, not yet run |
| 15–16 | Final validation, signed releases | pending device testing |
