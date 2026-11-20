using RemotePC.Core.Session;
using SIPSorcery.Net;
using Xunit;

namespace RemotePC.Core.Tests;

/// <summary>Regression: JSON null username/credential crashed flutter_webrtc on iOS (NSNull passed as NSString).</summary>
public class IceJsonTests
{
    [Fact]
    public void Stun_Entry_Has_No_Null_Fields()
    {
        var json = SessionManager.IceJson(new RTCIceServer { urls = "stun:stun.l.google.com:19302" }).ToJsonString();
        Assert.Equal("""{"urls":["stun:stun.l.google.com:19302"]}""", json);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Turn_Entry_Keeps_Credentials_And_Splits_Urls()
    {
        var json = SessionManager.IceJson(new RTCIceServer { urls = "turn:t.example:3478?transport=udp, turns:t.example:443", username = "u", credential = "c" }).ToJsonString();
        Assert.Equal("""{"urls":["turn:t.example:3478?transport=udp","turns:t.example:443"],"username":"u","credential":"c"}""", json);
    }
}
