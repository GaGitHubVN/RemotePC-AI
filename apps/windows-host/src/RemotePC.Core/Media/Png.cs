using System.Buffers.Binary;
using System.IO.Compression;

namespace RemotePC.Core.Media;

/// <summary>Minimal PNG (RGB8) encoder from BGRA input with optional downscale. No external dependencies.</summary>
public static class Png
{
    private static readonly uint[] CrcTable = BuildCrc();

    public static byte[] FromBgra(ReadOnlySpan<byte> bgra, int width, int height, int stride, int maxWidth = int.MaxValue)
    {
        var scale = Math.Min(1.0, (double)maxWidth / width);
        var w = Math.Max(1, (int)(width * scale));
        var h = Math.Max(1, (int)(height * scale));
        var raw = new byte[(w * 3 + 1) * h];
        var o = 0;
        for (var y = 0; y < h; y++)
        {
            raw[o++] = 0; // filter: none
            var sy = Math.Min(height - 1, (int)(y / scale));
            for (var x = 0; x < w; x++)
            {
                var sx = Math.Min(width - 1, (int)(x / scale));
                var i = (sy * stride) + (sx * 4);
                raw[o++] = bgra[i + 2];
                raw[o++] = bgra[i + 1];
                raw[o++] = bgra[i];
            }
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // color type RGB
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in t) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(len, crc ^ 0xFFFFFFFFu);
        s.Write(len);
    }

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
