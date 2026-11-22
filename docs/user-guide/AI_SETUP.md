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

## Cloud AI (API key or account sign-in)

Besides local runtimes, the PC can use cloud AI accounts. Add them on the PC (**AI → Cloud AI → Add cloud AI…**) or
from the phone (**AI → model picker → Add cloud AI**). Either way the key lives only on the PC, encrypted with DPAPI;
it is never stored on the phone and never sent back to it.

| Service | API key | Account sign-in |
|---|---|---|
| Claude (Anthropic) | yes — platform.claude.com → API keys | no |
| OpenAI | yes — platform.openai.com/api-keys | no |
| Google Gemini | yes — aistudio.google.com/apikey | no |
| OpenRouter (Claude, GPT, Gemini, Llama… in one account) | yes | **yes** |
| Groq, DeepSeek, xAI Grok, Mistral | yes | no |
| Any OpenAI-compatible `https://` endpoint | yes | no |

**Account sign-in** uses OpenRouter's official OAuth PKCE login:
- on the PC, the browser opens, you sign in and approve, and the browser returns to a one-shot listener on
  `localhost` — nothing to copy;
- on the phone, the browser opens the same page; after you approve, it shows a code that you paste into the app
  (valid 10 minutes).
The PC creates the PKCE verifier and exchanges the code itself, so the resulting key never touches the phone. Usage is
billed to your OpenRouter account; you can limit or revoke the key at openrouter.ai/keys.

Why there is no "Sign in with ChatGPT / Claude / Gemini": those consumer subscriptions (ChatGPT Plus, Claude Pro/Max,
Gemini app) do not offer a login that third-party apps may use for API access. Using them would mean imitating the
vendors' own apps, which their terms forbid. Use an API key from the developer platform, or OpenRouter sign-in.

With cloud AI, the chat text — and, for agent tasks, screenshots and file contents the agent reads — is sent to that
provider. The PC shows every cloud provider as "remote".

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
