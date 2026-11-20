# Local AI setup

RemotePC AI never ships a model and never downloads one by itself. It uses the AI runtime you run on the PC.

## Supported runtimes (auto-discovered on this PC only)

| Runtime | Default endpoint | API used | Metadata available |
|---|---|---|---|
| Ollama | `http://127.0.0.1:11434` (honours `OLLAMA_HOST` if it points to this PC) | `/api/tags`, `/api/show`, `/api/chat` | context length, tools, vision, size, quantisation |
| LM Studio (server on) | `http://127.0.0.1:1234` | OpenAI-compatible `/v1/models`, `/v1/chat/completions` | context length (LM Studio extension); tools/vision **unknown** |
| llama.cpp `llama-server` | `http://127.0.0.1:8080` | OpenAI-compatible | usually only the model id |
| Other OpenAI-compatible | add in `config.json` → `ai.customEndpoints` | OpenAI-compatible | depends |

"Unknown" means the server did not report it — the app does not guess.

## Ollama quick start

```powershell
winget install Ollama.Ollama
ollama pull qwen2.5:7b        # tool calling, good for the agent (≈4.7 GB)
ollama pull llama3.2-vision   # vision model for screenshot analysis (≈7.9 GB)
```

Then on the PC: **Local AI → Scan providers → Select model → Test inference**. The phone can also switch models
(model picker in the chat header).

## Which model for what

| Use | Needs | Notes from our tests |
|---|---|---|
| Chat | any model | `qwen2.5:0.5b` streams at ~115 tok/s on an Iris Xe laptop |
| AI agent | `tools` capability | `qwen2.5:0.5b` calls tools but chose the wrong tool for "create a folder" — use ≥ 7B (e.g. `qwen2.5:7b`, `qwen3:8b`, `llama3.1:8b`). Larger models are **NOT VERIFIED** yet. |
| Screenshot analysis | `vision` capability | images are refused for models that report no vision |

## Resource limits

- *Max concurrent AI requests* (Local AI page) defaults to 1 so inference cannot starve the remote session.
- The phone may set temperature/top-p/max tokens within bounds; context size is controlled only on the PC.

## Agent permissions

- The phone needs **Let AI operate Windows** (`ai_agent`) granted on the PC.
- Security → *AI agent policy*: auto-approve reads (default on), auto-approve writes (default off), review plan.
- Sensitive actions (delete, overwrite, Win-key combos) always ask on the phone, regardless of policy.
- There is no shell/command tool. File tools only see the allowed folders (Desktop, Documents, Downloads, Received + extra roots).
