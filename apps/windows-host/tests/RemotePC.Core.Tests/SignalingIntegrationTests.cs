using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using RemotePC.Core.Protocol;
using RemotePC.Core.Security;
using RemotePC.Core.Signaling;
using Xunit;

namespace RemotePC.Core.Tests;

/// <summary>Starts the real Node signaling server (backend/signaling) and connects C# clients to it.</summary>
public sealed class NodeSignalingFixture : IAsyncLifetime
{
    private Process? _proc;
    public Uri Url { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var port = FreePort();
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "backend", "signaling"));
        var psi = new ProcessStartInfo("node", "--experimental-strip-types src/node/main.ts")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["HOST"] = "127.0.0.1";
        psi.Environment["NODE_ENV"] = "test";
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("node did not start");
        Url = new Uri($"ws://127.0.0.1:{port}/signal");
        using var http = new HttpClient();
        for (var i = 0; i < 100; i++)
        {
            try
            {
                if ((await http.GetAsync(new Uri($"http://127.0.0.1:{port}/healthz"))).IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("signaling server did not become healthy: " + await _proc.StandardError.ReadToEndAsync());
    }

    public Task DisposeAsync()
    {
        try { _proc?.Kill(true); } catch (InvalidOperationException) { }
        _proc?.Dispose();
        return Task.CompletedTask;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

[Trait("Category", "Integration")]
public sealed class SignalingIntegrationTests(NodeSignalingFixture fx) : IClassFixture<NodeSignalingFixture>
{
    private static async Task WaitOnline(SignalingClient c)
    {
        for (var i = 0; i < 100 && c.State != SignalingState.Online; i++) await Task.Delay(50);
        Assert.Equal(SignalingState.Online, c.State);
    }

    [Fact]
    public async Task Pairing_Then_Session_Envelope_Over_Real_Signaling_Server()
    {
        using var dir = new TempDir();
        var time = TimeProvider.System;
        using var hostKeys = DeviceKeys.Generate();
        using var phoneKeys = DeviceKeys.Generate();
        var store = new DeviceStore(dir.File("d.dat"), new TestProtector());
        var hostRouter = new PeerRouter(hostKeys, store, time);
        var pairing = new PairingManager(hostKeys, store, time)
        {
            ApprovalHandler = (_, _) => Task.FromResult(new PairingDecision(true, Permission.ViewScreen | Permission.ControlInput)),
        };

        await using var host = new SignalingClient(fx.Url, hostKeys, "host");
        await using var phone = new SignalingClient(fx.Url, phoneKeys, "client");
        var phoneInbox = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostGotSession = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        phone.EnvelopeReceived += e => phoneInbox.TrySetResult(e);
        host.EnvelopeReceived += async e =>
        {
            var m = hostRouter.Open(e);
            if (m is null) return;
            if (m.Typ == "pair.request")
            {
                var result = await pairing.HandleRequestAsync(e, m.Payload, CancellationToken.None);
                host.Send(hostRouter.SealForPairing(e, "pair.result", result));
            }
            else
            {
                hostGotSession.TrySetResult(m);
            }
        };
        host.Start();
        phone.Start();
        await WaitOnline(host);
        await WaitOnline(phone);

        // Phone side of pairing (what the mobile app does after scanning the QR code).
        var offer = pairing.CreateOffer(PairingMode.Qr);
        var kx = new KeyExchange(Enc.B64u(phoneKeys.EdPk), Enc.B64u(phoneKeys.XPk));
        const string pn = "pn-integration-1";
        var transcript = PairingCrypto.Transcript(hostKeys.DeviceId, phoneKeys.DeviceId, kx.EdPk, kx.XPk, Enc.B64u(hostKeys.EdPk), Enc.B64u(hostKeys.XPk), pn);
        var key = EnvelopeCrypto.DeriveKey(phoneKeys, hostKeys.XPk, hostKeys.DeviceId);
        var req = EnvelopeCrypto.Seal(key, new Envelope
        {
            Typ = "pair.request", From = phoneKeys.DeviceId, To = hostKeys.DeviceId, Seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), N = "", Kx = kx,
        }, new JsonObject { ["pairNonce"] = pn, ["proof"] = Enc.B64u(PairingCrypto.Proof(Enc.FromB64u(offer.SecretB64u), transcript)), ["deviceName"] = "Test phone", ["platform"] = "android" });
        Assert.True(phone.Send(req));

        var reply = await phoneInbox.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("pair.result", reply.Typ);
        var result = EnvelopeCrypto.Open(key, reply);
        Assert.True((bool)result["ok"]!);
        Assert.Contains("control_input", result["permissions"]!.AsArray().Select(x => (string?)x));
        Assert.NotNull(store.Get(phoneKeys.DeviceId));

        // Now an authenticated session message.
        var open = EnvelopeCrypto.Seal(key, new Envelope
        {
            Typ = "session.open", From = phoneKeys.DeviceId, To = hostKeys.DeviceId, Seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), N = "",
        }, new JsonObject { ["reqId"] = "r-1" });
        Assert.True(phone.Send(open));
        var msg = await hostGotSession.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("session.open", msg.Typ);
        Assert.Equal("r-1", (string?)msg.Payload["reqId"]);
        Assert.NotNull(msg.Device);
    }

    [Fact]
    public async Task Requests_Turn_Credentials_And_Reports_Rtt()
    {
        using var keys = DeviceKeys.Generate();
        await using var c = new SignalingClient(fx.Url, keys, "host");
        c.Start();
        await WaitOnline(c);
        var turn = await c.RequestTurnAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(turn);
        Assert.NotNull(turn["iceServers"]);
    }

    [Fact]
    public void Rejects_Insecure_Remote_Url()
    {
        using var keys = DeviceKeys.Generate();
        Assert.Throws<ArgumentException>(() => new SignalingClient(new Uri("ws://example.com/signal"), keys, "host"));
    }

    [Fact]
    public void Backoff_Grows_And_Caps()
    {
        Assert.InRange(SignalingClient.Backoff(0).TotalMilliseconds, 800, 1200);
        Assert.InRange(SignalingClient.Backoff(3).TotalMilliseconds, 6400, 9600);
        Assert.InRange(SignalingClient.Backoff(50).TotalMilliseconds, 48_000, 72_000);
    }
}
