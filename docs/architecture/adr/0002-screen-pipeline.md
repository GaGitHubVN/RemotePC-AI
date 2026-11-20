# ADR-0002 — Screen pipeline

## Decision
```
DXGI Desktop Duplication (D3D11 texture, BGRA, dirty-frame driven)
  → GPU scale/convert to NV12 (D3D11 Video Processor)
  → Media Foundation H.264 encoder MFT (hardware first: Intel QSV / NVENC / AMF; fallback: Microsoft software H.264 MFT)
      low-latency mode, CBR/VBR target from adaptive controller, no B-frames, periodic + on-demand IDR
  → Annex-B access units
  → SIPSorcery RTCPeerConnection (H.264 RTP packetization, SRTP/DTLS, ICE/STUN/TURN)
  → mobile libwebrtc (flutter_webrtc) hardware H.264 decode → Texture render
```

- **Why Desktop Duplication instead of Windows.Graphics.Capture:** works without a WinRT projection, no capture
  border, supports per-monitor output and cursor shape metadata, available on all Windows 11 machines.
  WGC remains a possible future backend (e.g. per-window capture).
- **Why H.264 first:** the only codec with near-universal hardware decode on iOS 18+ and Android 9+ and a
  hardware encoder on all three GPU vendors. HEVC/AV1 are negotiated only when both sides advertise them;
  the encoder factory enumerates MFTs (`MFTEnumEx`, `MFT_ENUM_FLAG_HARDWARE`) and never assumes availability.
- **Why SIPSorcery:** pure C# WebRTC stack (ICE, DTLS-SRTP, SCTP data channels, H.264 packetiser), actively
  maintained (10.0.x, 2026), no native libwebrtc build (which would need MSVC + depot_tools).
  Trade-off: congestion control is simpler than Google's; we implement our own adaptive bitrate controller
  from RTCP receiver reports (loss, jitter, RTT) — see `AdaptiveBitrateController`.

## Latency instrumentation
Each frame carries timestamps (capture, encode start/end, send). The client reports decode/render
statistics from `getStats()` (`framesDecoded`, `totalDecodeTime`, `jitterBufferDelay`) and input round-trip
via `ctl` `ping` echo. Network one-way latency is **estimated** as RTT/2 (clock skew makes true one-way
measurement impossible without clock sync) — the diagnostics screen labels it as an estimate.

## Rejected
- JPEG/PNG screenshots over WebSocket (high bandwidth, no inter-frame compression) — explicitly ruled out.
- FFmpeg bundling — LGPL/GPL licensing of encoders and binary size; MF is already on the OS.
