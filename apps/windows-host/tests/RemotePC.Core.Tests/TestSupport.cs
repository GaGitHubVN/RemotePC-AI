using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RemotePC.Core.Security;

namespace RemotePC.Core.Tests;

/// <summary>TEST-ONLY protector (production uses DPAPI in RemotePC.Platform.Windows).</summary>
internal sealed class TestProtector : ISecretProtector
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public byte[] Protect(byte[] plaintext, string purpose)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length + 16];
        using var g = new AesGcm(_key, 16);
        g.Encrypt(nonce, plaintext, ct.AsSpan(0, plaintext.Length), ct.AsSpan(plaintext.Length), System.Text.Encoding.UTF8.GetBytes(purpose));
        return [.. nonce, .. ct];
    }

    public byte[] Unprotect(byte[] data, string purpose)
    {
        var pt = new byte[data.Length - 28];
        using var g = new AesGcm(_key, 16);
        g.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, pt.Length), data.AsSpan(12 + pt.Length), pt, System.Text.Encoding.UTF8.GetBytes(purpose));
        return pt;
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remotepc-tests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

internal static class Vectors
{
    public static readonly JsonNode V1 = JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "vectors", "v1.json")))!;
    public static string S(string path)
    {
        JsonNode? n = V1;
        foreach (var p in path.Split('.')) n = n![p];
        return n!.GetValue<string>();
    }
}
