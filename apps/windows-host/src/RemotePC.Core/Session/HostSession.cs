using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using RemotePC.Core.Media;
using RemotePC.Core.Security;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RemotePC.Core.Session;

public enum SessionState { Negotiating, Connected, Closed }

/// <summary>
/// One remote session with one paired device: a WebRTC peer connection (video + data channels).
/// All permission checks read the device record live from <see cref="DeviceStore"/>, so revocation or
/// permission changes take effect on the next message.
/// </summary>
public sealed class HostSession : IAsyncDisposable
{
    private readonly SessionManager _mgr;
    private readonly RTCPeerConnection _pc;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private RTCDataChannel? _ctl, _mv, _in, _file;
    private IScreenPipeline? _stream;
    private AdaptiveBitrateController? _abr;
    private StreamSettings _settings = StreamSettings.For(QualityPreset.Auto);
    private long _lastMoveSeq = -1;
    private long _lastKeyframeRequest;
    private uint _fps = 30;
    private readonly Dictionary<long, CancellationTokenSource> _running = [];

    public string Id { get; }
    public string DeviceId { get; }
    public string ResumeToken { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public SessionState State { get; private set; } = SessionState.Negotiating;

    /// <summary>Bumped on each negotiation step (open, offer) so slow phones get a fresh connect window.</summary>
    internal int NegotiationStep;
    public int DisplayIndex { get; private set; }
    public double? InputRttMs { get; private set; }
    public StreamStats? Stats => _stream?.Stats;
    public string? Transport { get; private set; }

    /// <summary>Raised for a file-channel binary frame (handled by the file-transfer module).</summary>
    public event Action<HostSession, byte[]>? FileChunk;
    public event Action<HostSession>? Closed;

    /// <summary>Bulk-data high-water mark on the file channel (bytes).</summary>
    public const ulong MaxFileBuffered = 256 * 1024;

    internal HostSession(SessionManager mgr, string id, string deviceId, string resumeToken, RTCConfiguration config)
    {
        _mgr = mgr;
        Id = id;
        DeviceId = deviceId;
        ResumeToken = resumeToken;
        _pc = new RTCPeerConnection(config);
        _pc.ondatachannel += OnDataChannel;
        _pc.onconnectionstatechange += OnConnectionState;
        _pc.OnReceiveReport += OnReceiveReport;
        _pc.onicecandidate += c =>
        {
            if (c is null) return;
            _mgr.SendToDevice(DeviceId, "webrtc.ice", Id, new JsonObject
            {
                ["candidate"] = "candidate:" + c.ToString(),
                ["sdpMid"] = c.sdpMid ?? "0",
                ["sdpMLineIndex"] = c.sdpMLineIndex,
            });
        };
    }

    public PairedDevice? Device => _mgr.Devices.Get(DeviceId);

    public bool Has(Permission p) => Device is { } d && d.Permissions.HasFlag(p);

    internal async Task<string?> HandleOfferAsync(string sdp)
    {
        // Add the outbound video track before applying the remote offer so the answer includes it.
        if (Has(Permission.ViewScreen))
        {
            var h264 = new VideoFormat(VideoCodecsEnum.H264, 96, 90000, "packetization-mode=1;profile-level-id=42e01f;level-asymmetry-allowed=1");
            _pc.addTrack(new MediaStreamTrack(h264, MediaStreamStatusEnum.SendOnly));
        }
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK)
        {
            _mgr.Log("session", $"rejecting offer: {result}");
            return null;
        }
        var answer = _pc.createAnswer();
        await _pc.setLocalDescription(answer).ConfigureAwait(false);
        return answer.sdp;
    }

    internal void AddIceCandidate(string candidate, string? sdpMid, ushort mline)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return; // end-of-candidates
        _pc.addIceCandidate(new RTCIceCandidateInit { candidate = candidate, sdpMid = sdpMid ?? "0", sdpMLineIndex = mline });
    }

    private void OnConnectionState(RTCPeerConnectionState s)
    {
        _mgr.Log("session", $"{Id} connection {s}");
        if (s == RTCPeerConnectionState.connected)
        {
            State = SessionState.Connected;
            Transport = "webrtc";
            if (Has(Permission.ViewScreen)) StartStream(DisplayIndex);
            _mgr.RaiseChanged();
        }
        else if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
        {
            _ = _mgr.CloseSessionAsync(this, s.ToString());
        }
    }

    private void OnDataChannel(RTCDataChannel dc)
    {
        switch (dc.label)
        {
            case "ctl": _ctl = dc; dc.onmessage += (_, _, data) => _ = OnCtlAsync(data); break;
            case "mv": _mv = dc; dc.onmessage += (_, _, data) => OnMove(data); break;
            case "in": _in = dc; dc.onmessage += (_, _, data) => OnInput(data); break;
            case "file": _file = dc; dc.onmessage += (_, _, data) => FileChunk?.Invoke(this, data); break;
            default: dc.close(); break;
        }
    }

    // ---------- video ----------

    internal void StartStream(int display)
    {
        lock (_lock)
        {
            if (State == SessionState.Closed) return;
            _stream?.Dispose();
            var displays = _mgr.Screen.Displays();
            if (display < 0 || display >= displays.Count) display = 0;
            DisplayIndex = display;
            _abr = new AdaptiveBitrateController(_settings);
            _fps = (uint)_settings.Fps;
            _stream = _mgr.Screen.Start(display, _settings, SendFrame);
        }
    }

    private void SendFrame(EncodedFrame f)
    {
        if (State != SessionState.Connected || !Has(Permission.ViewScreen)) return;
        _pc.SendVideo(RtpDuration(ref _lastCaptureTicks, f.CaptureTicks, _fps), f.Data);
    }

    private long _lastCaptureTicks;

    /// <summary>
    /// RTP timestamp increment (90 kHz) from the real capture interval. The desktop only produces frames when it
    /// changes, so a fixed 1/fps step would tell the phone's jitter buffer that frames arrive late, making it grow
    /// and swing (unstable latency). Clamped to [1 ms, 1 s].
    /// </summary>
    internal static uint RtpDuration(ref long lastCaptureTicks, long captureTicks, uint fallbackFps)
    {
        var last = lastCaptureTicks;
        lastCaptureTicks = captureTicks;
        if (last == 0 || captureTicks <= last) return 90000 / Math.Max(1u, fallbackFps);
        var units = (captureTicks - last) * 90000.0 / System.Diagnostics.Stopwatch.Frequency;
        return (uint)Math.Clamp(units, 90, 90000);
    }

    private void OnReceiveReport(System.Net.IPEndPoint ep, SDPMediaTypesEnum media, RTCPCompoundPacket p)
    {
        if (media != SDPMediaTypesEnum.video) return;
        var fb = p.Feedback;
        if (fb is not null && fb.Header.PacketType == RTCPReportTypesEnum.PSFB &&
            fb.Header.PayloadFeedbackMessageType is PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR)
        {
            RequestKeyFrame();
        }
        var rr = p.ReceiverReport?.ReceptionReports?.FirstOrDefault();
        if (rr is not null && _abr is not null && _stream is not null)
        {
            var rtt = RttFromReport(rr.LastSenderReportTimestamp, rr.DelaySinceLastSenderReport);
            if (_abr.OnReport(rr.FractionLost / 256.0, rtt))
            {
                _stream.SetBitrate(_abr.CurrentKbps);
                _stream.SetFps(_abr.RecommendedFps);
                _fps = (uint)_abr.RecommendedFps;
            }
        }
    }

    /// <summary>RTT from RTCP LSR/DLSR (RFC 3550 §6.4.1), NaN if unavailable.</summary>
    internal static double RttFromReport(uint lsr, uint dlsr)
    {
        if (lsr == 0) return double.NaN;
        var now = DateTime.UtcNow - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ntpMiddle = (uint)(((ulong)now.TotalSeconds << 16) | (uint)((now.TotalSeconds % 1) * 65536));
        var rtt = unchecked(ntpMiddle - lsr - dlsr);
        var ms = rtt * 1000.0 / 65536.0;
        return ms is >= 0 and < 10_000 ? ms : double.NaN;
    }

    public void RequestKeyFrame()
    {
        var now = Stopwatch.GetTimestamp();
        if ((now - Interlocked.Read(ref _lastKeyframeRequest)) * 1000 / Stopwatch.Frequency < 300) return;
        Interlocked.Exchange(ref _lastKeyframeRequest, now);
        _stream?.RequestKeyFrame();
    }

    // ---------- input ----------

    private Input.PixelRect? CurrentMonitor()
    {
        var displays = _mgr.Screen.Displays();
        return DisplayIndex < displays.Count ? displays[DisplayIndex].Bounds : null;
    }

    private void OnMove(byte[] data)
    {
        if (!Has(Permission.ControlInput)) return;
        try
        {
            var m = JsonNode.Parse(data);
            var q = (long?)m?["q"] ?? 0;
            if (q <= Interlocked.Read(ref _lastMoveSeq)) return; // late, out-of-order move
            Interlocked.Exchange(ref _lastMoveSeq, q);
            if (CurrentMonitor() is { } mon) _mgr.Input.Move((double)m!["x"]!, (double)m["y"]!, mon);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException)
        {
            // malformed input is ignored
        }
    }

    internal void OnInput(byte[] data)
    {
        if (!Has(Permission.ControlInput)) return;
        try
        {
            var m = JsonNode.Parse(data) as JsonObject;
            var mon = CurrentMonitor();
            if (m is null || mon is null) return;
            switch ((string?)m["k"])
            {
                case "btn":
                    var b = (string?)m["b"];
                    if (b is "left" or "right" or "middle")
                        _mgr.Input.Button(b, (bool)m["d"]!, (double)m["x"]!, (double)m["y"]!, mon.Value);
                    break;
                case "wheel":
                    _mgr.Input.Wheel((int?)m["dx"] ?? 0, (int?)m["dy"] ?? 0);
                    break;
                case "key":
                    _mgr.Input.Key((ushort)(int)m["vk"]!, (bool)m["d"]!);
                    break;
                case "combo":
                    _mgr.Input.Combo(m["vks"]!.AsArray().Select(v => (ushort)(int)v!).ToArray());
                    break;
                case "text":
                    _mgr.Input.Text((string?)m["s"] ?? "");
                    break;
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException or OverflowException)
        {
        }
    }

    // ---------- ctl RPC ----------

    private async Task OnCtlAsync(byte[] data)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(data) as JsonObject; }
        catch (System.Text.Json.JsonException) { return; }
        if (msg is null) return;
        var id = (long?)msg["id"] ?? 0;
        var method = (string?)msg["m"];
        if (method is null) return;

        if (method == "rpc.cancel")
        {
            var target = (long?)msg["p"]?["id"] ?? 0;
            lock (_lock) if (_running.TryGetValue(target, out var c)) c.Cancel();
            return;
        }
        var device = Device;
        if (device is null) { await CloseAsync("revoked").ConfigureAwait(false); return; }
        if (!_mgr.Rpc.TryGet(method, out var entry)) { SendError(id, "unknown_method", method); return; }
        if (entry.Required != Permission.None && !device.Permissions.HasFlag(entry.Required))
        {
            _mgr.Log("security", $"denied {method} for {device.Name}: missing {PermissionNames.NameOf(entry.Required)}");
            SendError(id, "forbidden", $"permission '{PermissionNames.NameOf(entry.Required)}' not granted on this PC");
            return;
        }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        lock (_lock) _running[id] = cts;
        try
        {
            var ctx = new RpcContext { Session = this, Device = device, RequestId = id, Cancellation = cts.Token };
            var result = await entry.Handler(msg["p"], ctx).ConfigureAwait(false);
            SendCtl(new JsonObject { ["id"] = id, ["r"] = result ?? new JsonObject() });
        }
        catch (RpcException e) { SendError(id, e.Code, e.Message); }
        catch (OperationCanceledException) { SendError(id, "cancelled", "cancelled"); }
        catch (Exception e)
        {
            _mgr.Log("error", $"{method}: {e.GetType().Name}: {e.Message}");
            SendError(id, "internal", e.Message);
        }
        finally
        {
            lock (_lock) _running.Remove(id);
            cts.Dispose();
        }
    }

    private void SendError(long id, string code, string message) =>
        SendCtl(new JsonObject { ["id"] = id, ["e"] = new JsonObject { ["code"] = code, ["msg"] = message } });

    public void SendEvent(string ev, JsonNode payload, long? requestId = null)
    {
        var o = new JsonObject { ["ev"] = ev, ["p"] = payload };
        if (requestId is not null) o["id"] = requestId;
        SendCtl(o);
    }

    private void SendCtl(JsonObject o)
    {
        var dc = _ctl;
        if (dc?.readyState != RTCDataChannelState.open) return;
        lock (dc) dc.send(o.ToJsonString());
    }

    /// <summary>Sends a binary frame on the file channel with simple backpressure.</summary>
    public async Task SendFileChunkAsync(byte[] frame, CancellationToken ct)
    {
        var dc = _file ?? throw new RpcException("no_channel", "file channel not open");
        // Keep the shared SCTP association shallow so ctl/input messages never queue behind bulk data.
        while (dc.bufferedAmount > MaxFileBuffered) await Task.Delay(2, ct).ConfigureAwait(false);
        lock (dc) dc.send(frame, 0, frame.Length);
    }

    public void SetStreamSettings(StreamSettings s)
    {
        _settings = s.Sanitized();
        if (State == SessionState.Connected && Has(Permission.ViewScreen)) StartStream(DisplayIndex);
    }

    internal void SetInputRtt(double ms) => InputRttMs = ms;

    public async Task CloseAsync(string reason) => await _mgr.CloseSessionAsync(this, reason).ConfigureAwait(false);

    internal void Teardown()
    {
        lock (_lock)
        {
            if (State == SessionState.Closed) return;
            State = SessionState.Closed;
            foreach (var c in _running.Values) c.Cancel();
        }
        _cts.Cancel();
        _stream?.Dispose();
        _stream = null;
        _mgr.Input.ReleaseAll();
        try { _pc.close(); } catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { }
        Closed?.Invoke(this);
    }

    public ValueTask DisposeAsync()
    {
        Teardown();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static string Describe(byte[] data) => Encoding.UTF8.GetString(data);
}
