namespace RemotePC.Core.Media;

/// <summary>
/// CPU BGRA → NV12 (BT.709 limited range) with box-filter downscaling. Used by the system-memory encode path;
/// a GPU video-processor path can replace it without changing callers.
/// </summary>
public static class ColorConvert
{
    /// <summary>NV12 buffer size for even dimensions.</summary>
    public static int Nv12Size(int width, int height) => width * height * 3 / 2;

    /// <summary>
    /// Converts <paramref name="src"/> (BGRA, <paramref name="srcStride"/> bytes per row) into NV12 of size
    /// dstW×dstH (both even). Scaling uses an integer-ratio-free box average of 2×2 source samples per chroma site.
    /// </summary>
    public static unsafe void BgraToNv12(ReadOnlySpan<byte> src, int srcW, int srcH, int srcStride,
        Span<byte> dst, int dstW, int dstH)
    {
        if ((dstW & 1) != 0 || (dstH & 1) != 0) throw new ArgumentException("destination size must be even");
        if (dst.Length < Nv12Size(dstW, dstH)) throw new ArgumentException("destination too small");
        if (src.Length < (srcH - 1) * srcStride + (srcW * 4)) throw new ArgumentException("source too small");

        // Fixed-point source coordinate per destination pixel (16.16).
        var xStep = ((long)srcW << 16) / dstW;
        var yStep = ((long)srcH << 16) / dstH;
        Span<int> xMap = dstW <= 8192 ? stackalloc int[dstW] : new int[dstW];
        for (var x = 0; x < dstW; x++) xMap[x] = (int)Math.Min(srcW - 1, (x * xStep + (xStep >> 1)) >> 16) * 4;

        fixed (byte* s = src)
        fixed (byte* d = dst)
        {
            var yPlane = d;
            var uvPlane = d + (dstW * dstH);
            for (var y = 0; y < dstH; y += 2)
            {
                var sy0 = (int)Math.Min(srcH - 1, (y * yStep + (yStep >> 1)) >> 16);
                var sy1 = (int)Math.Min(srcH - 1, ((y + 1) * yStep + (yStep >> 1)) >> 16);
                var row0 = s + ((long)sy0 * srcStride);
                var row1 = s + ((long)sy1 * srcStride);
                var y0 = yPlane + ((long)y * dstW);
                var y1 = y0 + dstW;
                var uv = uvPlane + ((long)(y >> 1) * dstW);
                for (var x = 0; x < dstW; x += 2)
                {
                    var a = row0 + xMap[x];
                    var b = row0 + xMap[x + 1];
                    var c = row1 + xMap[x];
                    var e = row1 + xMap[x + 1];
                    y0[x] = Luma(a);
                    y0[x + 1] = Luma(b);
                    y1[x] = Luma(c);
                    y1[x + 1] = Luma(e);
                    int bb = a[0] + b[0] + c[0] + e[0];
                    int gg = a[1] + b[1] + c[1] + e[1];
                    int rr = a[2] + b[2] + c[2] + e[2];
                    // BT.709 limited range, values summed over 4 pixels (hence >> 2 folded into the shift).
                    uv[x] = Clamp(((-26 * rr - 87 * gg + 112 * bb) >> 10) + 128);
                    uv[x + 1] = Clamp(((112 * rr - 102 * gg - 10 * bb) >> 10) + 128);
                }
            }
        }
    }

    private static unsafe byte Luma(byte* p) => (byte)(((47 * p[2] + 157 * p[1] + 16 * p[0] + 128) >> 8) + 16);

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>Fits a source size into a maximum size, preserving aspect ratio, with even dimensions.</summary>
    public static (int W, int H) FitEven(int srcW, int srcH, int maxW, int maxH)
    {
        var scale = Math.Min(1.0, Math.Min((double)maxW / srcW, (double)maxH / srcH));
        var w = Math.Max(16, (int)Math.Round(srcW * scale) & ~1);
        var h = Math.Max(16, (int)Math.Round(srcH * scale) & ~1);
        return (w, h);
    }
}
