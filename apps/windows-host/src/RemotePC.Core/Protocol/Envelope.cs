using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RemotePC.Core.Protocol;

public sealed record KeyExchange(
    [property: JsonPropertyName("edPk")] string EdPk,
    [property: JsonPropertyName("xPk")] string XPk);

/// <summary>End-to-end envelope, PROTOCOL.md §3.</summary>
public sealed record Envelope
{
    [JsonPropertyName("v")] public int V { get; init; } = 1;
    [JsonPropertyName("typ")] public required string Typ { get; init; }
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("to")] public required string To { get; init; }
    [JsonPropertyName("sid")] public string Sid { get; init; } = "";
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("ts")] public long Ts { get; init; }
    [JsonPropertyName("n")] public required string N { get; init; }
    [JsonPropertyName("kx")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public KeyExchange? Kx { get; init; }
    [JsonPropertyName("ct")] public string Ct { get; init; } = "";

    public byte[] Aad() => Enc.Utf8(
        $"remotepc-env-v1|{V}|{Typ}|{From}|{To}|{Sid}|{Seq}|{Ts}|{N}|{Kx?.EdPk ?? ""}|{Kx?.XPk ?? ""}");

    /// <summary>Structural validation (receiver check 1). Does not authenticate.</summary>
    public bool IsWellFormed() =>
        V == 1 && Crypto.IsDeviceId(From) && Crypto.IsDeviceId(To) && Typ is { Length: > 0 and <= 41 }
        && Sid.Length <= 32 && N.Length == 16 && Ct.Length is >= 22 and <= 65_536 && Seq >= 0;
}

public static class EnvelopeCrypto
{
    public const int MaxPlaintext = 48 * 1024;

    public static byte[] DeriveKey(DeviceKeys me, ReadOnlySpan<byte> peerXPk, string peerId)
    {
        var (lo, hi) = string.CompareOrdinal(me.DeviceId, peerId) < 0 ? (me.DeviceId, peerId) : (peerId, me.DeviceId);
        var salt = SHA256.HashData(Enc.Utf8($"{lo}|{hi}"));
        var shared = me.SharedSecret(peerXPk);
        try { return Crypto.Hkdf(shared, salt, "remotepc/v1/envelope"); }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }

    public static Envelope Seal(ReadOnlySpan<byte> key, Envelope header, JsonNode payload, byte[]? nonce = null) =>
        SealRaw(key, header, JsonSerializer.SerializeToUtf8Bytes(payload, ProtocolJson.Options), nonce);

    internal static Envelope SealRaw(ReadOnlySpan<byte> key, Envelope header, byte[] plaintext, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(12);
        var h = header with { V = 1, N = Enc.B64u(nonce), Ct = "" };
        if (plaintext.Length > MaxPlaintext) throw new ArgumentException("payload too large");
        var ct = new byte[plaintext.Length + 16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, ct.AsSpan(0, plaintext.Length), ct.AsSpan(plaintext.Length), h.Aad());
        return h with { Ct = Enc.B64u(ct) };
    }

    /// <exception cref="CryptographicException">authentication failed</exception>
    public static JsonNode Open(ReadOnlySpan<byte> key, Envelope env)
    {
        var nonce = Enc.FromB64u(env.N);
        var ct = Enc.FromB64u(env.Ct);
        if (nonce.Length != 12 || ct.Length < 16) throw new CryptographicException("malformed envelope");
        var pt = new byte[ct.Length - 16];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, ct.AsSpan(0, pt.Length), ct.AsSpan(pt.Length), pt, env.Aad());
        return JsonNode.Parse(pt) ?? throw new CryptographicException("empty payload");
    }
}

/// <summary>Receiver checks 3–5 of PROTOCOL.md §3. Thread-safe.</summary>
public sealed class ReplayGuard(TimeProvider time, long windowMs = 120_000)
{
    private readonly Dictionary<string, long> _nonces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _seqs = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public enum Result { Ok, Stale, Replay, Sequence }

    public Result Check(Envelope e)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_lock)
        {
            if (Math.Abs(e.Ts - now) > windowMs) return Result.Stale;
            if (_nonces.ContainsKey($"{e.From}|{e.N}")) return Result.Replay;
            if (_seqs.TryGetValue($"{e.From}|{e.Sid}", out var last) && e.Seq <= last) return Result.Sequence;
            return Result.Ok;
        }
    }

    public void Accept(Envelope e)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_lock)
        {
            _nonces[$"{e.From}|{e.N}"] = now;
            _seqs[$"{e.From}|{e.Sid}"] = e.Seq;
            if (_nonces.Count > 4096)
            {
                foreach (var k in _nonces.Where(kv => now - kv.Value > 600_000).Select(kv => kv.Key).ToList())
                    _nonces.Remove(k);
            }
        }
    }

    /// <summary>Forget sequence state of a session (after it closes).</summary>
    public void ForgetSession(string from, string sid)
    {
        lock (_lock) _seqs.Remove($"{from}|{sid}");
    }
}

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
