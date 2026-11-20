# ADR-0005 — Signaling runtime and TURN

## Decision
- `backend/signaling` is TypeScript. `src/core/` contains transport-agnostic logic (handshake, routing, rate
  limiting, TURN credentials, validation). Two adapters:
  - `src/node/` — `ws` server for local development and self-hosting (Docker image provided).
  - `src/worker/` — Cloudflare Worker + one Durable Object (WebSocket hibernation API) that holds all sockets
    of a routing shard. Workers/DO support WebSocket servers; they do **not** provide UDP TURN.
- TURN: `coturn` on a VPS via Docker Compose (`backend/turn`), `use-auth-secret` with credentials minted by the
  signaling server. Cloudflare's managed TURN (Realtime TURN) is a paid alternative and can be configured
  through the same `iceServers` setting.

## Cost notes
Signaling is tiny (a few KB per session). TURN relays the full video stream: at 4 Mbit/s an hour of relayed
session ≈ 1.8 GB of egress. Most home/mobile NATs connect directly via STUN; carrier-grade/symmetric NAT on mobile
networks frequently needs TURN. Without a TURN server some WAN connections **will fail**, and the app says so.
