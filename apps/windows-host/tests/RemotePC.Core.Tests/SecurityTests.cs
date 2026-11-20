using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using RemotePC.Core.Protocol;
using RemotePC.Core.Security;
using Xunit;

namespace RemotePC.Core.Tests;

public sealed class SecurityTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly TestProtector _protector = new();
    private readonly DeviceKeys _host = DeviceKeys.Generate();
    private readonly DeviceKeys _phone = DeviceKeys.Generate();
    private readonly DeviceStore _store;
    private readonly PairingManager _pairing;
    private readonly PeerRouter _router;

    public SecurityTests()
    {
        _store = new DeviceStore(_dir.File("devices.dat"), _protector);
        _pairing = new PairingManager(_host, _store, _time);
        _router = new PeerRouter(_host, _store, _time);
    }

    public void Dispose()
    {
        _dir.Dispose();
        _host.Dispose();
        _phone.Dispose();
    }

    private long _seq;

    private Envelope PairRequest(PairingOffer offer, byte[]? secretOverride = null, string pairNonce = "pn-12345678")
    {
        var seq = ++_seq; // a real client uses max(last + 1, now_ms)
        var kx = new KeyExchange(Enc.B64u(_phone.EdPk), Enc.B64u(_phone.XPk));
        var transcript = PairingCrypto.Transcript(_host.DeviceId, _phone.DeviceId, kx.EdPk, kx.XPk, Enc.B64u(_host.EdPk), Enc.B64u(_host.XPk), pairNonce);
        var secret = secretOverride ?? (offer.Mode == PairingMode.Qr ? Enc.FromB64u(offer.SecretB64u) : Enc.Utf8(PairingCrypto.NormalizeCode(offer.DisplayCode!)!));
        var payload = new JsonObject
        {
            ["pairNonce"] = pairNonce,
            ["proof"] = Enc.B64u(PairingCrypto.Proof(secret, transcript)),
            ["deviceName"] = "iPhone của Phú\u0007",
            ["platform"] = "ios",
            ["appVersion"] = "0.1.0",
        };
        var key = EnvelopeCrypto.DeriveKey(_phone, _host.XPk, _host.DeviceId);
        return EnvelopeCrypto.Seal(key, new Envelope
        {
            Typ = "pair.request", From = _phone.DeviceId, To = _host.DeviceId, Seq = seq,
            Ts = _time.GetUtcNow().ToUnixTimeMilliseconds(), N = "", Kx = kx,
        }, payload);
    }

    private async Task<JsonObject> Submit(Envelope env)
    {
        var msg = _router.Open(env);
        Assert.NotNull(msg);
        return await _pairing.HandleRequestAsync(env, msg.Payload, CancellationToken.None);
    }

    private void ApproveWith(Permission p, Action<PairingApprovalRequest>? inspect = null) =>
        _pairing.ApprovalHandler = (req, _) => { inspect?.Invoke(req); return Task.FromResult(new PairingDecision(true, p)); };

    [Theory]
    [InlineData(PairingMode.Qr)]
    [InlineData(PairingMode.Code)]
    public async Task Pairing_Succeeds_With_Valid_Proof_And_Approval(PairingMode mode)
    {
        PairingApprovalRequest? seen = null;
        ApproveWith(Permission.ViewScreen | Permission.UseAi, r => seen = r);
        var offer = _pairing.CreateOffer(mode);
        var result = await Submit(PairRequest(offer));

        Assert.True((bool)result["ok"]!);
        Assert.NotNull(seen);
        Assert.Equal("iPhone của Phú", seen.DeviceName); // control char stripped
        Assert.Matches("^\\d{6}$", seen.VerifyCode);
        var d = _store.Get(_phone.DeviceId);
        Assert.NotNull(d);
        Assert.Equal(Permission.ViewScreen | Permission.UseAi, d.Permissions);
        Assert.Null(_pairing.CurrentOffer); // single use
    }

    [Fact]
    public async Task Pairing_Offer_Is_Single_Use()
    {
        ApproveWith(Permission.ViewScreen);
        var offer = _pairing.CreateOffer(PairingMode.Qr);
        Assert.True((bool)(await Submit(PairRequest(offer)))["ok"]!);
        var again = await Submit(PairRequest(offer, pairNonce: "pn-other-123"));
        Assert.Equal("expired", (string?)again["reason"]);
    }

    [Fact]
    public async Task Pairing_Locks_After_Five_Bad_Proofs()
    {
        ApproveWith(Permission.ViewScreen);
        var offer = _pairing.CreateOffer(PairingMode.Code);
        for (var i = 0; i < 5; i++)
        {
            var r = await Submit(PairRequest(offer, Enc.Utf8("AAAAAAAAAA"), $"pn-bad-{i:D4}"));
            Assert.Equal("bad_proof", (string?)r["reason"]);
        }
        var correct = await Submit(PairRequest(offer, pairNonce: "pn-good-0001"));
        Assert.Equal("locked", (string?)correct["reason"]);
        Assert.Null(_store.Get(_phone.DeviceId));
    }

    [Fact]
    public async Task Pairing_Offer_Expires()
    {
        ApproveWith(Permission.ViewScreen);
        var offer = _pairing.CreateOffer(PairingMode.Qr);
        _time.Advance(PairingManager.OfferLifetime + TimeSpan.FromSeconds(1));
        var r = await Submit(PairRequest(offer));
        Assert.Equal("expired", (string?)r["reason"]);
    }

    [Fact]
    public async Task Pairing_Rejected_By_Owner_Stores_Nothing()
    {
        _pairing.ApprovalHandler = (_, _) => Task.FromResult(new PairingDecision(false, Permission.None));
        var r = await Submit(PairRequest(_pairing.CreateOffer(PairingMode.Qr)));
        Assert.Equal("rejected", (string?)r["reason"]);
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task Router_Accepts_Paired_Rejects_Unknown_Replay_And_Revoked()
    {
        ApproveWith(Permission.ViewScreen);
        await Submit(PairRequest(_pairing.CreateOffer(PairingMode.Qr)));

        var key = EnvelopeCrypto.DeriveKey(_phone, _host.XPk, _host.DeviceId);
        Envelope Msg(long seq, long skewMs = 0) => EnvelopeCrypto.Seal(key, new Envelope
        {
            Typ = "session.open", From = _phone.DeviceId, To = _host.DeviceId, Seq = seq,
            Ts = _time.GetUtcNow().ToUnixTimeMilliseconds() + skewMs, N = "",
        }, new JsonObject { ["reqId"] = "a" });

        var reasons = new List<RejectReason>();
        _router.Rejected += (_, r) => reasons.Add(r);

        var m1 = Msg(101);
        Assert.NotNull(_router.Open(m1));
        Assert.Null(_router.Open(m1));            // exact replay
        Assert.Null(_router.Open(Msg(101)));      // old sequence
        Assert.NotNull(_router.Open(Msg(102)));

        Assert.Null(_router.Open(Msg(103, skewMs: 180_000)));  // 3 min in the future
        Assert.Null(_router.Open(Msg(104, skewMs: -180_000))); // 3 min old

        _store.Revoke(_phone.DeviceId);
        Assert.Null(_router.Open(Msg(110)));

        using var stranger = DeviceKeys.Generate();
        var sKey = EnvelopeCrypto.DeriveKey(stranger, _host.XPk, _host.DeviceId);
        var fromStranger = EnvelopeCrypto.Seal(sKey, new Envelope { Typ = "session.open", From = stranger.DeviceId, To = _host.DeviceId, Seq = 1, Ts = _time.GetUtcNow().ToUnixTimeMilliseconds(), N = "" }, new JsonObject());
        Assert.Null(_router.Open(fromStranger));

        Assert.Equal([RejectReason.Replay, RejectReason.Sequence, RejectReason.Stale, RejectReason.Stale, RejectReason.UnknownPeer, RejectReason.UnknownPeer], reasons);
    }

    [Fact]
    public void Router_Rejects_Impersonation_Via_Spoofed_From()
    {
        // Attacker seals with its own key but claims to be a paired phone.
        _store.Upsert(new PairedDevice { Id = _phone.DeviceId, Name = "p", Platform = "ios", EdPk = Enc.B64u(_phone.EdPk), XPk = Enc.B64u(_phone.XPk), Permissions = Permission.ViewScreen });
        using var attacker = DeviceKeys.Generate();
        var key = EnvelopeCrypto.DeriveKey(attacker, _host.XPk, _host.DeviceId);
        var env = EnvelopeCrypto.Seal(key, new Envelope { Typ = "session.open", From = _phone.DeviceId, To = _host.DeviceId, Seq = 1, Ts = _time.GetUtcNow().ToUnixTimeMilliseconds(), N = "" }, new JsonObject());
        Assert.Null(_router.Open(env));
    }

    [Fact]
    public void PairRequest_With_Mismatched_Kx_Is_Rejected()
    {
        var offer = _pairing.CreateOffer(PairingMode.Qr);
        var env = PairRequest(offer);
        using var other = DeviceKeys.Generate();
        Assert.Null(_router.Open(env with { Kx = env.Kx! with { EdPk = Enc.B64u(other.EdPk) } }));
    }

    [Fact]
    public void Router_Seals_For_Paired_Device_Which_Can_Open()
    {
        _store.Upsert(new PairedDevice { Id = _phone.DeviceId, Name = "p", Platform = "android", EdPk = Enc.B64u(_phone.EdPk), XPk = Enc.B64u(_phone.XPk), Permissions = Permission.ViewScreen });
        var e1 = _router.Seal(_phone.DeviceId, "session.accept", "s1", new JsonObject { ["x"] = 1 });
        var e2 = _router.Seal(_phone.DeviceId, "webrtc.answer", "s1", new JsonObject { ["x"] = 2 });
        Assert.True(e2.Seq > e1.Seq);
        var key = EnvelopeCrypto.DeriveKey(_phone, _host.XPk, _host.DeviceId);
        Assert.Equal(2, (int)EnvelopeCrypto.Open(key, e2)["x"]!);
    }

    [Fact]
    public void DeviceStore_Persists_Encrypted_And_Reloads()
    {
        var d = new PairedDevice { Id = _phone.DeviceId, Name = "Pixel", Platform = "android", EdPk = Enc.B64u(_phone.EdPk), XPk = Enc.B64u(_phone.XPk), Permissions = Permission.ViewScreen | Permission.Clipboard, PairedAt = _time.GetUtcNow() };
        _store.Upsert(d);
        var raw = File.ReadAllBytes(_dir.File("devices.dat"));
        Assert.DoesNotContain("Pixel", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);

        var reloaded = new DeviceStore(_dir.File("devices.dat"), _protector);
        Assert.Equal(d, reloaded.Get(_phone.DeviceId));

        reloaded.Update(_phone.DeviceId, x => x with { Name = "Pixel 9" });
        Assert.Equal("Pixel 9", new DeviceStore(_dir.File("devices.dat"), _protector).Get(_phone.DeviceId)!.Name);
        Assert.Throws<InvalidOperationException>(() => reloaded.Update(_phone.DeviceId, x => x with { XPk = Enc.B64u(new byte[32]) }));
        Assert.Throws<ArgumentException>(() => reloaded.Upsert(d with { Id = "a".PadRight(26, 'a') }));
    }

    [Fact]
    public void Identity_Is_Stable_Across_Loads()
    {
        using var a = IdentityStore.LoadOrCreate(_dir.File("id.dat"), _protector);
        using var b = IdentityStore.LoadOrCreate(_dir.File("id.dat"), _protector);
        Assert.Equal(a.DeviceId, b.DeviceId);
    }

    [Fact]
    public void Permission_Names_RoundTrip()
    {
        var p = Permission.ViewScreen | Permission.AiAgent | Permission.ModifyFiles;
        Assert.Equal(p, PermissionNames.FromNames(PermissionNames.ToNames(p)));
        Assert.Equal(Permission.None, PermissionNames.FromNames(["admin", "root"]));
    }

    [Fact]
    public void Qr_Uri_Contains_Keys_And_Escapes_Name()
    {
        var offer = _pairing.CreateOffer(PairingMode.Qr);
        var uri = _pairing.BuildQrUri(offer, "PC của tôi & co", "wss://sig.example/signal", ["192.168.1.5:45822"]);
        Assert.StartsWith("remotepc://pair?v=1&h=" + _host.DeviceId, uri, StringComparison.Ordinal);
        Assert.Contains("n=PC%20c%E1%BB%A7a%20t%C3%B4i%20%26%20co", uri, StringComparison.Ordinal);
        Assert.Contains("s=" + offer.SecretB64u, uri, StringComparison.Ordinal);
    }
}
