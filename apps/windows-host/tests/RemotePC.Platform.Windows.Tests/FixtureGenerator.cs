using RemotePC.Core.Media;
using RemotePC.Platform.Windows.MediaFoundation;
using Xunit;

namespace RemotePC.Platform.Windows.Tests;

/// <summary>
/// Generates the synthetic H.264 clip replayed by tools/RemotePC.TestHost (used for iOS/Android simulator tests in CI,
/// where no Windows desktop exists). Only runs when REMOTEPC_GEN_FIXTURE=1. The picture is drawn here — it never
/// contains the real desktop.
/// Format: repeated [int32 LE length][1 byte keyframe flag][Annex-B access unit].
/// </summary>
public class FixtureGenerator
{
    [Fact]
    [Trait("Category", "Fixture")]
    public void Generate_TestHost_Clip()
    {
        if (Environment.GetEnvironmentVariable("REMOTEPC_GEN_FIXTURE") != "1") return;
        const int w = 1280, h = 720, frames = 90;
        var outPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/RemotePC.TestHost/fixtures/pattern-720p.h264s"));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using var enc = MfH264Encoder.Create(w, h, 30, 1500, preferHardware: false);
        var bgra = new byte[w * h * 4];
        var nv12 = new byte[ColorConvert.Nv12Size(w, h)];
        using var fs = File.Create(outPath);
        using var bw = new BinaryWriter(fs);
        var written = 0;
        for (var i = 0; i < frames + 8 && written < frames; i++)
        {
            Draw(bgra, w, h, Math.Min(i, frames - 1));
            ColorConvert.BgraToNv12(bgra, w, h, w * 4, nv12, w, h);
            foreach (var f in enc.Encode(nv12, i * 333_333L, 0, forceKeyFrame: i % 30 == 0))
            {
                if (written >= frames) break;
                bw.Write(f.Data.Length);
                bw.Write(f.KeyFrame ? (byte)1 : (byte)0);
                bw.Write(f.Data);
                written++;
            }
        }
        Assert.True(written >= 60, $"only {written} frames");
    }

    private static void Rect(byte[] p, int w, int x0, int y0, int rw, int rh, (byte r, byte g, byte b) c)
    {
        for (var y = y0; y < y0 + rh; y++)
            for (var x = x0; x < x0 + rw; x++)
            {
                var o = (y * w + x) * 4;
                p[o] = c.b; p[o + 1] = c.g; p[o + 2] = c.r; p[o + 3] = 255;
            }
    }

    private static void Draw(byte[] p, int w, int h, int t)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                p[o] = (byte)(120 + 80 * y / h); p[o + 1] = (byte)(60 + 40 * x / w); p[o + 2] = (byte)(30 + 30 * y / h); p[o + 3] = 255;
            }
        Rect(p, w, 0, h - 48, w, 48, (24, 26, 34));                       // taskbar
        Rect(p, w, w / 2 - 20, h - 40, 32, 32, (79, 70, 229));            // start button
        Rect(p, w, 140, 90, 620, 400, (245, 246, 250));                   // window
        Rect(p, w, 140, 90, 620, 36, (225, 228, 238));                    // title bar
        Rect(p, w, 712, 100, 14, 14, (220, 60, 60));
        for (var l = 0; l < 8; l++) Rect(p, w, 170, 150 + l * 36, 200 + (l * 53 % 320), 14, (180, 186, 200)); // text lines
        Rect(p, w, 820, 160, 320, 220, (6, 182, 212));                    // second window
        var bx = 840 + (int)(260 * (0.5 + 0.5 * Math.Sin(t * 2 * Math.PI / 90)));
        Rect(p, w, bx, 400, 40, 40, (250, 204, 21));                      // moving square (proves motion)
    }
}
