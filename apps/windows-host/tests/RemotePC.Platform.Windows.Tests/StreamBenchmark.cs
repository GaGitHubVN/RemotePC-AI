using System.Diagnostics;
using System.Runtime.InteropServices;
using RemotePC.Core.Media;
using RemotePC.Platform.Windows.Capture;
using Xunit;
using Xunit.Abstractions;

namespace RemotePC.Platform.Windows.Tests;

/// <summary>
/// Real capture → convert → encode throughput on this PC while the desktop changes every frame.
/// Opt-in (REMOTEPC_BENCH=1) because it paints a small animated square on the real screen for a few seconds.
/// </summary>
public sealed class StreamBenchmark(ITestOutputHelper output)
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool PatBlt(IntPtr dc, int x, int y, int w, int h, uint rop);
    private const uint DSTINVERT = 0x00550009;

    [Fact]
    public void Pipeline_Fps_On_A_Changing_Desktop()
    {
        if (Environment.GetEnvironmentVariable("REMOTEPC_BENCH") != "1") return;
        foreach (var preset in new[] { QualityPreset.Auto, QualityPreset.LowLatency })
        {
            var settings = StreamSettings.For(preset);
            var src = new DesktopScreenSource(output.WriteLine);
            long frames = 0, bytes = 0;
            var stop = false;
            // Animate: invert a 200×200 square at ~120 Hz so every vsync has new content.
            var painter = new Thread(() =>
            {
                var dc = GetDC(IntPtr.Zero);
                var i = 0;
                while (!Volatile.Read(ref stop)) { _ = PatBlt(dc, 100 + (i++ % 50) * 4, 100, 200, 200, DSTINVERT); Thread.Sleep(4); }
                _ = ReleaseDC(IntPtr.Zero, dc);
            }) { IsBackground = true };
            painter.Start();
            using (var p = src.Start(0, settings, f => { Interlocked.Increment(ref frames); Interlocked.Add(ref bytes, f.Data.Length); }))
            {
                Thread.Sleep(1500); // warm-up (encoder creation, first IDR)
                var f0 = Interlocked.Read(ref frames);
                var sw = Stopwatch.StartNew();
                Thread.Sleep(5000);
                var fps = (Interlocked.Read(ref frames) - f0) / sw.Elapsed.TotalSeconds;
                var s = p.Stats;
                output.WriteLine($"{preset}: {fps:F1} fps delivered (target {settings.Fps}), {s.Width}x{s.Height} {s.Encoder} hw={s.Hardware} " +
                                 $"capture {s.CaptureMs:F1} ms, convert {s.ConvertMs:F1} ms, encode {s.EncodeMs:F1} ms, dropped {s.FramesDropped}, " +
                                 $"{bytes * 8 / 1000 / 6.5:F0} kbps");
            }
            Volatile.Write(ref stop, true);
            painter.Join();
        }
    }
}
