# RemotePC AI Protocol — v1

Status: **IMPLEMENTED** (signaling + envelope + pairing). Changes require a version bump or a backwards-compatible extension.

All binary values in JSON are **base64url without padding** unless noted. All times are Unix epoch milliseconds.
Three independent implementations exist and are cross-checked against the test vectors in `vectors/`:

| Language | Location | Used by |
|---|---|---|
| TypeScript | `backend/signaling/src/protocol/` | signaling server, vector generator |
| C# | `apps/windows-host/src/RemotePC.Core/Protocol/` | Windows Host |
| Dart | `apps/mobile/lib/core/protocol/` | iOS / Android client |

---

## 1. Device identity

Every device (host or client) generates on first launch, and stores in the platform secret store
(Windows DPAPI, iOS Keychain, Android Keystore-backed storage):

- an **Ed25519** signing key pair `(edSk, edPk)` — authenticates the device to the signaling server;
- an **X25519** key pair `(xSk, xPk)` — derives end-to-end keys with paired peers.

```
deviceId = lower(base32_rfc4648_nopad(SHA-256(edPk)))[0:26]     // 130 bits, e.g. "mfrggzdfmztwq2lknnwg23tpob"
```

A device ID is not a secret, but it is unguessable and only known to paired peers and the signaling server.

## 2. Signaling server protocol

Transport: WebSocket, JSON text frames, max frame 64 KiB. Production is `wss://` (TLS). The LAN endpoint
served by the Windows Host is plain `ws://` and relies entirely on the end-to-end envelope (§3) — see ADR-0004.

### 2.1 Authentication handshake

```
S → D  {"t":"challenge","v":1,"nonce":"<32 random bytes>","time":<ms>}
D → S  {"t":"hello","v":1,"role":"host"|"client","id":"<deviceId>","edPk":"<32B>","sig":"<64B>"}
S → D  {"t":"welcome","v":1,"id":"<deviceId>","time":<ms>}
```

`sig = Ed25519.sign(edSk, UTF8("remotepc-signal-v1\n" + nonce + "\n" + id + "\n" + role))`
where `nonce` is the exact base64url string the server sent.

The server MUST verify that `id == deviceId(edPk)` and that the signature is valid. Any message other than
`hello` before `welcome` closes the socket (code 4001). A hello must arrive within 10 s (code 4002).
If a second socket authenticates with the same `id`, the older socket is closed (code 4003, "replaced").

### 2.2 Messages after authentication

| Direction | Message | Meaning |
|---|---|---|
| D → S | `{"t":"relay","to":id,"env":Envelope}` | forward an end-to-end envelope |
| S → D | `{"t":"relay","from":id,"env":Envelope}` | delivered envelope (`from` is the authenticated sender) |
| D → S | `{"t":"presence","ids":[id,…]}` (≤ 32) | ask which peers are online |
| S → D | `{"t":"presence","online":[id,…]}` | |
| D → S | `{"t":"turn"}` | request short-lived TURN credentials |
| S → D | `{"t":"turn","iceServers":[…],"ttl":seconds}` | coturn REST-API credentials (§2.3) |
| D → S | `{"t":"ping"}` | app-level heartbeat |
| S → D | `{"t":"pong","time":ms}` | |
| S → D | `{"t":"error","code":str,"ref":str?,"msg":str?}` | `peer_offline`, `rate_limited`, `bad_request`, `too_large` |

The server only forwards; it cannot read or forge envelopes. It MUST check `env.from == authenticated id`
and `env.to == relay.to`. Rate limit: token bucket per socket (default 40 msg/s, burst 120) and per IP for
handshakes (default 20/min). The server never receives screen, input, file or AI data.

### 2.3 TURN credentials

The server and coturn share `TURN_SECRET` (coturn `use-auth-secret`). Issued per authenticated device:

```
username   = "<expiryUnixSeconds>:<deviceId>"
credential = base64_std(HMAC-SHA1(TURN_SECRET, username))
ttl        = 3600 by default
```

## 3. End-to-end envelope

```jsonc
{
  "v": 1,
  "typ": "pair.request",      // message type (§4, §5)
  "from": "<deviceId>",
  "to": "<deviceId>",
  "sid": "",                   // session id or "" (outside a session)
  "seq": 1763630000000,        // per (sender, sid) strictly increasing; senders use max(last + 1, now_ms)
                               // so a restarted device never reuses a value the peer already accepted
  "ts": 1763630000000,
  "n": "<12 random bytes>",
  "kx": {"edPk": "…", "xPk": "…"},   // ONLY on pair.request; omitted otherwise
  "ct": "<AES-256-GCM ciphertext || 16-byte tag>"
}
```

**Key derivation** (identical for both directions):

```
shared = X25519(mySk, peerXPk)
salt   = SHA-256(UTF8(min(idA,idB) + "|" + max(idA,idB)))       // ordinal string comparison
K      = HKDF-SHA256(ikm=shared, salt=salt, info=UTF8("remotepc/v1/envelope"), L=32)
```

**AAD** is the UTF-8 encoding of:

```
"remotepc-env-v1|" + v + "|" + typ + "|" + from + "|" + to + "|" + sid + "|" + seq + "|" + ts + "|" + n + "|" + (kx.edPk or "") + "|" + (kx.xPk or "")
```

`ct = AES-256-GCM(K, nonce=n, plaintext=UTF8(JSON(payload)), aad=AAD)`.

**Receiver checks**, in order — any failure drops the envelope silently (and is logged locally):

1. `v == 1`, `to == myId`, field types and sizes valid (`ct` ≤ 48 KiB).
2. Peer key: from the paired-device store; for `pair.request` from `kx` (and `deviceId(kx.edPk) == from`).
3. `|ts − now| ≤ 120 000 ms`.
4. `n` not seen from this peer within the last 10 minutes (replay cache).
5. `seq` > last accepted `seq` for `(from, sid)`.
6. AEAD decryption succeeds.

## 4. Pairing

Host creates a **pairing offer** valid for 5 minutes, single use, max 5 failed proofs:

- **QR mode** — `secret` = 32 random bytes. QR text:
  `remotepc://pair?v=1&h=<hostId>&e=<hostEdPk>&x=<hostXPk>&s=<secret>&n=<urlencoded host name>&sig=<wss url, urlencoded>&lan=<ip:port,…>&exp=<ms>`
- **Code mode** (LAN discovery, §6) — `secret = UTF8(code)`, where `code` is 10 characters from
  `ABCDEFGHJKMNPQRSTUVWXYZ23456789` displayed as `XXXXX-XXXXX` (≈ 49 bits); the dash is not part of the secret.

```
transcript = UTF8("remotepc-pair-v1|" + hostId + "|" + clientId + "|" + clientEdPk + "|" + clientXPk + "|" + hostEdPk + "|" + hostXPk + "|" + pairNonce)
proofKey   = HKDF-SHA256(ikm=secret, salt=empty, info=UTF8("remotepc/v1/pair-proof"), L=32)
proof      = HMAC-SHA256(proofKey, transcript)
verifyCode = decimal(uint32_be(SHA-256(transcript)[0:4]) mod 1_000_000), zero-padded to 6 digits
```

1. Client → Host `pair.request` (envelope with `kx`): `{"pairNonce","proof","deviceName","platform","appVersion"}`
2. Host verifies offer validity and `proof` (constant-time), then shows an **approval dialog** with the
   device name, platform and `verifyCode`; the phone displays the same `verifyCode`. The user compares and
   chooses the permissions to grant. (Comparison defeats a MITM that substitutes keys in code mode.)
3. Host → Client `pair.result`: `{"ok":true,"hostName","permissions":[…]}` or `{"ok":false,"reason"}`.
   `reason` ∈ `expired`, `bad_proof`, `rejected`, `locked`.

## 5. Sessions

```
C → H  session.open    {"reqId","resume":{"sid","token"}?,"client":{"screen":{"w","h"},"codecs":["H264",…]}}
H → C  session.accept  {"reqId","sid","token","permissions":[…],"iceServers":[…],"displays":[…]}
H → C  session.reject  {"reqId","reason":"revoked"|"busy"|"locked"|"not_paired"}
C → H  webrtc.offer    {"sdp"}            (sid set; client offers: recvonly video, data channels)
H → C  webrtc.answer   {"sdp"}
*      webrtc.ice      {"candidate","sdpMid","sdpMLineIndex"}
*      session.close   {"reason"}
```

- The host keeps **at most one session per client device**; a new `session.open` from the same device
  closes the previous one (no duplicates after network switches).
- `token` is 32 random bytes, valid 10 minutes after disconnect, for resume without re-pairing.
  Resume re-uses permissions but always creates a new WebRTC connection; queued input is never replayed.
- WebRTC DTLS fingerprints are inside encrypted envelopes, so the DTLS channel is bound to the paired keys.

### 5.1 Data channels

| Label | Reliability | Payload |
|---|---|---|
| `ctl` | ordered, reliable | JSON-RPC style control/AI/file-system messages (§5.2) |
| `mv` | unordered, `maxRetransmits=0` | pointer moves `{"x","y","q"}` — host drops `q` ≤ last |
| `in` | ordered, reliable | buttons, wheel, keys, text (§5.3) |
| `file` | ordered, reliable, binary | file-transfer chunks (§5.4) |

Coordinates `x`,`y` are normalized `[0,1]` inside the **current display's video frame** (client removes
letterbox and viewport zoom). The host maps them into that monitor's physical-pixel rectangle on the
virtual desktop.

### 5.2 `ctl` messages

```
request   {"id":n,"m":"method.name","p":{…}}
response  {"id":n,"r":{…}}   or   {"id":n,"e":{"code":"…","msg":"…"}}
event     {"ev":"event.name","p":{…}}
```

Every method declares a required permission; the **host** enforces it (`e.code = "forbidden"`).
Methods are listed in `docs/architecture/RPC.md`.

### 5.3 `in` messages

```
{"k":"btn","b":"left|right|middle","d":true|false,"x","y"}
{"k":"wheel","dx","dy"}                      // in WHEEL_DELTA units (120 = one notch)
{"k":"key","vk":<Windows VK>,"d":true|false} // physical key
{"k":"text","s":"Xin chào"}                 // Unicode text, sent with KEYEVENTF_UNICODE
{"k":"combo","vks":[0x11,0x43]}              // press in order, release in reverse
```

### 5.4 File chunks

Binary frame: `[16-byte transferId][8-byte big-endian offset][payload ≤ 60 KiB]`.
The transfer is opened with `file.begin` on `ctl` (name, size, sha256, direction) and finished with
`file.end`; the receiver verifies SHA-256 before moving the file from the temp name to its final name.

## 6. LAN discovery

Clients broadcast UDP to port **45821** `{"t":"discover","v":1}`; hosts reply unicast
`{"t":"host","v":1,"id","name","edPk","xPk","ws":port}`. The LAN signaling endpoint is
`ws://<ip>:<port>/signal` with the same protocol as §2 (the host is its own server).
Discovery replies are unauthenticated hints; authenticity comes from paired keys (§3) or the pairing proof plus verify code (§4).
