using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace RemotePC.Core.Protocol;

/// <summary>Device identity: Ed25519 (signaling auth) + X25519 (envelope key agreement). PROTOCOL.md §1.</summary>
public sealed class DeviceKeys : IDisposable
{
    private readonly byte[] _edSeed;
    private readonly byte[] _xSk;

    public byte[] EdPk { get; }
    public byte[] XPk { get; }
    public string DeviceId { get; }

    private DeviceKeys(byte[] edSeed, byte[] xSk)
    {
        if (edSeed.Length != 32 || xSk.Length != 32) throw new ArgumentException("keys must be 32 bytes");
        _edSeed = edSeed;
        _xSk = xSk;
        EdPk = new Ed25519PrivateKeyParameters(edSeed).GeneratePublicKey().GetEncoded();
        XPk = new X25519PrivateKeyParameters(xSk).GeneratePublicKey().GetEncoded();
        DeviceId = Crypto.DeviceIdOf(EdPk);
    }

    public static DeviceKeys FromSeeds(byte[] edSeed, byte[] xSk) => new((byte[])edSeed.Clone(), (byte[])xSk.Clone());

    public static DeviceKeys Generate() => new(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

    /// <summary>Secret material for persistence through <see cref="Security.ISecretProtector"/>. Caller must zero it.</summary>
    public byte[] ExportSecret()
    {
        var b = new byte[64];
        _edSeed.CopyTo(b, 0);
        _xSk.CopyTo(b, 32);
        return b;
    }

    public static DeviceKeys ImportSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != 64) throw new ArgumentException("secret must be 64 bytes");
        return new DeviceKeys(secret[..32].ToArray(), secret[32..].ToArray());
    }

    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(_edSeed));
        var m = message.ToArray();
        signer.BlockUpdate(m, 0, m.Length);
        return signer.GenerateSignature();
    }

    public byte[] SharedSecret(ReadOnlySpan<byte> peerXPk)
    {
        if (peerXPk.Length != 32) throw new ArgumentException("peer key must be 32 bytes");
        var agreement = new Org.BouncyCastle.Crypto.Agreement.X25519Agreement();
        agreement.Init(new X25519PrivateKeyParameters(_xSk));
        var shared = new byte[32];
        try
        {
            // BouncyCastle rejects an all-zero result (low-order peer point) with InvalidOperationException.
            agreement.CalculateAgreement(new X25519PublicKeyParameters(peerXPk.ToArray()), shared, 0);
        }
        catch (InvalidOperationException e)
        {
            throw new CryptographicException("invalid peer public key", e);
        }
        if (CryptographicOperations.FixedTimeEquals(shared, new byte[32]))
            throw new CryptographicException("invalid peer public key");
        return shared;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_edSeed);
        CryptographicOperations.ZeroMemory(_xSk);
    }
}

public static class Crypto
{
    public static string DeviceIdOf(ReadOnlySpan<byte> edPk)
    {
        if (edPk.Length != 32) throw new ArgumentException("edPk must be 32 bytes");
        return Enc.Base32(SHA256.HashData(edPk))[..26];
    }

    public static bool IsDeviceId(string? s) =>
        s is { Length: 26 } && s.All(c => c is (>= 'a' and <= 'z') or (>= '2' and <= '7'));

    public static bool EdVerify(ReadOnlySpan<byte> pk, ReadOnlySpan<byte> message, ReadOnlySpan<byte> sig)
    {
        if (pk.Length != 32 || sig.Length != 64) return false;
        try
        {
            var v = new Ed25519Signer();
            v.Init(false, new Ed25519PublicKeyParameters(pk.ToArray()));
            var m = message.ToArray();
            v.BlockUpdate(m, 0, m.Length);
            return v.VerifySignature(sig.ToArray());
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, string info, int length = 32)
    {
        var output = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, Enc.Utf8(info));
        return output;
    }
}
