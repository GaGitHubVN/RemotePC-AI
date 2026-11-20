# Troubleshooting

| Symptom | What to check |
|---|---|
| Phone says "PC not reachable" | Same Wi-Fi? Firewall allowed RemotePC-AI on **Private** networks? Guest/hotel Wi-Fi isolates clients. For other networks configure a `wss://` signaling server. |
| Pairing: "Wrong code" / "locked" | Codes are case-insensitive, 10 characters; after 5 wrong tries create a new code on the PC. |
| Pairing: "The PC owner rejected this phone" | The approval dialog on the PC was declined or timed out (2 min). |
| Phone paired before but now "removed on the PC" | The device was revoked, or the PC's `data\` folder was moved to another Windows account (DPAPI) — pair again. |
| Connected but black screen | UAC/lock screen is up (secure desktop cannot be captured); display mode change in progress; check PC Sessions page for the encoder. |
| Low frame rate | PC Sessions page shows encoder and fps; choose *Low Latency* or *Data Saver*; check `relay (TURN)` path in phone diagnostics. |
| Clicks land in the wrong place | Use **Trackpad** mode or reset zoom; report display scaling + monitor layout if direct-touch mapping is off. |
| Keys stuck after disconnect | The host releases every key/button it pressed when a session ends; press and release the key on the PC once. |
| AI: "No AI model selected" | PC → Local AI → Scan providers → Select model. |
| AI: model cannot use images / tools | The provider reported no `vision` / `tools` capability — pick another model. |
| Agent does the wrong thing | Small models are unreliable planners; use ≥7B tool models; enable *Review plan*; keep write auto-approve off. |
| File transfer slow | Known limitation of the host SCTP stack (~200 KiB/s measured on loopback). |
| Logs | PC: Activity Logs page or `data\logs\activity-*.jsonl`; crashes: `data\crash.log`. |
