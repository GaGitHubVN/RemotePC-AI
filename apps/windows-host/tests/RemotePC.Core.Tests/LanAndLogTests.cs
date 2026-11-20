using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using RemotePC.Core.Logging;
using RemotePC.Core.Protocol;
using RemotePC.Core.Signaling;
using Xunit;

namespace RemotePC.Core.Tests;

[Trait("Category", "Integration")]
public class LanAndLogTests
{
    [Fact]
    public async Task Lan_Server_Authenticates_And_Delivers_Envelopes_Both_Ways()
    {
        using var host = DeviceKeys.Generate();
        using var phone = DeviceKeys.Generate();
        await using var lan = new LanSignalingServer(host, port: 0);
        var received = new TaskCompletionSource<(Envelope Env, Func<Envelope, bool> Reply)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lan.EnvelopeReceived += (e, reply) => received.TrySetResult((e, reply));
        lan.Start();

        // The phone uses the same SignalingClient code path as for the Internet server.
        await using var client = new SignalingClient(new Uri($"ws://127.0.0.1:{lan.Port}/signal"), phone, "client");
        var phoneInbox = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EnvelopeReceived += e => phoneInbox.TrySetResult(e);
        client.Start();
        for (var i = 0; i < 100 && client.State != SignalingState.Online; i++) await Task.Delay(50);
        Assert.Equal(SignalingState.Online, client.State);

        var key = EnvelopeCrypto.DeriveKey(phone, host.XPk, host.DeviceId);
        var env = EnvelopeCrypto.Seal(key, new Envelope { Typ = "session.open", From = phone.DeviceId, To = host.DeviceId, Seq = 1, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), N = "" }, new JsonObject { ["x"] = 1 });
        Assert.True(client.Send(env));
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(phone.DeviceId, got.Env.From);

        var reply = EnvelopeCrypto.Seal(key, new Envelope { Typ = "session.accept", From = host.DeviceId, To = phone.DeviceId, Seq = 2, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), N = "" }, new JsonObject { ["ok"] = true });
        Assert.True(got.Reply(reply));
        var back = await phoneInbox.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("session.accept", back.Typ);
        Assert.True((bool)EnvelopeCrypto.Open(key, back)["ok"]!);
    }

    [Fact]
    public async Task Lan_Server_Rejects_Bad_Signature()
    {
        using var host = DeviceKeys.Generate();
        await using var lan = new LanSignalingServer(host, port: 0);
        lan.Start();
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{lan.Port}/signal"), CancellationToken.None);
        var ch = await SignalingClient.ReceiveJsonAsync(ws, CancellationToken.None);
        Assert.Equal("challenge", (string?)ch!["t"]);
        using var k = DeviceKeys.Generate();
        var bad = new JsonObject { ["t"] = "hello", ["v"] = 1, ["role"] = "client", ["id"] = k.DeviceId, ["edPk"] = Enc.B64u(k.EdPk), ["sig"] = Enc.B64u(new byte[64]) };
        await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(bad.ToJsonString()), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        var buf = new byte[256];
        var r = await ws.ReceiveAsync(buf, CancellationToken.None);
        Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Close, r.MessageType);
        Assert.Equal(4004, (int)ws.CloseStatus!.Value);
    }

    [Fact]
    public async Task Discovery_Responder_Answers_Probe()
    {
        var port = 46000 + Random.Shared.Next(1000);
        using var responder = new LanDiscoveryResponder(() => new JsonObject { ["t"] = "host", ["v"] = 1, ["id"] = "abc", ["ws"] = 45822 }, port);
        using var udp = new UdpClient(0);
        var probe = """{"t":"discover","v":1}"""u8.ToArray();
        await udp.SendAsync(probe, new IPEndPoint(IPAddress.Loopback, port));
        var res = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("host", (string?)JsonNode.Parse(res.Buffer)!["t"]);
    }

    [Theory]
    [InlineData("token=abcdef123456", "token=[REDACTED]")]
    [InlineData("{\"password\": \"hunter2\"}", "{\"password\": \"[REDACTED]\"}")]
    [InlineData("Authorization: Bearer eyJhbGciOi.x.y", "Authorization: [REDACTED]")]
    [InlineData("device mfrggzdfmztwq2lknnwg23tpob connected", "device mfrggzdfmztwq2lknnwg23tpob connected")]
    public void Redacts_Secrets_But_Keeps_Device_Ids(string input, string expected) =>
        Assert.Equal(expected, ActivityLog.Redact(input));

    [Fact]
    public void Redacts_Long_Opaque_Values()
    {
        var s = ActivityLog.Redact("sealed blob " + new string('A', 60));
        Assert.DoesNotContain(new string('A', 60), s, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_Writes_Redacted_Jsonl_And_Clears()
    {
        using var dir = new TempDir();
        var log = new ActivityLog(dir.Path);
        log.Write("auth", "api_key=sk-12345 paired");
        var file = Directory.GetFiles(dir.Path, "activity-*.jsonl").Single();
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("sk-12345", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        log.Clear();
        Assert.Empty(Directory.GetFiles(dir.Path, "activity-*.jsonl"));
        Assert.Empty(log.Recent);
    }
}
