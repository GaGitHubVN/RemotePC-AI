# `ctl` RPC methods

Every method is checked on the host against the calling device's live permissions (`forbidden` otherwise).

| Method | Permission | Params → Result |
|---|---|---|
| `ping` | — | `{t}` → `{t, hostTime}` |
| `diag.rtt` | — | `{ms}` (phone-measured RTT, shown on the PC) |
| `sys.info` | — | → `{hostName, os, version, permissions[], remoteInput}` |
| `sys.metrics` | — | → `{cpu, ramUsedGb, ramTotalGb}` |
| `display.list` / `display.select` | view_screen | `{index}` switches monitor without reconnecting |
| `stream.set` | view_screen | `{preset}` or Custom `{maxWidth,maxHeight,fps,targetKbps,maxKbps,cursor}` |
| `stream.keyframe` / `stream.stats` | view_screen | |
| `screen.capture` | view_screen | `{display?, maxWidth?}` → `{png}` (base64) |
| `clipboard.get` / `clipboard.set` | clipboard | `{text}` |
| `ai.status` / `ai.models` / `ai.select` | use_ai | providers, models (real metadata or null), selection |
| `ai.chat` | use_ai | `{messages[{role,content,images?}], options?}`; events `ai.delta {d}` / `{t}` (thinking); result `{content, model, usage}` |
| `ai.extract` | use_ai | `{path, maxChars}` → `{supported, kind, text, truncated, message}` |
| `agent.run` | ai_agent | `{request}` → task |
| `agent.list` / `agent.status` / `agent.image` / `agent.templates` | ai_agent | |
| `agent.approve` | ai_agent | `{taskId, approvalId, approved, plan?}` |
| `agent.pause` | ai_agent | `{taskId, pause}` |
| `agent.cancel` | — | always allowed for the task owner |
| `file.begin` / `file.status` / `file.end` | send_files | upload control (`{name,size,sha256,folder?}` → `{id, offset, chunk}`) |
| `file.download` | receive_files | `{path, offset?}` → `{id,name,size,sha256}`; chunks on `file`; event `file.done` / `file.failed` |
| `file.cancel` | — | |
| `fs.roots` / `fs.list` / `fs.stat` / `fs.search` | receive_files | sandboxed virtual paths |
| `fs.mkdir` | send_files | |
| `fs.rename` / `fs.delete` | modify_files | delete requires `confirmed: true`; goes to the Recycle Bin when possible |
| `rpc.cancel` | — | `{id}` cancels a running request (e.g. stop generation) |

Agent events pushed to the owning phone: `agent.state`, `agent.plan`, `agent.step`, `agent.progress`, `agent.image`, `agent.approval`.
