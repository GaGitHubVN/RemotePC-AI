namespace RemotePC.Core.Media;

/// <summary>Helpers for Annex-B H.264 byte streams.</summary>
public static class H264
{
    public const int NalSlice = 1, NalIdr = 5, NalSei = 6, NalSps = 7, NalPps = 8, NalAud = 9;

    /// <summary>Enumerates NAL unit types found after 3- or 4-byte start codes.</summary>
    public static IEnumerable<int> NalTypes(byte[] annexB)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                yield return annexB[i + 3] & 0x1f;
                i += 3;
            }
        }
    }

    public static bool IsKeyFrame(byte[] annexB) => NalTypes(annexB).Contains(NalIdr);

    public static bool HasStartCode(byte[] annexB) =>
        annexB.Length > 4 && annexB[0] == 0 && annexB[1] == 0 && (annexB[2] == 1 || (annexB[2] == 0 && annexB[3] == 1));
}

/// <summary>Encoded access unit plus timing for diagnostics.</summary>
public sealed record EncodedFrame(byte[] Data, bool KeyFrame, long CaptureTicks, long EncodeStartTicks, long EncodeEndTicks, int Width, int Height);

public interface IVideoEncoder : IDisposable
{
    string Name { get; }
    bool IsHardware { get; }
    int Width { get; }
    int Height { get; }
    /// <summary>Encodes one NV12 frame. May return 0..n access units (async MFTs can lag by a frame).</summary>
    IReadOnlyList<EncodedFrame> Encode(ReadOnlySpan<byte> nv12, long timestamp100ns, long captureTicks, bool forceKeyFrame);
    void SetBitrate(int kbps);
}
