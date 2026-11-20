using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemotePC.Core.Protocol;
using Xunit;

namespace RemotePC.Core.Tests;

public class ProtocolVectorTests
{
    private static DeviceKeys Host => DeviceKeys.FromSeeds(Enc.FromB64u(Vectors.S("keys.host.edSeed")), Enc.FromB64u(Vectors.S("keys.host.xSk")));
    private static DeviceKeys Client => DeviceKeys.FromSeeds(Enc.FromB64u(Vectors.S("keys.client.edSeed")), Enc.FromB64u(Vectors.S("keys.client.xSk")));

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "my")]
    [InlineData("fo", "mzxq")]
    [InlineData("foo", "mzxw6")]
    [InlineData("foob", "mzxw6yq")]
    [InlineData("fooba", "mzxw6ytb")]
    [InlineData("foobar", "mzxw6ytboi")]
    public void Base32_Rfc4648(string input, string expected) => Assert.Equal(expected, Enc.Base32(Enc.Utf8(input)));

    [Fact]
    public void B64u_RoundTrip_And_Rejects_Standard_Alphabet()
    {
        var data = RandomNumberGenerator.GetBytes(37);
        Assert.Equal(data, Enc.FromB64u(Enc.B64u(data)));
        Assert.Throws<FormatException>(() => Enc.FromB64u("ab+/"));
    }

    [Fact]
    public void Keys_And_DeviceIds_Match_Vectors()
    {
        Assert.Equal(Vectors.S("keys.host.edPk"), Enc.B64u(Host.EdPk));
        Assert.Equal(Vectors.S("keys.host.xPk"), Enc.B64u(Host.XPk));
        Assert.Equal(Vectors.S("keys.host.id"), Host.DeviceId);
        Assert.Equal(Vectors.S("keys.client.id"), Client.DeviceId);
    }

    [Fact]
    public void Hello_Signature_Matches_Vector()
    {
        // Ed25519 is deterministic, so the signature must be byte-identical.
        var sig = Host.Sign(Enc.Utf8(Vectors.S("hello.message")));
        Assert.Equal(Vectors.S("hello.sig"), Enc.B64u(sig));
        Assert.True(Crypto.EdVerify(Host.EdPk, Enc.Utf8(Vectors.S("hello.message")), sig));
        Assert.False(Crypto.EdVerify(Host.EdPk, Enc.Utf8("tampered"), sig));
    }

    [Fact]
    public void Envelope_Key_Is_Symmetric_And_Matches_Vector()
    {
        Assert.Equal(Vectors.S("envelope.key"), Enc.B64u(EnvelopeCrypto.DeriveKey(Host, Client.XPk, Client.DeviceId)));
        Assert.Equal(Vectors.S("envelope.key"), Enc.B64u(EnvelopeCrypto.DeriveKey(Client, Host.XPk, Host.DeviceId)));
    }

    [Fact]
    public void Opens_TypeScript_Sealed_Envelopes()
    {
        var key = Enc.FromB64u(Vectors.S("envelope.key"));
        var env = Vectors.V1["envelope"]!["sealed"].Deserialize<Envelope>(ProtocolJson.Options)!;
        var payload = EnvelopeCrypto.Open(key, env);
        Assert.Equal("Xin chào 👋", (string?)payload["text"]);

        var pair = Vectors.V1["envelope"]!["pairRequest"].Deserialize<Envelope>(ProtocolJson.Options)!;
        Assert.NotNull(pair.Kx);
        Assert.Equal("nonce-1", (string?)EnvelopeCrypto.Open(key, pair)["pairNonce"]);
    }

    [Fact]
    public void Seal_Produces_Byte_Identical_Ciphertext_To_Reference()
    {
        var key = Enc.FromB64u(Vectors.S("envelope.key"));
        var reference = Vectors.V1["envelope"]!["sealed"].Deserialize<Envelope>(ProtocolJson.Options)!;
        // JSON escaping may legitimately differ between languages (C# escapes non-BMP chars), so the
        // byte-exact check feeds the reference's exact UTF-8 plaintext to validate AES-GCM + AAD layout.
        var sealedHere = EnvelopeCrypto.SealRaw(key, reference with { Ct = "" }, Enc.Utf8("""{"reqId":"r1","text":"Xin chào 👋"}"""), Enc.FromB64u(reference.N));
        Assert.Equal(reference.Ct, sealedHere.Ct);
        // And the JSON path still round-trips semantically.
        var viaJson = EnvelopeCrypto.Seal(key, reference with { Ct = "" }, JsonNode.Parse("""{"reqId":"r1","text":"Xin chào 👋"}""")!);
        Assert.Equal("Xin chào 👋", (string?)EnvelopeCrypto.Open(key, viaJson)["text"]);
    }

    [Fact]
    public void Envelope_Header_Tampering_Is_Detected()
    {
        var key = Enc.FromB64u(Vectors.S("envelope.key"));
        var env = Vectors.V1["envelope"]!["sealed"].Deserialize<Envelope>(ProtocolJson.Options)!;
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCrypto.Open(key, env with { Seq = env.Seq + 1 }));
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCrypto.Open(key, env with { Typ = "session.close" }));
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCrypto.Open(key, env with { Sid = "x" }));
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCrypto.Open(RandomNumberGenerator.GetBytes(32), env));
    }

    [Fact]
    public void Pairing_Transcript_Proofs_And_VerifyCode_Match_Vectors()
    {
        var p = Vectors.V1["pairing"]!;
        string G(string k) => p[k]!.GetValue<string>();
        var t = PairingCrypto.Transcript(G("hostId"), G("clientId"), G("clientEdPk"), G("clientXPk"), G("hostEdPk"), G("hostXPk"), G("pairNonce"));
        Assert.Equal(G("transcript"), System.Text.Encoding.UTF8.GetString(t));
        Assert.Equal(G("qrProof"), Enc.B64u(PairingCrypto.Proof(Enc.FromB64u(G("qrSecret")), t)));
        Assert.Equal(G("codeProof"), Enc.B64u(PairingCrypto.Proof(Enc.Utf8(G("code")), t)));
        Assert.Equal(G("verifyCode"), PairingCrypto.VerifyCode(t));
    }

    [Fact]
    public void Pairing_Codes()
    {
        var c = PairingCrypto.GenerateCode();
        Assert.Equal(10, c.Length);
        Assert.All(c, ch => Assert.Contains(ch, PairingCrypto.CodeAlphabet));
        Assert.Equal("K7MXQ2RTP9", PairingCrypto.NormalizeCode("k7mxq-2rtp9"));
        Assert.Null(PairingCrypto.NormalizeCode("K7MXQ2RTP1"));
        Assert.Null(PairingCrypto.NormalizeCode("short"));
    }

    [Fact]
    public void Rejects_LowOrder_X25519_Point()
    {
        using var k = DeviceKeys.Generate();
        Assert.Throws<CryptographicException>(() => k.SharedSecret(new byte[32]));
    }

    [Fact]
    public void Secret_Export_Import_RoundTrip()
    {
        using var k = DeviceKeys.Generate();
        var s = k.ExportSecret();
        using var k2 = DeviceKeys.ImportSecret(s);
        Assert.Equal(k.DeviceId, k2.DeviceId);
        Assert.Equal(k.XPk, k2.XPk);
    }
}
