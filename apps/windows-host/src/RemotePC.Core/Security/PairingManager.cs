using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RemotePC.Core.Protocol;

namespace RemotePC.Core.Security;

public enum PairingMode { Qr, Code }

public sealed class PairingOffer
{
    internal byte[] Secret { get; init; } = [];
    public PairingMode Mode { get; init; }
    /// <summary>Only for <see cref="PairingMode.Code"/>: "XXXXX-XXXXX".</summary>
    public string? DisplayCode { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string SecretB64u => Enc.B64u(Secret);
    internal int Failures { get; set; }
    internal bool Used { get; set; }
}

/// <summary>Request awaiting the PC owner's decision in the approval dialog.</summary>
public sealed record PairingApprovalRequest(
    string ClientId, string DeviceName, string Platform, string AppVersion, string VerifyCode);

public sealed record PairingDecision(bool Approved, Permission Permissions, string? DeviceName = null);

/// <summary>Host side of PROTOCOL.md §4.</summary>
public sealed class PairingManager(DeviceKeys host, DeviceStore store, TimeProvider time)
{
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(5);
    public const int MaxFailuresPerOffer = 5;

    private readonly Lock _lock = new();
    private PairingOffer? _offer;
    private readonly Queue<DateTimeOffset> _recentFailures = new();

    /// <summary>Asks the UI to approve. Must return within a reasonable time; null/timeout = rejected.</summary>
    public Func<PairingApprovalRequest, CancellationToken, Task<PairingDecision>>? ApprovalHandler { get; set; }

    public PairingOffer? CurrentOffer
    {
        get { lock (_lock) return IsLive(_offer) ? _offer : null; }
    }

    public PairingOffer CreateOffer(PairingMode mode)
    {
        PairingOffer offer;
        if (mode == PairingMode.Qr)
        {
            offer = new PairingOffer { Mode = mode, Secret = RandomNumberGenerator.GetBytes(32), ExpiresAt = time.GetUtcNow() + OfferLifetime };
        }
        else
        {
            var code = PairingCrypto.GenerateCode();
            offer = new PairingOffer { Mode = mode, Secret = Enc.Utf8(code), DisplayCode = PairingCrypto.FormatCode(code), ExpiresAt = time.GetUtcNow() + OfferLifetime };
        }
        lock (_lock) _offer = offer;
        return offer;
    }

    public void CancelOffer()
    {
        lock (_lock) _offer = null;
    }

    public string BuildQrUri(PairingOffer offer, string hostName, string? signalingUrl, IEnumerable<string> lanEndpoints)
    {
        var q = new List<string>
        {
            "v=1", $"h={host.DeviceId}", $"e={Enc.B64u(host.EdPk)}", $"x={Enc.B64u(host.XPk)}",
            $"s={offer.SecretB64u}", $"n={Uri.EscapeDataString(hostName)}",
            $"exp={offer.ExpiresAt.ToUnixTimeMilliseconds()}",
        };
        if (!string.IsNullOrEmpty(signalingUrl)) q.Add($"sig={Uri.EscapeDataString(signalingUrl)}");
        var lan = string.Join(',', lanEndpoints);
        if (lan.Length > 0) q.Add($"lan={Uri.EscapeDataString(lan)}");
        return "remotepc://pair?" + string.Join('&', q);
    }

    private bool IsLive(PairingOffer? o) => o is { Used: false } && o.ExpiresAt > time.GetUtcNow() && o.Failures < MaxFailuresPerOffer;

    /// <summary>
    /// Processes a decrypted pair.request. The envelope must already have passed AEAD with keys from <c>kx</c>.
    /// Returns the pair.result payload to send back.
    /// </summary>
    public async Task<JsonObject> HandleRequestAsync(Envelope env, JsonNode payload, CancellationToken ct)
    {
        if (env.Kx is null) return Fail("bad_proof");
        var pairNonce = payload["pairNonce"]?.GetValue<string>() ?? "";
        var proofStr = payload["proof"]?.GetValue<string>() ?? "";
        var name = Sanitize(payload["deviceName"]?.GetValue<string>(), 64, "Phone");
        var platform = Sanitize(payload["platform"]?.GetValue<string>(), 16, "unknown");
        var appVersion = Sanitize(payload["appVersion"]?.GetValue<string>(), 32, "");
        if (pairNonce.Length is < 8 or > 64) return Fail("bad_proof");

        PairingOffer offer;
        byte[] transcript;
        lock (_lock)
        {
            var now = time.GetUtcNow();
            while (_recentFailures.Count > 0 && now - _recentFailures.Peek() > TimeSpan.FromMinutes(1)) _recentFailures.Dequeue();
            if (_recentFailures.Count >= 20) return Fail("locked");
            if (_offer is null) return Fail("expired");
            if (!IsLive(_offer)) return Fail(_offer.Failures >= MaxFailuresPerOffer ? "locked" : "expired");
            offer = _offer;

            transcript = PairingCrypto.Transcript(host.DeviceId, env.From, env.Kx.EdPk, env.Kx.XPk,
                Enc.B64u(host.EdPk), Enc.B64u(host.XPk), pairNonce);
            var expected = PairingCrypto.Proof(offer.Secret, transcript);
            byte[] given;
            try { given = Enc.FromB64u(proofStr); } catch (FormatException) { given = []; }
            if (!CryptographicOperations.FixedTimeEquals(expected, given))
            {
                offer.Failures++;
                _recentFailures.Enqueue(now);
                return Fail("bad_proof");
            }
            offer.Used = true; // single use, even if the owner rejects
        }

        var verify = PairingCrypto.VerifyCode(transcript);
        var handler = ApprovalHandler;
        if (handler is null) return Fail("rejected");

        PairingDecision decision;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { decision = await handler(new PairingApprovalRequest(env.From, name, platform, appVersion, verify), timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return Fail("rejected"); }
        }
        if (!decision.Approved) return Fail("rejected");

        store.Upsert(new PairedDevice
        {
            Id = env.From,
            Name = Sanitize(decision.DeviceName, 64, name),
            Platform = platform,
            EdPk = env.Kx.EdPk,
            XPk = env.Kx.XPk,
            Permissions = decision.Permissions,
            PairedAt = time.GetUtcNow(),
        });
        return new JsonObject
        {
            ["ok"] = true,
            ["hostName"] = Environment.MachineName,
            ["permissions"] = new JsonArray(PermissionNames.ToNames(decision.Permissions).Select(n => (JsonNode)n).ToArray()),
        };
    }

    private static JsonObject Fail(string reason) => new() { ["ok"] = false, ["reason"] = reason };

    internal static string Sanitize(string? s, int max, string fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var clean = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return fallback;
        return clean.Length > max ? clean[..max] : clean;
    }
}
