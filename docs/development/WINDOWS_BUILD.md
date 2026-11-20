# Building the Windows Host

## Prerequisites (developer machine only)

```powershell
pwsh scripts/setup/check-env.ps1     # reports what is installed / missing
```

- .NET SDK 10.0.x (`winget install Microsoft.DotNet.SDK.10`)
- Node.js 22.6+ (only for tests that start the real signaling server)
- No Visual Studio / MSVC is required.

## Build, test, publish

```powershell
cd apps/windows-host
dotnet build RemotePC.slnx -c Release                       # warnings are errors
dotnet test tests/RemotePC.Core.Tests --filter "Category!=LocalAI"
dotnet test tests/RemotePC.Platform.Windows.Tests            # needs an interactive desktop + GPU/MF
dotnet test tests/RemotePC.Core.Tests --filter "Category=LocalAI"   # needs Ollama with a tool-capable model
```

Portable release (tests → publish → smoke test from an isolated temp folder → zip → SHA-256):

```powershell
pwsh scripts/release/package-windows.ps1
# → releases/windows/RemotePC-AI-Windows-x64-Portable.zip + SHA256SUMS.txt
```

## What the package contains

- `RemotePC-AI.exe` — single self-contained executable (~90 MB): .NET runtime, WPF, SIPSorcery, BouncyCastle, PdfPig,
  OpenXML, QRCoder are all embedded. Native libraries extract to a temp folder on first run (`IncludeNativeLibrariesForSelfExtract`).
- On first run it creates `data\` next to the exe (or `%LOCALAPPDATA%\RemotePC-AI` if the folder is read-only):
  `identity.dat`, `devices.dat` (DPAPI-encrypted), `config.json`, `logs\`.

## System components that cannot be bundled

| Component | Why | Check |
|---|---|---|
| Media Foundation | H.264 encoder MFTs ship with Windows | `Get-WindowsCapability -Online -Name Media.MediaFeaturePack*` on N editions |
| GPU driver | hardware encoders (QSV/NVENC/AMF) | Local AI page → none needed; Sessions page shows encoder + `HW` |
| DXGI 1.2 | Desktop Duplication | Windows 8+ (always on Windows 11) |
| Windows Defender Firewall | inbound LAN port 45822 / UDP 45821 | Windows asks on first run; allow on **Private** networks |

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `BG1002 … .baml cannot be found` on publish | stale `obj` from a build with a different RID — delete `src/RemotePC.Host/obj` (the package script does) |
| "No usable H.264 Media Foundation encoder" | Windows N without Media Feature Pack, or broken GPU driver; software MFT is used when available |
| Black screen while UAC prompt is open | Secure desktop cannot be captured by design; stream resumes automatically |
| Phone cannot reach PC on LAN | firewall rule for `RemotePC-AI.exe` on Private networks; both devices on the same subnet; guest Wi-Fi often isolates clients |
