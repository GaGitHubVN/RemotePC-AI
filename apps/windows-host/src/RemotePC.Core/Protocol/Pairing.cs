using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RemotePC.Core.Protocol;

public static class PairingCrypto
{
    public const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static byte[] Transcript(string hostId, string clientId, string clientEdPk, string clientXPk,
        string hostEdPk, string hostXPk, string pairNonce) =>
        Enc.Utf8($"remotepc-pair-v1|{hostId}|{clientId}|{clientEdPk}|{clientXPk}|{hostEdPk}|{hostXPk}|{pairNonce}");

    public static byte[] Proof(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> transcript)
    {
        var key = Crypto.Hkdf(secret, [], "remotepc/v1/pair-proof");
        try { return HMACSHA256.HashData(key, transcript); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static string VerifyCode(ReadOnlySpan<byte> transcript)
    {
        var h = SHA256.HashData(transcript);
        var n = BinaryPrimitives.ReadUInt32BigEndian(h) % 1_000_000;
        return n.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string GenerateCode()
    {
        Span<char> c = stackalloc char[10];
        for (var i = 0; i < c.Length; i++) c[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(c);
    }

    public static string? NormalizeCode(string input)
    {
        var s = new string(input.ToUpperInvariant().Where(ch => ch is not ('-' or ' ')).ToArray());
        return s.Length == 10 && s.All(CodeAlphabet.Contains) ? s : null;
    }

    public static string FormatCode(string code) => $"{code[..5]}-{code[5..]}";
}
