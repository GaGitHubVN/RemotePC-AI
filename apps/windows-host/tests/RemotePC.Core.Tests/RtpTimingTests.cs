using System.Diagnostics;
using RemotePC.Core.Session;
using Xunit;

namespace RemotePC.Core.Tests;

public sealed class RtpTimingTests
{
    [Fact]
    public void Rtp_Timestamp_Follows_Real_Capture_Interval()
    {
        long last = 0;
        var f = Stopwatch.Frequency;
        Assert.Equal(3000u, HostSession.RtpDuration(ref last, 10 * f, 30));          // first frame: nominal 1/30 s
        Assert.Equal(9000u, HostSession.RtpDuration(ref last, 10 * f + f / 10, 30)); // 100 ms later → 9000 @ 90 kHz
        Assert.Equal(90000u, HostSession.RtpDuration(ref last, 20 * f, 30));         // long idle gap clamped to 1 s
        Assert.Equal(3000u, HostSession.RtpDuration(ref last, 20 * f, 30));          // non-increasing clock → nominal
    }
}
