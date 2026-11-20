using System.Buffers.Binary;
using RemotePC.Core.Media;
using RemotePC.Core.Protocol;
using RemotePC.Core.Security;
using RemotePC.Core.Session;
using RemotePC.Core.Tests;
using RemotePC.Platform.Windows.Capture;
using RemotePC.Platform.Windows.Input;
using RemotePC.Platform.Windows.Security;
using Xunit;
using Xunit.Abstractions;

namespace RemotePC.Platform.Windows.Tests;

[Trait("Category", "Hardware")]
public sealed class RealStreamE2ETests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_Desktop_Streams_To_WebRtc_Client()
    {
        using var dir = new TempDir();
        using var host = DeviceKeys.Generate();
        using var phoneKeys = DeviceKeys.Generate();
        var store = new DeviceStore(dir.File("d.dat"), new DpapiProtector());
        store.Upsert(new PairedDevice { Id = phoneKeys.DeviceId, Name = "p", Platform = "test", EdPk = Enc.B64u(phoneKeys.EdPk), XPk = Enc.B64u(phoneKeys.XPk), Permissions = Permission.ViewScreen });
        var router = new PeerRouter(host, store, TimeProvider.System);
        var mgr = new SessionManager(router, store, new DesktopScreenSource(output.WriteLine), new WindowsInputSink(), TimeProvider.System)
        {
            IceServerProvider = _ => Task.FromResult(new List<SIPSorcery.Net.RTCIceServer>()),
        };
        TestPhone phone = null!;
        phone = new TestPhone(phoneKeys, host, async env =>
        {
            var m = router.Open(env);
            if (m is not null) await mgr.HandleAsync(m, e => { phone.Receive(e); return true; });
        });
        await using (phone)
        {
            await phone.OpenSessionAsync();
            await phone.ConnectWebRtcAsync();
            await phone.Connected.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await phone.WaitChannelsOpenAsync();
            // A static desktop still produces a refresh frame every second; wait for a few.
            for (var i = 0; i < 100 && phone.VideoFrames.Count < 3; i++) await Task.Delay(50);
            var frames = phone.VideoFrames.ToArray();
            Assert.True(frames.Length >= 3, $"received {frames.Length} frames");
            Assert.Contains(frames, f => H264.IsKeyFrame(f) && H264.NalTypes(f).Contains(H264.NalSps));
            var stats = await phone.CallAsync("stream.stats");
            output.WriteLine("stats: " + stats["r"]);
            Assert.False(string.IsNullOrEmpty((string?)stats["r"]!["encoder"]));
            await mgr.CloseAllAsync("test_end");
        }
    }

    [Fact]
    public void Screenshot_Png_Is_Valid()
    {
        var png = new DesktopScreenSource().CapturePng(0, maxWidth: 800);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        var w = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        Assert.InRange(w, 1, 800);
        output.WriteLine($"png {w}x{BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))}, {png.Length / 1024} KiB");
    }

    [Fact]
    public void Clipboard_RoundTrip_Unicode()
    {
        var cb = new WindowsClipboard();
        var previous = cb.GetText();
        try
        {
            Assert.True(cb.SetText("Tiếng Việt có dấu ✓"));
            Assert.Equal("Tiếng Việt có dấu ✓", cb.GetText());
        }
        finally
        {
            if (previous is not null) cb.SetText(previous);
        }
    }
}
