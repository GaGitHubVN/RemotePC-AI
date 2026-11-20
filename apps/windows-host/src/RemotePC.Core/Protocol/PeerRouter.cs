using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RemotePC.Core.Security;

namespace RemotePC.Core.Protocol;

public sealed record InboundMessage(string From, string Typ, string Sid, JsonNode Payload, Envelope Envelope, PairedDevice? Device);

public enum RejectReason { Malformed, NotForMe, UnknownPeer, Stale, Replay, Sequence, Decrypt }

/// <summary>
/// Opens inbound envelopes (all receiver checks of PROTOCOL.md §3) and seals outbound ones.
/// Transport-agnostic: Internet signaling and the LAN endpoint both feed it.
/// </summary>
public sealed class PeerRouter(DeviceKeys me, DeviceStore devices, TimeProvider time)
{
    private readonly ReplayGuard _guard = new(time);
    private readonly ConcurrentDictionary<string, long> _sendSeq = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _keyCache = new(StringComparer.Ordinal);

    public string MyId => me.DeviceId;

    public event Action<string, RejectReason>? Rejected;

    public InboundMessage? Open(Envelope env)
    {
        if (!env.IsWellFormed()) return Reject(env.From, RejectReason.Malformed);
        if (env.To != me.DeviceId) return Reject(env.From, RejectReason.NotForMe);

        byte[] key;
        PairedDevice? device = null;
        if (env.Typ == "pair.request")
        {
            if (env.Kx is null) return Reject(env.From, RejectReason.Malformed);
            try
            {
                if (Crypto.DeviceIdOf(Enc.FromB64u(env.Kx.EdPk)) != env.From) return Reject(env.From, RejectReason.Malformed);
                key = EnvelopeCrypto.DeriveKey(me, Enc.FromB64u(env.Kx.XPk), env.From);
            }
            catch (Exception e) when (e is FormatException or ArgumentException or CryptographicException)
            {
                return Reject(env.From, RejectReason.Malformed);
            }
        }
        else
        {
            if (env.Kx is not null) return Reject(env.From, RejectReason.Malformed);
            device = devices.Get(env.From);
            if (device is null) return Reject(env.From, RejectReason.UnknownPeer);
            key = KeyFor(device);
        }

        switch (_guard.Check(env))
        {
            case ReplayGuard.Result.Stale: return Reject(env.From, RejectReason.Stale);
            case ReplayGuard.Result.Replay: return Reject(env.From, RejectReason.Replay);
            case ReplayGuard.Result.Sequence: return Reject(env.From, RejectReason.Sequence);
        }

        JsonNode payload;
        try { payload = EnvelopeCrypto.Open(key, env); }
        catch (Exception e) when (e is CryptographicException or FormatException or System.Text.Json.JsonException)
        {
            return Reject(env.From, RejectReason.Decrypt);
        }
        _guard.Accept(env);
        return new InboundMessage(env.From, env.Typ, env.Sid, payload, env, device);
    }

    /// <summary>Seal for a paired device.</summary>
    public Envelope Seal(string peerId, string typ, string sid, JsonNode payload)
    {
        var device = devices.Get(peerId) ?? throw new InvalidOperationException("peer not paired");
        return SealWithKey(KeyFor(device), peerId, typ, sid, payload);
    }

    /// <summary>Seal a reply to an unpaired peer (pair.result) using the key from its pair.request.</summary>
    public Envelope SealForPairing(Envelope request, string typ, JsonNode payload)
    {
        var key = EnvelopeCrypto.DeriveKey(me, Enc.FromB64u(request.Kx!.XPk), request.From);
        return SealWithKey(key, request.From, typ, "", payload);
    }

    private Envelope SealWithKey(byte[] key, string peerId, string typ, string sid, JsonNode payload)
    {
        // Sequence = max(last + 1, now_ms): strictly increasing and never reused after a restart.
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var seq = _sendSeq.AddOrUpdate($"{peerId}|{sid}", now, (_, s) => Math.Max(s + 1, now));
        var header = new Envelope { Typ = typ, From = me.DeviceId, To = peerId, Sid = sid, Seq = seq, Ts = time.GetUtcNow().ToUnixTimeMilliseconds(), N = "" };
        return EnvelopeCrypto.Seal(key, header, payload);
    }

    /// <summary>Drop cached key material (call after revocation).</summary>
    public void Forget(string peerId) => _keyCache.TryRemove(peerId, out _);

    private byte[] KeyFor(PairedDevice d) =>
        _keyCache.GetOrAdd(d.Id + "|" + d.XPk, _ => EnvelopeCrypto.DeriveKey(me, Enc.FromB64u(d.XPk), d.Id));

    private InboundMessage? Reject(string from, RejectReason r)
    {
        Rejected?.Invoke(from, r);
        return null;
    }
}
