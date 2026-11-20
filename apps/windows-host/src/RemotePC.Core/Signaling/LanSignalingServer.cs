using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemotePC.Core.Protocol;

namespace RemotePC.Core.Signaling;

/// <summary>
/// LAN signaling endpoint served by the host itself (PROTOCOL.md §2, §6), so phones on the same network work
/// without Internet. Same challenge/hello authentication as the public server; it only routes envelopes
/// addressed to this host. Plain ws:// — confidentiality/integrity come from the end-to-end envelope (ADR-0004).
/// Implemented on TcpListener + WebSocket.CreateFromStream so no URL ACL / admin rights are needed.
/// </summary>
public sealed class LanSignalingServer : IAsyncDisposable
{
    public const int DefaultPort = 45822;
    private readonly DeviceKeys _host;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _handshakesPerIp = new(StringComparer.Ordinal);
    private Task? _accept;
    private DateTime _windowStart = DateTime.UtcNow;

    public int Port { get; private set; }
    public bool AllowNonPrivateClients { get; init; }
    /// <summary>Envelope from an authenticated LAN client. The second argument sends a reply to that client.</summary>
    public event Action<Envelope, Func<Envelope, bool>>? EnvelopeReceived;

    public LanSignalingServer(DeviceKeys host, int port = DefaultPort)
    {
        _host = host;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoopAsync(_cts.Token);
    }

    public bool SendTo(string deviceId, Envelope env) =>
        _clients.TryGetValue(deviceId, out var ws) && TrySend(ws, Relay(env));

    private static string Relay(Envelope env) =>
        new JsonObject { ["t"] = "relay", ["from"] = env.From, ["env"] = JsonSerializer.SerializeToNode(env, ProtocolJson.Options) }.ToJsonString();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private bool RateLimited(string ip)
    {
        if (DateTime.UtcNow - _windowStart > TimeSpan.FromMinutes(1)) { _handshakesPerIp.Clear(); _windowStart = DateTime.UtcNow; }
        return _handshakesPerIp.AddOrUpdate(ip, 1, (_, n) => n + 1) > 30;
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var tcp = client;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        if ((!AllowNonPrivateClients && !Net.NetworkInfo.IsPrivate(remote.Address)) || RateLimited(remote.Address.ToString())) return;
        client.NoDelay = true;
        var stream = client.GetStream();
        WebSocket ws;
        try
        {
            using var hsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (!await UpgradeAsync(stream, hsTimeout.Token).ConfigureAwait(false)) return;
            ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { return; }

        string? id = null;
        try
        {
            var nonce = Enc.B64u(RandomNumberGenerator.GetBytes(32));
            await SendAsync(ws, new JsonObject { ["t"] = "challenge", ["v"] = 1, ["nonce"] = nonce, ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }.ToJsonString(), ct).ConfigureAwait(false);
            using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var hello = await SignalingClient.ReceiveJsonAsync(ws, helloTimeout.Token).ConfigureAwait(false);
                id = Authenticate(hello, nonce);
            }
            if (id is null)
            {
                await ws.CloseAsync((WebSocketCloseStatus)4004, "authentication failed", ct).ConfigureAwait(false);
                return;
            }
            if (_clients.TryRemove(id, out var old)) _ = old.CloseAsync((WebSocketCloseStatus)4003, "replaced", CancellationToken.None);
            _clients[id] = ws;
            await SendAsync(ws, new JsonObject { ["t"] = "welcome", ["v"] = 1, ["id"] = id, ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }.ToJsonString(), ct).ConfigureAwait(false);

            var bucket = new TokenBucketLite(40, 120);
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var msg = await SignalingClient.ReceiveJsonAsync(ws, ct).ConfigureAwait(false);
                if (msg is null) break;
                if (!bucket.Take()) { TrySend(ws, """{"t":"error","code":"rate_limited"}"""); continue; }
                switch ((string?)msg["t"])
                {
                    case "ping":
                        TrySend(ws, $$"""{"t":"pong","time":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}""");
                        break;
                    case "presence":
                        var online = msg["ids"]?.AsArray().Select(x => (string?)x).Where(x => x == _host.DeviceId).ToArray() ?? [];
                        TrySend(ws, new JsonObject { ["t"] = "presence", ["online"] = new JsonArray(online.Select(x => (JsonNode)x!).ToArray()) }.ToJsonString());
                        break;
                    case "turn":
                        TrySend(ws, """{"t":"turn","iceServers":[],"ttl":0}""");
                        break;
                    case "relay":
                        var env = msg["env"].Deserialize<Envelope>(ProtocolJson.Options);
                        if (env is null || env.From != id || (string?)msg["to"] != env.To)
                        {
                            TrySend(ws, """{"t":"error","code":"bad_request"}""");
                            break;
                        }
                        if (env.To != _host.DeviceId)
                        {
                            TrySend(ws, """{"t":"error","code":"peer_offline"}""");
                            break;
                        }
                        var socket = ws;
                        EnvelopeReceived?.Invoke(env, e => TrySend(socket, Relay(e)));
                        break;
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or JsonException or InvalidOperationException) { }
        finally
        {
            if (id is not null) _clients.TryRemove(new KeyValuePair<string, WebSocket>(id, ws));
            ws.Dispose();
        }
    }

    private static string? Authenticate(JsonObject? hello, string nonce)
    {
        if (hello is null || (string?)hello["t"] != "hello") return null;
        var id = (string?)hello["id"];
        var role = (string?)hello["role"];
        if (!Crypto.IsDeviceId(id) || role is not ("client" or "host")) return null;
        try
        {
            var pk = Enc.FromB64u((string?)hello["edPk"] ?? "");
            var sig = Enc.FromB64u((string?)hello["sig"] ?? "");
            if (Crypto.DeviceIdOf(pk) != id) return null;
            return Crypto.EdVerify(pk, Enc.Utf8($"remotepc-signal-v1\n{nonce}\n{id}\n{role}"), sig) ? id : null;
        }
        catch (Exception e) when (e is FormatException or ArgumentException) { return null; }
    }

    /// <summary>Minimal RFC 6455 server handshake for GET /signal.</summary>
    private static async Task<bool> UpgradeAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var len = 0;
        while (true)
        {
            var n = await stream.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
            if (n == 0) return false;
            len += n;
            var text = Encoding.ASCII.GetString(buf, 0, len);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            if (len >= buf.Length) return false;
        }
        var lines = Encoding.ASCII.GetString(buf, 0, len).Split("\r\n");
        if (!lines[0].StartsWith("GET /signal ", StringComparison.Ordinal))
        {
            await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            return false;
        }
        var key = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim()).FirstOrDefault();
        if (key is null) return false;
        // RFC 6455 §4.2.2 mandates SHA-1 here; it is a protocol handshake checksum, not a security control.
#pragma warning disable CA5350
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
        var resp = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), ct).ConfigureAwait(false);
        return true;
    }

    private static Task SendAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private static bool TrySend(WebSocket ws, string text)
    {
        if (ws.State != WebSocketState.Open) return false;
        try
        {
            lock (ws) ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (var ws in _clients.Values) ws.Abort();
        if (_accept is not null) await _accept.ConfigureAwait(false);
        _cts.Dispose();
    }

    private sealed class TokenBucketLite
    {
        private readonly double _rate, _burst;
        private double _tokens;
        private DateTime _last = DateTime.UtcNow;
        public TokenBucketLite(double rate, double burst) { _rate = rate; _burst = burst; _tokens = burst; }
        public bool Take()
        {
            var now = DateTime.UtcNow;
            _tokens = Math.Min(_burst, _tokens + ((now - _last).TotalSeconds * _rate));
            _last = now;
            if (_tokens < 1) return false;
            _tokens--;
            return true;
        }
    }
}

/// <summary>UDP LAN discovery responder (PROTOCOL.md §6). Replies only to well-formed discover probes.</summary>
public sealed class LanDiscoveryResponder : IDisposable
{
    public const int Port = 45821;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<JsonObject> _announcement;

    public LanDiscoveryResponder(Func<JsonObject> announcement, int port = Port)
    {
        _announcement = announcement;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            if (r.Buffer.Length > 256 || !Net.NetworkInfo.IsPrivate(r.RemoteEndPoint.Address)) continue;
            try
            {
                var m = JsonNode.Parse(r.Buffer);
                if ((string?)m?["t"] != "discover" || (int?)m["v"] != 1) continue;
                var reply = Encoding.UTF8.GetBytes(_announcement().ToJsonString());
                await _udp.SendAsync(reply, r.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (JsonException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
