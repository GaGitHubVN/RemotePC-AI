using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemotePC.Core.Protocol;
using RemotePC.Core.Security;
using SIPSorcery.Net;

namespace RemotePC.Core.Session;

/// <summary>Sends an envelope over whichever transport (Internet signaling or LAN endpoint) a device last used.</summary>
public delegate bool EnvelopeSender(Envelope env);

public sealed record ResumeTicket(string Sid, string Token, string DeviceId, DateTimeOffset ExpiresAt);

/// <summary>
/// Session lifecycle (PROTOCOL.md §5): session.open / webrtc.offer / webrtc.ice / session.close.
/// Guarantees at most one session per device and enforces the concurrent-session limit.
/// </summary>
public sealed class SessionManager
{
    private readonly PeerRouter _router;
    private readonly ConcurrentDictionary<string, HostSession> _byDevice = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EnvelopeSender> _transport = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ResumeTicket> _resume = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private bool _locked;

    public DeviceStore Devices { get; }
    public IScreenSource Screen { get; }
    public IInputSink Input { get; }
    public RpcRegistry Rpc { get; } = new();
    public Func<CancellationToken, Task<List<RTCIceServer>>>? IceServerProvider { get; set; }
    public event Action? Changed;
    public event Action<string, string>? LogMessage;

    public SessionManager(PeerRouter router, DeviceStore devices, IScreenSource screen, IInputSink input, TimeProvider time)
    {
        _router = router;
        Devices = devices;
        Screen = screen;
        Input = input;
        _time = time;
        devices.Changed += OnDevicesChanged;
        RegisterBuiltins();
    }

    public IReadOnlyCollection<HostSession> Sessions => _byDevice.Values.ToList();

    /// <summary>Lock remote access: closes all sessions and rejects new ones until unlocked.</summary>
    public bool Locked
    {
        get => _locked;
        set
        {
            _locked = value;
            if (value) _ = CloseAllAsync("locked");
            RaiseChanged();
        }
    }

    internal void Log(string category, string message) => LogMessage?.Invoke(category, message);
    internal void RaiseChanged() => Changed?.Invoke();

    internal bool SendToDevice(string deviceId, string typ, string sid, JsonNode payload)
    {
        if (!_transport.TryGetValue(deviceId, out var send) || Devices.Get(deviceId) is null) return false;
        return send(_router.Seal(deviceId, typ, sid, payload));
    }

    /// <summary>Entry point for every authenticated session-related envelope.</summary>
    public async Task HandleAsync(InboundMessage msg, EnvelopeSender replyVia)
    {
        if (msg.Device is null) return; // pairing is handled elsewhere
        _transport[msg.From] = replyVia;
        switch (msg.Typ)
        {
            case "session.open": await OpenAsync(msg).ConfigureAwait(false); break;
            case "webrtc.offer":
                {
                    if (!TryGetSession(msg, out var s)) return;
                    var sdp = (string?)msg.Payload["sdp"];
                    if (string.IsNullOrEmpty(sdp) || sdp.Length > 32_000) return;
                    var answer = await s.HandleOfferAsync(sdp).ConfigureAwait(false);
                    if (answer is null) { await CloseSessionAsync(s, "bad_offer").ConfigureAwait(false); return; }
                    SendToDevice(msg.From, "webrtc.answer", s.Id, new JsonObject { ["sdp"] = answer });
                    break;
                }
            case "webrtc.ice":
                {
                    if (!TryGetSession(msg, out var s)) return;
                    s.AddIceCandidate((string?)msg.Payload["candidate"] ?? "", (string?)msg.Payload["sdpMid"], (ushort)((int?)msg.Payload["sdpMLineIndex"] ?? 0));
                    break;
                }
            case "session.close":
                {
                    if (TryGetSession(msg, out var s)) await CloseSessionAsync(s, "client_closed").ConfigureAwait(false);
                    break;
                }
        }
    }

    private bool TryGetSession(InboundMessage msg, out HostSession session) =>
        _byDevice.TryGetValue(msg.From, out session!) && session.Id == msg.Sid && session.State != SessionState.Closed;

    private async Task OpenAsync(InboundMessage msg)
    {
        var reqId = (string?)msg.Payload["reqId"] ?? "";
        var device = msg.Device!;
        if (_locked) { Reject(msg.From, reqId, "locked"); return; }

        // Resume: same device, valid unexpired token → keep the session id.
        // The previous session may still be open (network switch before the host noticed) or already closed.
        string? sid = null;
        var resume = msg.Payload["resume"];
        if (resume is not null && (string?)resume["sid"] is { } rsid)
        {
            var given = Enc.Utf8((string?)resume["token"] ?? "");
            if (_byDevice.TryGetValue(msg.From, out var live) && live.Id == rsid
                && CryptographicOperations.FixedTimeEquals(Enc.Utf8(live.ResumeToken), given))
            {
                sid = rsid;
            }
            else if (_resume.TryGetValue(rsid, out var ticket) && ticket.DeviceId == msg.From && ticket.ExpiresAt > _time.GetUtcNow()
                && CryptographicOperations.FixedTimeEquals(Enc.Utf8(ticket.Token), given))
            {
                sid = rsid;
            }
        }

        // One session per device: replace any previous one (e.g. after Wi-Fi → 4G switch).
        if (_byDevice.TryGetValue(msg.From, out var existing)) await CloseSessionAsync(existing, "replaced").ConfigureAwait(false);
        if (sid is not null) _resume.TryRemove(sid, out _); // tickets are single use

        var others = _byDevice.Count;
        if (others >= Devices.MaxConcurrentSessions) { Reject(msg.From, reqId, "busy"); return; }

        sid ??= Enc.B64u(RandomNumberGenerator.GetBytes(12));
        var token = Enc.B64u(RandomNumberGenerator.GetBytes(32));
        var ice = IceServerProvider is null ? DefaultIce() : await IceServerProvider(CancellationToken.None).ConfigureAwait(false);
        var config = new RTCConfiguration { iceServers = ice, X_UseRtpFeedbackProfile = true };
        var session = new HostSession(this, sid, msg.From, token, config);
        _byDevice[msg.From] = session;
        Log("session", $"opened {sid} for {device.Name}");

        var displays = new JsonArray(Screen.Displays().Select(d => (JsonNode)new JsonObject
        {
            ["index"] = d.Index, ["name"] = d.Name, ["w"] = d.Bounds.Width, ["h"] = d.Bounds.Height, ["primary"] = d.Primary,
        }).ToArray());
        SendToDevice(msg.From, "session.accept", "", new JsonObject
        {
            ["reqId"] = reqId,
            ["sid"] = sid,
            ["token"] = token,
            ["permissions"] = new JsonArray(PermissionNames.ToNames(device.Permissions).Select(n => (JsonNode)n).ToArray()),
            ["iceServers"] = JsonSerializer.SerializeToNode(ice.Select(s => new { urls = s.urls.Split(','), s.username, s.credential })),
            ["displays"] = displays,
            ["hostName"] = Environment.MachineName,
        });
        Devices.Update(msg.From, d => d with { LastSeen = _time.GetUtcNow() });
        _ = ExpireIfNotConnectedAsync(session);
        RaiseChanged();
    }

    private async Task ExpireIfNotConnectedAsync(HostSession s)
    {
        await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        if (s.State == SessionState.Negotiating) await CloseSessionAsync(s, "connect_timeout").ConfigureAwait(false);
    }

    private static List<RTCIceServer> DefaultIce() => [new RTCIceServer { urls = "stun:stun.l.google.com:19302" }];

    private void Reject(string deviceId, string reqId, string reason)
    {
        Log("session", $"rejected {deviceId}: {reason}");
        SendToDevice(deviceId, "session.reject", "", new JsonObject { ["reqId"] = reqId, ["reason"] = reason });
    }

    public async Task CloseSessionAsync(HostSession s, string reason)
    {
        if (!_byDevice.TryRemove(new KeyValuePair<string, HostSession>(s.DeviceId, s))) { s.Teardown(); return; }
        s.Teardown();
        if (reason is not ("revoked" or "locked" or "emergency"))
            _resume[s.Id] = new ResumeTicket(s.Id, s.ResumeToken, s.DeviceId, _time.GetUtcNow() + TimeSpan.FromMinutes(10));
        SendToDevice(s.DeviceId, "session.close", s.Id, new JsonObject { ["reason"] = reason });
        Log("session", $"closed {s.Id}: {reason}");
        RaiseChanged();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task CloseAllAsync(string reason)
    {
        foreach (var s in _byDevice.Values.ToList()) await CloseSessionAsync(s, reason).ConfigureAwait(false);
        if (reason is "emergency" or "locked") _resume.Clear();
    }

    private void OnDevicesChanged()
    {
        foreach (var s in _byDevice.Values.ToList())
        {
            if (Devices.Get(s.DeviceId) is null)
            {
                _router.Forget(s.DeviceId);
                _ = CloseSessionAsync(s, "revoked");
            }
        }
        foreach (var t in _resume.Values.Where(t => Devices.Get(t.DeviceId) is null).ToList()) _resume.TryRemove(t.Sid, out _);
    }

    private void RegisterBuiltins()
    {
        Rpc.Register("ping", Permission.None, (p, _) => Task.FromResult<JsonNode?>(new JsonObject { ["t"] = p?["t"]?.DeepClone(), ["hostTime"] = _time.GetUtcNow().ToUnixTimeMilliseconds() }));
        Rpc.Register("diag.rtt", Permission.None, (p, ctx) =>
        {
            if ((double?)p?["ms"] is { } ms and >= 0 and < 60_000) ctx.Session.SetInputRtt(ms);
            return Task.FromResult<JsonNode?>(null);
        });
        Rpc.Register("display.list", Permission.ViewScreen, (_, ctx) => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["current"] = ctx.Session.DisplayIndex,
            ["displays"] = new JsonArray(Screen.Displays().Select(d => (JsonNode)new JsonObject { ["index"] = d.Index, ["name"] = d.Name, ["w"] = d.Bounds.Width, ["h"] = d.Bounds.Height, ["primary"] = d.Primary }).ToArray()),
        }));
        Rpc.Register("display.select", Permission.ViewScreen, (p, ctx) =>
        {
            var idx = (int?)p?["index"] ?? 0;
            if (idx < 0 || idx >= Screen.Displays().Count) throw new RpcException("bad_request", "no such display");
            ctx.Session.StartStream(idx);
            return Task.FromResult<JsonNode?>(new JsonObject { ["current"] = idx });
        });
        Rpc.Register("stream.keyframe", Permission.ViewScreen, (_, ctx) => { ctx.Session.RequestKeyFrame(); return Task.FromResult<JsonNode?>(null); });
        Rpc.Register("stream.set", Permission.ViewScreen, (p, ctx) =>
        {
            var preset = Enum.TryParse<Media.QualityPreset>((string?)p?["preset"], true, out var pr) ? pr : Media.QualityPreset.Auto;
            var s = Media.StreamSettings.For(preset);
            if (preset == Media.QualityPreset.Custom && p is not null)
            {
                s = s with
                {
                    MaxWidth = (int?)p["maxWidth"] ?? s.MaxWidth, MaxHeight = (int?)p["maxHeight"] ?? s.MaxHeight,
                    Fps = (int?)p["fps"] ?? s.Fps, TargetKbps = (int?)p["targetKbps"] ?? s.TargetKbps, MaxKbps = (int?)p["maxKbps"] ?? s.MaxKbps,
                    RenderCursor = (bool?)p["cursor"] ?? s.RenderCursor,
                };
            }
            ctx.Session.SetStreamSettings(s);
            return Task.FromResult<JsonNode?>(new JsonObject { ["preset"] = preset.ToString() });
        });
        Rpc.Register("stream.stats", Permission.ViewScreen, (_, ctx) =>
        {
            var st = ctx.Session.Stats;
            return Task.FromResult<JsonNode?>(st is null ? new JsonObject() : JsonSerializer.SerializeToNode(st, ProtocolJson.Options));
        });
    }
}
