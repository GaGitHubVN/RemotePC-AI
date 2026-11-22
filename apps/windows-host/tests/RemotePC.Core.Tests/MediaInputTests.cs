using RemotePC.Core.Input;
using RemotePC.Core.Media;
using Xunit;

namespace RemotePC.Core.Tests;

public class MediaInputTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 1, 1919, 1079)]
    [InlineData(0.5, 0.5, 960, 540)]
    public void Maps_Normalized_To_Pixels_On_Single_Monitor(double nx, double ny, int ex, int ey)
    {
        var m = new PixelRect(0, 0, 1920, 1080);
        Assert.Equal((ex, ey), CoordinateMapper.ToPixel(nx, ny, m));
        var (ax, ay) = CoordinateMapper.ToAbsolute(nx, ny, m, m);
        Assert.Equal((ex, ey), CoordinateMapper.AbsoluteToPixel(ax, ay, m));
    }

    [Fact]
    public void Maps_Into_Secondary_Monitor_With_Negative_Origin_And_HiDpi()
    {
        // Primary 3840x2160 (200 % scaling, still physical pixels), secondary 1920x1080 to the left, offset down.
        var primary = new PixelRect(0, 0, 3840, 2160);
        var left = new PixelRect(-1920, 500, 1920, 1080);
        var vd = new PixelRect(-1920, 0, 5760, 2160);
        foreach (var (nx, ny) in new[] { (0.0, 0.0), (0.25, 0.75), (1.0, 1.0), (0.999, 0.001) })
        {
            var expected = CoordinateMapper.ToPixel(nx, ny, left);
            var (ax, ay) = CoordinateMapper.ToAbsolute(nx, ny, left, vd);
            Assert.Equal(expected, CoordinateMapper.AbsoluteToPixel(ax, ay, vd));
            Assert.InRange(expected.X, left.X, left.Right - 1);
            Assert.InRange(expected.Y, left.Y, left.Bottom - 1);
        }
        var p = CoordinateMapper.ToAbsolute(0.5, 0.5, primary, vd);
        Assert.Equal((1920, 1080), CoordinateMapper.AbsoluteToPixel(p.X, p.Y, vd));
    }

    [Fact]
    public void Clamps_Out_Of_Range_And_Rejects_NaN()
    {
        var m = new PixelRect(0, 0, 100, 100);
        Assert.Equal((0, 99), CoordinateMapper.ToPixel(-3, 7, m));
        Assert.Throws<ArgumentException>(() => CoordinateMapper.ToPixel(double.NaN, 0, m));
    }

    [Fact]
    public void BgraToNv12_Produces_Expected_Colors()
    {
        const int w = 4, h = 4;
        var src = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { src[i * 4] = 0; src[(i * 4) + 1] = 0; src[(i * 4) + 2] = 255; src[(i * 4) + 3] = 255; } // pure red
        var dst = new byte[ColorConvert.Nv12Size(w, h)];
        ColorConvert.BgraToNv12(src, w, h, w * 4, dst, w, h);
        // BT.709 limited: red → Y≈63, U≈102, V≈240
        Assert.All(dst.AsSpan(0, w * h).ToArray(), y => Assert.InRange(y, 60, 66));
        Assert.InRange(dst[w * h], 99, 105);
        Assert.InRange(dst[(w * h) + 1], 236, 242);

        Array.Clear(src); // black (alpha 0 irrelevant)
        ColorConvert.BgraToNv12(src, w, h, w * 4, dst, w, h);
        Assert.Equal(16, dst[0]);
        Assert.Equal(128, dst[w * h]);
    }

    [Fact]
    public void BgraToNv12_Downscales_With_Stride_Padding()
    {
        const int sw = 8, sh = 6, stride = 40; // 8 px * 4 = 32 bytes + 8 padding
        var src = new byte[stride * sh];
        for (var y = 0; y < sh; y++) for (var x = 0; x < sw; x++) src[(y * stride) + (x * 4) + 1] = 255; // green
        var dst = new byte[ColorConvert.Nv12Size(4, 2)];
        ColorConvert.BgraToNv12(src, sw, sh, stride, dst, 4, 2);
        Assert.All(dst.AsSpan(0, 8).ToArray(), y => Assert.InRange(y, 170, 176)); // BT.709 green Y≈173
    }

    [Fact]
    public void FitEven_Preserves_Aspect()
    {
        Assert.Equal((1920, 1080), ColorConvert.FitEven(1920, 1080, 1920, 1080));
        Assert.Equal((1280, 720), ColorConvert.FitEven(2560, 1440, 1280, 720));
        Assert.Equal((1440, 900), ColorConvert.FitEven(2880, 1800, 1600, 900));
        var (w, h) = ColorConvert.FitEven(1366, 768, 1366, 768);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
    }

    [Fact]
    public void Abr_Decreases_On_Loss_And_Recovers()
    {
        var abr = new AdaptiveBitrateController(StreamSettings.For(QualityPreset.Balanced));
        var start = abr.CurrentKbps;
        abr.OnReport(0.2, 50);
        Assert.True(abr.CurrentKbps < start * 0.95);
        for (var i = 0; i < 60; i++) abr.OnReport(0, 50);
        Assert.Equal(8000, abr.CurrentKbps); // capped at MaxKbps
        for (var i = 0; i < 60; i++) abr.OnReport(0.5, 50);
        Assert.Equal(300, abr.CurrentKbps); // floored at MinKbps
        Assert.True(abr.RecommendedFps < 60); // halves the 60 fps target under heavy loss
    }

    [Fact]
    public void Abr_Backs_Off_On_Queueing_Delay()
    {
        var abr = new AdaptiveBitrateController(StreamSettings.For(QualityPreset.Balanced));
        for (var i = 0; i < 5; i++) abr.OnReport(0, 40);
        var before = abr.CurrentKbps;
        abr.OnReport(0, 400);
        Assert.True(abr.CurrentKbps < before);
    }

    [Fact]
    public void Custom_Settings_Are_Clamped()
    {
        var s = new StreamSettings { Preset = QualityPreset.Custom, Fps = 500, MaxWidth = 99999, TargetKbps = 1, MaxKbps = 1, MinKbps = 1 }.Sanitized();
        Assert.Equal(60, s.Fps);
        Assert.Equal(3840, s.MaxWidth);
        Assert.True(s.MinKbps <= s.TargetKbps && s.TargetKbps <= s.MaxKbps);
    }

    [Fact]
    public void H264_Nal_Parsing()
    {
        byte[] au = [0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3, 0, 0, 1, 0x65, 9, 9];
        Assert.Equal([7, 8, 5], H264.NalTypes(au));
        Assert.True(H264.IsKeyFrame(au));
        Assert.False(H264.IsKeyFrame([0, 0, 1, 0x41, 5]));
    }
}
