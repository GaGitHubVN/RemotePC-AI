# ADR-0001 — Windows Host: .NET 10 + WPF, self-contained portable

## Context
The host needs a polished desktop UI, native Windows APIs (DXGI, Media Foundation, SendInput, UI Automation,
DPAPI, WASAPI), must be portable (no installer, no admin, no runtime install) and must be buildable on the
developer machine (no MSVC) and on GitHub `windows-latest`.

## Options
| Option | Pros | Cons |
|---|---|---|
| **.NET 10 WPF** | Mature, rich styling/animation, full COM/P-Invoke, self-contained publish, testable core in plain `net10.0` | WPF has no built-in Fluent controls on older styles — we build our own theme |
| WinUI 3 | Native Fluent look | Unpackaged/self-contained deployment is more fragile; Windows App SDK runtime bootstrap |
| Electron/Tauri | Web UI | Native capture/encode would still need a native addon; larger footprint |
| C++/Qt | Max control | Needs MSVC/Qt toolchain, slower development, harder to test |

## Decision
`.NET 10` WPF with a custom Fluent-inspired theme. Code layout:

- `RemotePC.Core` (`net10.0`) — protocol, crypto, pairing, permission engine, session state, AI connector,
  agent engine, file-transfer logic. Platform independent, fully unit tested.
- `RemotePC.Platform.Windows` (`net10.0-windows`) — capture, encoding, input injection, UI Automation, DPAPI, system metrics.
- `RemotePC.Host` (WPF) — UI and composition root.

Publish with `-r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.
If a native dependency cannot be bundled, we ship the folder layout instead (documented in `docs/development/WINDOWS_BUILD.md`).

## Consequences
- The UI cannot be tested on CI beyond build + smoke start; logic is kept out of the views.
- System requirements that cannot be bundled: Windows 11 x64, a WDDM GPU driver for hardware encoding
  (otherwise the built-in software H.264 MFT is used), Media Foundation (missing on Windows "N" editions unless the Media Feature Pack is installed).
