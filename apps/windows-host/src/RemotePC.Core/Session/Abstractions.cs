using System.Text.Json.Nodes;
using RemotePC.Core.Input;
using RemotePC.Core.Media;
using RemotePC.Core.Security;

namespace RemotePC.Core.Session;

public sealed record DisplayDesc(int Index, string Name, PixelRect Bounds, bool Primary, int RotationDegrees);

public sealed record StreamStats(int Fps, int Kbps, int Width, int Height, string Encoder, bool Hardware,
    double CaptureMs, double ConvertMs, double EncodeMs, long FramesSent, long FramesDropped);

/// <summary>A running capture+encode pipeline for one display.</summary>
public interface IScreenPipeline : IDisposable
{
    int DisplayIndex { get; }
    void RequestKeyFrame();
    void SetBitrate(int kbps);
    void SetFps(int fps);
    StreamStats Stats { get; }
}

public interface IScreenSource
{
    IReadOnlyList<DisplayDesc> Displays();
    /// <summary>Starts streaming. <paramref name="onFrame"/> is invoked on the capture thread.</summary>
    IScreenPipeline Start(int displayIndex, StreamSettings settings, Action<EncodedFrame> onFrame);
    /// <summary>One-off still image (PNG) for screenshots and the AI agent.</summary>
    byte[] CapturePng(int displayIndex, int maxWidth = 1920);
}

public interface IInputSink
{
    bool Move(double nx, double ny, PixelRect monitor);
    bool Button(string button, bool down, double nx, double ny, PixelRect monitor);
    bool Wheel(int dx, int dy);
    bool Key(ushort vk, bool down);
    bool Combo(IReadOnlyList<ushort> vks);
    bool Text(string text);
    void ReleaseAll();
}

public interface IClipboard
{
    string? GetText();
    bool SetText(string text);
}

/// <summary>Context of one RPC call. Permission checks have already passed when a handler runs.</summary>
public sealed class RpcContext
{
    public required HostSession Session { get; init; }
    public required PairedDevice Device { get; init; }
    public required long RequestId { get; init; }
    public required CancellationToken Cancellation { get; init; }
    /// <summary>Sends an event correlated with this request (e.g. streamed AI tokens).</summary>
    public void Emit(string ev, JsonNode payload) => Session.SendEvent(ev, payload, RequestId);
}

public delegate Task<JsonNode?> RpcHandler(JsonNode? p, RpcContext ctx);

/// <summary>Thrown by handlers to return a structured error to the client.</summary>
public sealed class RpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class RpcRegistry
{
    private readonly Dictionary<string, (Permission Required, RpcHandler Handler)> _methods = new(StringComparer.Ordinal);

    /// <param name="required">Permission.None means any paired, connected device may call it.</param>
    public void Register(string method, Permission required, RpcHandler handler)
    {
        if (!_methods.TryAdd(method, (required, handler))) throw new InvalidOperationException($"duplicate RPC method {method}");
    }

    public bool TryGet(string method, out (Permission Required, RpcHandler Handler) entry) => _methods.TryGetValue(method, out entry);

    public IEnumerable<string> Methods => _methods.Keys;
}
