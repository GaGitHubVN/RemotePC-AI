using System.Diagnostics;
using RemotePC.Core.Media;
using RemotePC.Core.Security;
using RemotePC.Platform.Windows.Capture;
using RemotePC.Platform.Windows.MediaFoundation;
using RemotePC.Platform.Windows.Security;
using Xunit;
using Xunit.Abstractions;

namespace RemotePC.Platform.Windows.Tests;

/// <summary>
/// Runs against the real machine: DXGI duplication, Media Foundation encoders, DPAPI.
/// Requires an interactive desktop session (not a service / Session 0).
/// </summary>
[Trait("Category", "Hardware")]
public class PipelineHardwareTests(ITestOutputHelper output)
{
    [Fact]
    public void Dpapi_RoundTrip_And_Purpose_Binding()
    {
        var p = new DpapiProtector();
        var data = "bí mật"u8.ToArray();
        var enc = p.Protect(data, "a");
        Assert.Equal(data, p.Unprotect(enc, "a"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => p.Unprotect(enc, "b"));
    }

    [Fact]
    public void Lists_Monitors_And_Encoders()
    {
        var monitors = DesktopDuplicator.ListMonitors();
        foreach (var m in monitors) output.WriteLine($"monitor {m.Index}: {m.DeviceName} {m.Bounds} primary={m.Primary} adapter={m.AdapterName} rot={m.RotationDegrees}");
        Assert.NotEmpty(monitors);
        var encoders = MfH264Encoder.Enumerate();
        foreach (var e in encoders) output.WriteLine($"encoder: {e.Name} hw={e.IsHardware} async={e.IsAsync}");
        Assert.Contains(encoders, e => !e.IsHardware); // Microsoft software H.264 MFT ships with Windows (non-N)
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Captures_And_Encodes_Real_Desktop(bool hardware)
    {
        using var dup = new DesktopDuplicator(0);
        var (w, h) = ColorConvert.FitEven(dup.Width, dup.Height, 1280, 720);
        MfH264Encoder enc;
        try
        {
            enc = MfH264Encoder.Create(w, h, 30, 2500, preferHardware: hardware, log: output.WriteLine);
        }
        catch (NotSupportedException) when (hardware)
        {
            output.WriteLine("NOT VERIFIED: no hardware H.264 encoder usable on this machine");
            return;
        }
        using (enc)
        {
            if (hardware && !enc.IsHardware) output.WriteLine("hardware requested but fell back to software");
            var nv12 = new byte[ColorConvert.Nv12Size(w, h)];
            var frames = new List<EncodedFrame>();
            var convertTicks = 0L;
            for (var i = 0; i < 60; i++)
            {
                var r = dup.Acquire(i == 0 ? 500 : 33);
                Assert.NotEqual(CaptureResult.AccessLost, r);
                if (!dup.HasFrame) continue;
                var t0 = Stopwatch.GetTimestamp();
                dup.MapFrame((span, pitch) => ColorConvert.BgraToNv12(span, dup.Width, dup.Height, pitch, nv12, w, h));
                convertTicks += Stopwatch.GetTimestamp() - t0;
                frames.AddRange(enc.Encode(nv12, i * 333_333L, t0, forceKeyFrame: i == 30));
            }
            Assert.True(frames.Count >= 40, $"only {frames.Count} frames encoded");
            Assert.All(frames, f => Assert.True(H264.HasStartCode(f.Data), "Annex-B start code missing"));
            var first = frames[0];
            Assert.True(first.KeyFrame, "first frame must be IDR");
            Assert.Contains(H264.NalSps, H264.NalTypes(first.Data));
            Assert.Contains(H264.NalPps, H264.NalTypes(first.Data));
            Assert.True(frames.Skip(25).Take(15).Any(f => f.KeyFrame), "forced keyframe not produced");

            var encMs = frames.Average(f => (f.EncodeEndTicks - f.EncodeStartTicks) * 1000.0 / Stopwatch.Frequency);
            output.WriteLine($"{enc.Name}: {frames.Count} frames {w}x{h}, avg encode {encMs:F2} ms, avg convert {convertTicks * 1000.0 / Stopwatch.Frequency / 60:F2} ms, " +
                             $"keyframes={frames.Count(f => f.KeyFrame)}, avg size {frames.Average(f => f.Data.Length) / 1024:F1} KiB");
        }
    }
}
