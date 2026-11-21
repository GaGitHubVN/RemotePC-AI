using System.Text.Json.Nodes;
using RemotePC.Core.Media;
using RemotePC.Core.Protocol;
using RemotePC.Core.Security;
using RemotePC.Core.Session;
using Xunit;

namespace RemotePC.Core.Tests;

/// <summary>
/// End-to-end over real WebRTC (ICE/DTLS/SCTP/SRTP via SIPSorcery on loopback): envelope transport is
/// wired in-process; everything after signaling is the production code path.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionE2ETests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly DeviceKeys _host = DeviceKeys.Generate();
    private readonly DeviceKeys _phoneKeys = DeviceKeys.Generate();
    private DeviceStore _store = null!;
    private PeerRouter _router = null!;
    private SessionManager _mgr = null!;
    private readonly FakeScreenSource _screen = new();
    private readonly RecordingInput _input = new();
    private TestPhone _phone = null!;
    private readonly List<string> _log = [];

    public Task InitializeAsync()
    {
        _store = new DeviceStore(_dir.File("d.dat"), new TestProtector());
        _store.Upsert(new PairedDevice
        {
            Id = _phoneKeys.DeviceId, Name = "Phone", Platform = "test", EdPk = Enc.B64u(_phoneKeys.EdPk), XPk = Enc.B64u(_phoneKeys.XPk),
            Permissions = Permission.ViewScreen | Permission.ControlInput,
        });
        _router = new PeerRouter(_host, _store, TimeProvider.System);
        _mgr = new SessionManager(_router, _store, _screen, _input, TimeProvider.System);
        _mgr.LogMessage += (c, m) => { lock (_log) _log.Add($"{c}: {m}"); };
        _mgr.IceServerProvider = _ => Task.FromResult(new List<SIPSorcery.Net.RTCIceServer>()); // loopback only, no STUN
        _mgr.Rpc.Register("test.ai_only", Permission.UseAi, (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true }));
        _phone = new TestPhone(_phoneKeys, _host, async env =>
        {
            var msg = _router.Open(env);
            if (msg is not null) await _mgr.HandleAsync(msg, e => { _phone.Receive(e); return true; });
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _phone.DisposeAsync();
        await _mgr.CloseAllAsync("test_end");
        _dir.Dispose();
    }

    private async Task ConnectAsync()
    {
        var accept = await _phone.OpenSessionAsync();
        Assert.Contains("view_screen", accept["permissions"]!.AsArray().Select(x => (string?)x));
        Assert.Equal(2, accept["displays"]!.AsArray().Count);
        await _phone.ConnectWebRtcAsync();
        await _phone.Connected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await _phone.WaitChannelsOpenAsync();
    }

    [Fact]
    public async Task Streams_Video_And_Serves_Rpc_Over_Real_WebRtc()
    {
        await ConnectAsync();
        for (var i = 0; i < 100 && _phone.VideoFrames.Count < 15; i++) await Task.Delay(50);
        Assert.True(_phone.VideoFrames.Count >= 15, $"only {_phone.VideoFrames.Count} frames received");
        var all = _phone.VideoFrames.ToArray();
        Assert.Contains(all, f => H264.NalTypes(f).Contains(H264.NalSps) && H264.IsKeyFrame(f));
        // Large IDR (3 KB+) must be reassembled from multiple RTP packets (FU-A).
        Assert.Contains(all, f => f.Length > 3000);

        var pong = await _phone.CallAsync("ping", new JsonObject { ["t"] = 42 });
        Assert.Equal(42, (int)pong["r"]!["t"]!);

        var forbidden = await _phone.CallAsync("test.ai_only");
        Assert.Equal("forbidden", (string?)forbidden["e"]!["code"]);

        var unknown = await _phone.CallAsync("system.format_c");
        Assert.Equal("unknown_method", (string?)unknown["e"]!["code"]);

        var sel = await _phone.CallAsync("display.select", new JsonObject { ["index"] = 1 });
        Assert.Equal(1, (int)sel["r"]!["current"]!);
        Assert.Equal(1, _mgr.Sessions.Single().DisplayIndex);

        var stats = await _phone.CallAsync("stream.stats");
        Assert.Equal("fake", (string?)stats["r"]!["encoder"]);
    }

    [Fact]
    public async Task Input_Channels_Reach_The_Input_Sink_With_Permission_Enforced()
    {
        await ConnectAsync();
        _phone.SendMove(0.5, 0.25, 10);
        _phone.SendMove(0.9, 0.9, 5); // stale sequence → must be dropped
        _phone.SendInput(new JsonObject { ["k"] = "btn", ["b"] = "left", ["d"] = true, ["x"] = 0.5, ["y"] = 0.25 });
        _phone.SendInput(new JsonObject { ["k"] = "btn", ["b"] = "left", ["d"] = false, ["x"] = 0.5, ["y"] = 0.25 });
        _phone.SendInput(new JsonObject { ["k"] = "text", ["s"] = "Xin chào Việt Nam" });
        _phone.SendInput(new JsonObject { ["k"] = "combo", ["vks"] = new JsonArray(0x11, 0x43) });
        _phone.SendInput(new JsonObject { ["k"] = "wheel", ["dx"] = 0, ["dy"] = -120 });
        for (var i = 0; i < 60 && !_input.Calls.Any(c => c.StartsWith("wheel", StringComparison.Ordinal)); i++) await Task.Delay(50);

        var calls = _input.Calls.ToArray();
        Assert.Contains("move 0.50,0.25 @0", calls);
        Assert.DoesNotContain(calls, c => c.StartsWith("move 0.90", StringComparison.Ordinal));
        Assert.Contains("btn left True 0.50,0.25", calls);
        Assert.Contains("text Xin chào Việt Nam", calls);
        Assert.Contains("combo 17+67", calls);
        Assert.Contains("wheel 0,-120", calls);

        // Remove control permission at runtime → further input is ignored immediately.
        _store.Update(_phoneKeys.DeviceId, d => d with { Permissions = Permission.ViewScreen });
        var before = _input.Calls.Count;
        _phone.SendInput(new JsonObject { ["k"] = "text", ["s"] = "should not arrive" });
        _phone.SendMove(0.1, 0.1, 100);
        await Task.Delay(500);
        Assert.Equal(before, _input.Calls.Count);
    }

    [Fact]
    public async Task Revocation_Closes_Session_And_Blocks_Reconnect()
    {
        await ConnectAsync();
        _store.Revoke(_phoneKeys.DeviceId);
        // After revocation the host can no longer encrypt to the phone, so it just tears down.
        for (var i = 0; i < 60 && _mgr.Sessions.Count > 0; i++) await Task.Delay(50);
        Assert.Empty(_mgr.Sessions);
        Assert.Contains("release", _input.Calls); // stuck keys released on teardown

        await _phone.SendAsync("session.open", "", new JsonObject { ["reqId"] = "again" });
        await Task.Delay(500);
        Assert.Empty(_mgr.Sessions);
        Assert.DoesNotContain(_phone.Inbox, m => m.Typ == "session.accept" && (string?)m.Payload["reqId"] == "again");
    }

    [Fact]
    public async Task Concurrent_Session_Open_From_One_Device_Leaves_One_Session()
    {
        // Slow host: the phone re-sends session.open while the first is still being handled.
        var gate = new TaskCompletionSource();
        var calls = 0;
        _mgr.IceServerProvider = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) await gate.Task;
            return [];
        };
        var a = _phone.SendAsync("session.open", "", new JsonObject { ["reqId"] = "first" });
        await Task.Delay(100);
        var b = _phone.SendAsync("session.open", "", new JsonObject { ["reqId"] = "second" });
        await Task.Delay(200);
        gate.SetResult();
        await Task.WhenAll(a, b);
        for (var i = 0; i < 40 && _phone.Inbox.Count(m => m.Typ == "session.accept") < 2; i++) await Task.Delay(50);
        Assert.Single(_mgr.Sessions);
        await Task.Delay(200);
        int opened, closed;
        lock (_log)
        {
            opened = _log.Count(l => l.StartsWith("session: opened", StringComparison.Ordinal));
            closed = _log.Count(l => l.StartsWith("session: closed", StringComparison.Ordinal));
        }
        Assert.Equal(1, opened - closed); // no orphaned second session left negotiating
        var accepted = _phone.Inbox.Where(m => m.Typ == "session.accept").Select(m => (string?)m.Payload["sid"]).ToList();
        Assert.Equal(2, accepted.Count);
        Assert.Equal(_mgr.Sessions.Single().Id, accepted[^1]); // the latest request owns the live session
    }

    [Fact]
    public async Task Reopening_From_Same_Device_Replaces_Session_And_Resume_Keeps_Sid()
    {
        var first = await _phone.OpenSessionAsync();
        var sid = (string)first["sid"]!;
        var token = (string)first["token"]!;
        var second = await _phone.OpenSessionAsync(new JsonObject { ["sid"] = sid, ["token"] = token });
        Assert.Single(_mgr.Sessions); // no duplicate session after reconnect
        Assert.Equal(sid, (string)second["sid"]!); // resumed

        var third = await _phone.OpenSessionAsync(new JsonObject { ["sid"] = (string)second["sid"]!, ["token"] = "forged" });
        Assert.NotEqual(sid, (string)third["sid"]!);
        Assert.Single(_mgr.Sessions);
    }

    [Fact]
    public async Task Lock_Rejects_New_Sessions()
    {
        _mgr.Locked = true;
        await _phone.SendAsync("session.open", "", new JsonObject { ["reqId"] = "x" });
        var reject = _phone.Expect("session.reject");
        Assert.Equal("locked", (string?)reject.Payload["reason"]);
    }
}
