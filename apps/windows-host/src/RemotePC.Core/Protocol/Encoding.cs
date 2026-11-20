using System.Text;

namespace RemotePC.Core.Protocol;

/// <summary>Encodings defined in PROTOCOL.md: base64url without padding, lowercase RFC 4648 base32.</summary>
public static class Enc
{
    public static string B64u(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64u(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        foreach (var c in s)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                throw new FormatException("invalid base64url");
        }
        var b = s.Replace('-', '+').Replace('_', '/');
        b = (b.Length % 4) switch { 2 => b + "==", 3 => b + "=", 1 => throw new FormatException("invalid base64url length"), _ => b };
        return Convert.FromBase64String(b);
    }

    private const string B32 = "abcdefghijklmnopqrstuvwxyz234567";

    public static string Base32(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(B32[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
            value &= (1 << bits) - 1;
        }
        if (bits > 0) sb.Append(B32[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
