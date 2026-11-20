# Network setup

## LAN (works out of the box)

- The host listens on **TCP 45822** (`ws://<pc-ip>:45822/signal`) and answers discovery on **UDP 45821**.
- Allow `RemotePC-AI.exe` on **Private** networks when Windows Firewall asks (or: Settings → Firewall → Allow an app).
- QR codes include the PC's LAN addresses, so iPhones (which cannot broadcast without a special entitlement) still find it.
- Media goes peer-to-peer on the LAN (host ICE candidates); no Internet needed.

## Across networks (WAN)

You need a **signaling server** (tiny) and, for many mobile networks, a **TURN relay** (bandwidth-heavy).
No port forwarding is needed on the home router.

### Option A — one VPS with Docker Compose (signaling + TURN + automatic TLS)

```bash
git clone <your repo> && cd RemotePC-AI/backend/turn
cp .env.example .env               # set PUBLIC_HOST (DNS A record → VPS) and TURN_SECRET (openssl rand -base64 48)
cp turnserver.conf.example turnserver.conf   # same secret, VPS public IP, realm
mkdir certs                         # fullchain.pem + privkey.pem for turns:// (e.g. copy from Caddy or certbot)
docker compose up -d
curl https://$PUBLIC_HOST/healthz
```

Firewall on the VPS: TCP 80/443 (Caddy), UDP+TCP 3478, TCP 5349, UDP 49160–49999.

On the PC: **Settings → Signaling server** = `wss://<PUBLIC_HOST>/signal`, Save. Pair (or re-pair) phones so the QR carries the URL.

### Option B — Cloudflare Workers + Durable Objects (signaling only)

```powershell
cd backend/signaling
npx wrangler login
npx wrangler secret put TURN_SECRET      # only if you also run coturn
npx wrangler deploy
```

URL: `wss://remotepc-signaling.<account>.workers.dev/signal`. Workers cannot relay UDP media; for TURN use coturn
(Option A without the signaling service) or Cloudflare's managed TURN (paid). Runtime behaviour of the Worker is
**NOT VERIFIED** (bundling is verified in CI).

## How a connection is chosen

1. Phone tries every LAN endpoint (3 s), then the Internet signaling URL.
2. ICE gathers host, server-reflexive (STUN) and relay (TURN) candidates; WebRTC picks the best working pair.
3. Diagnostics on the phone show the path: `direct (LAN)`, `direct (STUN)` or `relay (TURN)`.

Without TURN, connections between two symmetric/carrier-grade NATs **will fail** — the app reports it instead of hanging.

## TURN cost estimate

TURN relays the whole stream: 4 Mbit/s ≈ 1.8 GB per hour of relayed session. A 1 TB/month VPS covers ~550 relayed hours.
Direct (STUN) connections cost nothing on the VPS.

## Security notes

- Signaling over the Internet must be `wss://` — the apps refuse plain `ws://` to public hosts.
- TURN credentials are per device, expire after 1 hour, and are only issued to authenticated devices.
- `denied-peer-ip` rules stop the TURN server from being used to reach private networks.

## Debugging

| Check | How |
|---|---|
| Signaling up | `curl https://<host>/healthz` → `{"ok":true,"connections":N}` |
| PC registered | PC Dashboard → *Internet signaling: Online (RTT)* |
| Phone path | phone → PC → ⋮ → Connection diagnostics |
| TURN works | https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/ with a minted credential (relay candidates appear) |
| Logs | PC → Activity Logs (category `network`, `session`); VPS → `docker compose logs -f` |
