using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using RemotePC.Core.Protocol;

namespace RemotePC.Core.Signaling;

public enum SignalingState { Disconnected, Connecting, Online, Backoff }

/// <summary>
/// Persistent authenticated connection to a signaling server (PROTOCOL.md §2) with exponential backoff
/// reconnect and app-level heartbeat. Raises <see cref="EnvelopeReceived"/> for every relayed envelope.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private readonly Uri _url;
    private readonly DeviceKeys _keys;
    private readonly string _role;
    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<JsonObject>? _turnWaiter;
    private Task? _loop;

    public SignalingState State { get; private set; } = SignalingState.Disconnected;
    public string? LastError { get; private set; }
    public TimeSpan? LastRtt { get; private set; }

    public event Action<Envelope>? EnvelopeReceived;
    public event Action<SignalingState>? StateChanged;
    public event Action<string, string?>? ServerError;

    /// <summary>Retry immediately instead of waiting for the backoff timer.</summary>
    private TaskCompletionSource _retryNow = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SignalingClient(Uri url, DeviceKeys keys, string role)
    {
        if (url.Scheme != "wss" && !(url.Scheme == "ws" && (url.IsLoopback || IsPrivate(url.Host))))
            throw new ArgumentException("signaling must use wss:// (ws:// only for loopback/LAN development)");
        _url = url;
        _keys = keys;
        _role = role;
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public void RetryNow() => _retryNow.TrySetResult();

    public bool Send(Envelope env)
    {
        if (State != SignalingState.Online) return false;
        var msg = new JsonObject { ["t"] = "relay", ["to"] = env.To, ["env"] = JsonSerializer.SerializeToNode(env, ProtocolJson.Options) };
        return _outbox.Writer.TryWrite(msg.ToJsonString());
    }

    public async Task<JsonObject?> RequestTurnAsync(TimeSpan timeout)
    {
        if (State != SignalingState.Online) return null;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _turnWaiter = tcs;
        _outbox.Writer.TryWrite("{\"t\":\"turn\"}");
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    private void SetState(SignalingState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetState(SignalingState.Connecting);
            var connectedAt = DateTimeOffset.UtcNow;
            try
            {
                await ConnectOnceAsync(ct).ConfigureAwait(false);
                LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is WebSocketException or IOException or JsonException or InvalidOperationException or OperationCanceledException or System.Net.Http.HttpRequestException)
            {
                LastError = e.Message;
            }
            if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromSeconds(30)) attempt = 0; // was stable
            SetState(SignalingState.Backoff);
            var delay = Backoff(attempt++);
            _retryNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.WhenAny(Task.Delay(delay, ct), _retryNow.Task).ConfigureAwait(false);
        }
        SetState(SignalingState.Disconnected);
    }

    /// <summary>Exponential backoff 1s → 60s with ±20 % jitter.</summary>
    internal static TimeSpan Backoff(int attempt)
    {
        var baseMs = Math.Min(60_000, 1000 * Math.Pow(2, Math.Min(attempt, 6)));
        var jitter = 1 + ((Random.Shared.NextDouble() * 0.4) - 0.2);
        return TimeSpan.FromMilliseconds(baseMs * jitter);
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(_url, connectTimeout.Token).ConfigureAwait(false);
        }

        var challenge = await ReceiveJsonAsync(ws, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("no challenge");
        if ((string?)challenge["t"] != "challenge") throw new InvalidOperationException("expected challenge");
        var nonce = (string?)challenge["nonce"] ?? throw new InvalidOperationException("no nonce");
        var sig = _keys.Sign(Enc.Utf8($"remotepc-signal-v1\n{nonce}\n{_keys.DeviceId}\n{_role}"));
        await SendTextAsync(ws, new JsonObject
        {
            ["t"] = "hello", ["v"] = 1, ["role"] = _role, ["id"] = _keys.DeviceId,
            ["edPk"] = Enc.B64u(_keys.EdPk), ["sig"] = Enc.B64u(sig),
        }.ToJsonString(), ct).ConfigureAwait(false);
        var welcome = await ReceiveJsonAsync(ws, ct).ConfigureAwait(false);
        if ((string?)welcome?["t"] != "welcome") throw new InvalidOperationException("authentication rejected");

        SetState(SignalingState.Online);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sender = SendLoopAsync(ws, session.Token);
        var heartbeat = HeartbeatAsync(session.Token);
        try
        {
            while (!session.IsCancellationRequested)
            {
                var msg = await ReceiveJsonAsync(ws, session.Token).ConfigureAwait(false);
                if (msg is null) break;
                Dispatch(msg);
            }
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(sender, heartbeat).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private DateTimeOffset _pingSent;

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            _pingSent = DateTimeOffset.UtcNow;
            _outbox.Writer.TryWrite("{\"t\":\"ping\"}");
        }
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (await _outbox.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_outbox.Reader.TryRead(out var m))
                await SendTextAsync(ws, m, ct).ConfigureAwait(false);
        }
    }

    private void Dispatch(JsonObject msg)
    {
        switch ((string?)msg["t"])
        {
            case "relay":
                var env = msg["env"].Deserialize<Envelope>(ProtocolJson.Options);
                // The server-authenticated sender must match the envelope's claimed sender.
                if (env is not null && (string?)msg["from"] == env.From) EnvelopeReceived?.Invoke(env);
                break;
            case "pong":
                LastRtt = DateTimeOffset.UtcNow - _pingSent;
                break;
            case "turn":
                _turnWaiter?.TrySetResult(msg);
                break;
            case "error":
                ServerError?.Invoke((string?)msg["code"] ?? "unknown", (string?)msg["ref"]);
                break;
        }
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

    internal static async Task<JsonObject?> ReceiveJsonAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 65_536) throw new InvalidOperationException("frame too large");
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray()) as JsonObject;
    }

    internal static bool IsPrivate(string host) =>
        System.Net.IPAddress.TryParse(host, out var ip) && Net.NetworkInfo.IsPrivate(ip);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
