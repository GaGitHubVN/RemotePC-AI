using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RemotePC.Core.Input;
using RemotePC.Core.Media;
using RemotePC.Core.Protocol;
using RemotePC.Core.Session;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RemotePC.Core.Tests;

/// <summary>
/// A protocol-complete client written with SIPSorcery: behaves like the mobile app (client offers,
/// recvonly H.264, ctl/mv/in/file data channels). Used for end-to-end tests without a phone.
/// </summary>
internal sealed class TestPhone : IAsyncDisposable
{
    private readonly DeviceKeys _keys;
    private readonly byte[] _hostXPk;
    private readonly string _hostId;
    private readonly Func<Envelope, Task> _sendToHost;
    private long _seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _ctl, _mv, _in;
    private long _rpcId;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();

    public BlockingCollection<(string Typ, JsonNode Payload, string Sid)> Inbox { get; } = [];
    public ConcurrentQueue<byte[]> VideoFrames { get; } = new();
    public ConcurrentQueue<JsonObject> Events { get; } = new();
    public string? Sid { get; private set; }
    public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestPhone(DeviceKeys keys, DeviceKeys host, Func<Envelope, Task> sendToHost)
    {
        _keys = keys;
        _hostXPk = host.XPk;
        _hostId = host.DeviceId;
        _sendToHost = sendToHost;
    }

    private byte[] Key => EnvelopeCrypto.DeriveKey(_keys, _hostXPk, _hostId);

    public Task SendAsync(string typ, string sid, JsonNode payload)
    {
        var env = EnvelopeCrypto.Seal(Key, new Envelope
        {
            Typ = typ, From = _keys.DeviceId, To = _hostId, Sid = sid, Seq = Interlocked.Increment(ref _seq),
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), N = "",
        }, payload);
        return _sendToHost(env);
    }

    /// <summary>Delivered by the test's transport (host → phone).</summary>
    public void Receive(Envelope env)
    {
        var payload = EnvelopeCrypto.Open(Key, env);
        switch (env.Typ)
        {
            case "webrtc.answer":
                _pc!.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = (string)payload["sdp"]! });
                break;
            case "webrtc.ice":
                _pc?.addIceCandidate(new RTCIceCandidateInit { candidate = (string)payload["candidate"]!, sdpMid = (string?)payload["sdpMid"], sdpMLineIndex = (ushort)(int)payload["sdpMLineIndex"]! });
                break;
        }
        Inbox.Add((env.Typ, payload, env.Sid));
    }

    public (string Typ, JsonNode Payload, string Sid) Expect(string typ, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (Inbox.TryTake(out var m, 100) && m.Typ == typ) return m;
        }
        throw new TimeoutException($"did not receive {typ}");
    }

    public async Task<JsonNode> OpenSessionAsync(JsonObject? resume = null)
    {
        var p = new JsonObject { ["reqId"] = "r" + Guid.NewGuid().ToString("N")[..6] };
        if (resume is not null) p["resume"] = resume;
        await SendAsync("session.open", "", p);
        var accept = Expect("session.accept");
        Sid = (string)accept.Payload["sid"]!;
        return accept.Payload;
    }

    public async Task ConnectWebRtcAsync()
    {
        _pc = new RTCPeerConnection(new RTCConfiguration { iceServers = [] });
        var fmt = new VideoFormat(VideoCodecsEnum.H264, 96, 90000, "packetization-mode=1;profile-level-id=42e01f");
        _pc.addTrack(new MediaStreamTrack(fmt, MediaStreamStatusEnum.RecvOnly));
        _pc.OnVideoFrameReceived += (_, _, frame, _) => VideoFrames.Enqueue(frame);
        _pc.onconnectionstatechange += s => { if (s == RTCPeerConnectionState.connected) Connected.TrySetResult(); };
        _ctl = await _pc.createDataChannel("ctl");
        _mv = await _pc.createDataChannel("mv", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 });
        _in = await _pc.createDataChannel("in");
        await _pc.createDataChannel("file");
        _ctl.onmessage += (_, _, data) =>
        {
            var o = (JsonObject)JsonNode.Parse(data)!;
            if (o["ev"] is not null) Events.Enqueue(o);
            else if ((long?)o["id"] is { } id && _pending.TryRemove(id, out var t)) t.TrySetResult(o);
        };
        _pc.onicecandidate += c =>
        {
            if (c is null) return;
            _ = SendAsync("webrtc.ice", Sid!, new JsonObject { ["candidate"] = "candidate:" + c, ["sdpMid"] = c.sdpMid, ["sdpMLineIndex"] = c.sdpMLineIndex });
        };
        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer);
        await SendAsync("webrtc.offer", Sid!, new JsonObject { ["sdp"] = offer.sdp });
    }

    public async Task WaitChannelsOpenAsync()
    {
        for (var i = 0; i < 100 && _ctl?.readyState != RTCDataChannelState.open; i++) await Task.Delay(50);
        if (_ctl?.readyState != RTCDataChannelState.open) throw new TimeoutException("ctl channel did not open");
    }

    public async Task<JsonObject> CallAsync(string method, JsonNode? p = null, int timeoutMs = 5000)
    {
        var id = Interlocked.Increment(ref _rpcId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        _ctl!.send(new JsonObject { ["id"] = id, ["m"] = method, ["p"] = p }.ToJsonString());
        return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    public void SendMove(double x, double y, long q) => _mv!.send(new JsonObject { ["x"] = x, ["y"] = y, ["q"] = q }.ToJsonString());
    public void SendInput(JsonObject o) => _in!.send(o.ToJsonString());

    public ValueTask DisposeAsync()
    {
        _pc?.close();
        return ValueTask.CompletedTask;
    }
}

/// <summary>TEST-ONLY screen source: emits synthetic H.264-shaped access units (SPS/PPS/IDR, then P slices).</summary>
internal sealed class FakeScreenSource : IScreenSource
{
    public int KeyFrameRequests;

    public IReadOnlyList<DisplayDesc> Displays() =>
    [
        new DisplayDesc(0, "FAKE1", new PixelRect(0, 0, 1920, 1080), true, 0),
        new DisplayDesc(1, "FAKE2", new PixelRect(1920, 0, 1280, 1024), false, 0),
    ];

    public IScreenPipeline Start(int displayIndex, StreamSettings settings, Action<EncodedFrame> onFrame) => new Pipeline(this, displayIndex, onFrame);

    public byte[] CapturePng(int displayIndex, int maxWidth = 1920) => [0x89, 0x50, 0x4E, 0x47];

    private sealed class Pipeline : IScreenPipeline
    {
        private readonly Timer _timer;
        private int _n;
        private volatile bool _key = true;
        private readonly FakeScreenSource _src;
        public int DisplayIndex { get; }
        public StreamStats Stats => new(30, 1000, 1280, 720, "fake", false, 0, 0, 0, _n, 0);

        public Pipeline(FakeScreenSource src, int display, Action<EncodedFrame> onFrame)
        {
            _src = src;
            DisplayIndex = display;
            _timer = new Timer(_ =>
            {
                var key = _key || _n % 60 == 0;
                _key = false;
                byte[] au = key
                    ? [0, 0, 0, 1, 0x67, 0x42, 0xe0, 0x1f, 0xaa, 0, 0, 0, 1, 0x68, 0xce, 0x3c, 0x80, 0, 0, 0, 1, 0x65, .. new byte[3000].Select((_, i) => (byte)(i * 7 + 1))]
                    : [0, 0, 0, 1, 0x41, .. new byte[800].Select((_, i) => (byte)(i + 3))];
                Interlocked.Increment(ref _n);
                onFrame(new EncodedFrame(au, key, 0, 0, 0, 1280, 720));
            }, null, 0, 33);
        }

        public void RequestKeyFrame() { _key = true; Interlocked.Increment(ref _src.KeyFrameRequests); }
        public void SetBitrate(int kbps) { }
        public void SetFps(int fps) { }
        public void Dispose() => _timer.Dispose();
    }
}

/// <summary>TEST-ONLY input sink that records calls.</summary>
internal sealed class RecordingInput : IInputSink
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public bool Move(double nx, double ny, PixelRect m) { Calls.Enqueue($"move {nx:F2},{ny:F2} @{m.X}"); return true; }
    public bool Button(string b, bool d, double nx, double ny, PixelRect m) { Calls.Enqueue($"btn {b} {d} {nx:F2},{ny:F2}"); return true; }
    public bool Wheel(int dx, int dy) { Calls.Enqueue($"wheel {dx},{dy}"); return true; }
    public bool Key(ushort vk, bool down) { Calls.Enqueue($"key {vk} {down}"); return true; }
    public bool Combo(IReadOnlyList<ushort> vks) { Calls.Enqueue($"combo {string.Join('+', vks)}"); return true; }
    public bool Text(string text) { Calls.Enqueue($"text {text}"); return true; }
    public void ReleaseAll() => Calls.Enqueue("release");
}
