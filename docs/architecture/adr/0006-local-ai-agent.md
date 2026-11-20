# ADR-0006 — Local AI connector and agent

## Decision
- Providers: **Ollama** (`/api/tags`, `/api/show`, `/api/chat` NDJSON streaming, native `tools`), and any
  **OpenAI-compatible** server — LM Studio (`:1234`), llama.cpp `llama-server` (`:8080`), vLLM, etc.
  (`/v1/models`, `/v1/chat/completions` with SSE). Discovery probes loopback ports only; remote endpoints must be
  added manually by the PC owner.
- Model metadata is reported only when the provider returns it (Ollama `/api/show` gives `capabilities`
  such as `tools`/`vision` and context length; OpenAI-compatible servers usually don't — shown as "unknown").
- The phone never talks to the model directly: requests go over the authenticated `ctl` data channel and the host
  calls the loopback API.
- **Agent:** typed tool registry (JSON Schema per tool) → permission engine (per device, per tool class:
  read / write / sensitive) → executor with timeout + cancellation → observation → verification step.
  Models with native tool calling use it; others are not offered agent mode (no fragile text parsing in v1).
- Untrusted content (files, screen OCR/vision output, web pages) is wrapped as data in tool results and the
  system prompt states it carries no authority; permission checks never depend on model output.
- Shell execution is not a tool by default. "Advanced Command Mode" is off by default, host-side only, and
  every command needs explicit approval with the exact command line shown.
