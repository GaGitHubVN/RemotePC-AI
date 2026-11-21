using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemotePC.Core.Agent;
using RemotePC.Core.AI;
using RemotePC.Core.Files;
using RemotePC.Core.Logging;
using RemotePC.Core.Net;
using RemotePC.Core.Protocol;
using RemotePC.Core.Security;
using RemotePC.Core.Session;
using RemotePC.Core.Signaling;
using RemotePC.Platform.Windows.Agent;
using RemotePC.Platform.Windows.Capture;
using RemotePC.Platform.Windows.Input;
using RemotePC.Platform.Windows.Security;
using SIPSorcery.Net;

namespace RemotePC.Host.Services;

/// <summary>Composition root of the Windows Host. UI-independent; the WPF layer observes it.</summary>
public sealed class HostRuntime : IAsyncDisposable
{
    public string DataDir { get; }
    public HostConfig Config { get; private set; }
    public DeviceKeys Keys { get; }
    public DeviceStore Devices { get; }
    public ActivityLog Log { get; }
    public PairingManager Pairing { get; }
    public PeerRouter Router { get; }
    public SessionManager Sessions { get; }
    public AiService Ai { get; }
    public AgentEngine Agent { get; }
    public FileTransferService Files { get; }
    public FileSandbox Sandbox { get; }
    public DesktopScreenSource Screen { get; }
    public SignalingClient? Internet { get; private set; }
    public LanSignalingServer? Lan { get; private set; }
    private LanDiscoveryResponder? _discovery;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly WindowsClipboard _clipboard = new();
    private readonly string _configPath;

    public event Action? StateChanged;

    public HostRuntime()
    {
        DataDir = DataPaths.Resolve();
        _configPath = Path.Combine(DataDir, "config.json");
        Config = HostConfig.Load(_configPath);
        Log = new ActivityLog(Path.Combine(DataDir, "logs")) { RetentionDays = Config.LogRetentionDays };
        var protector = new DpapiProtector();
        try { Keys = IdentityStore.LoadOrCreate(Path.Combine(DataDir, "identity.dat"), protector); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Folder copied from another Windows account/PC: DPAPI cannot decrypt → new identity, phones must re-pair.
            File.Move(Path.Combine(DataDir, "identity.dat"), Path.Combine(DataDir, $"identity.unreadable-{DateTime.Now:yyyyMMddHHmmss}.dat"));
            if (File.Exists(Path.Combine(DataDir, "devices.dat")))
                File.Move(Path.Combine(DataDir, "devices.dat"), Path.Combine(DataDir, $"devices.unreadable-{DateTime.Now:yyyyMMddHHmmss}.dat"), true);
            Keys = IdentityStore.LoadOrCreate(Path.Combine(DataDir, "identity.dat"), protector);
            Log.Write("security", "Stored identity could not be decrypted for this Windows user; a new identity was created. Paired phones must pair again.");
        }
        Devices = new DeviceStore(Path.Combine(DataDir, "devices.dat"), protector);
        Router = new PeerRouter(Keys, Devices, TimeProvider.System);
        Router.Rejected += (from, reason) => { if (reason is not RejectReason.Stale) Log.Write("security", $"rejected envelope from {from}: {reason}"); };
        Pairing = new PairingManager(Keys, Devices, TimeProvider.System);

        Screen = new DesktopScreenSource(m => Log.Write("stream", m));
        var input = new GatedInput(new WindowsInputSink(), () => Config.RemoteInputEnabled);
        Sessions = new SessionManager(Router, Devices, Screen, input, TimeProvider.System);
        Sessions.LogMessage += (c, m) => Log.Write(c, m);
        Sessions.Changed += () => StateChanged?.Invoke();
        Sessions.IceServerProvider = IceServersAsync;

        var received = Config.ReceiveFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "RemotePC-AI");
        Directory.CreateDirectory(received);
        var roots = FileSandbox.DefaultRoots(received).ToList();
        roots.AddRange(Config.ExtraRoots.Where(Directory.Exists).Select(r => new SandboxRoot(Path.GetFileName(r.TrimEnd('\\')), r, true)));
        Sandbox = new FileSandbox(roots);
        var extractor = new DocumentExtractor();
        Files = new FileTransferService(Sandbox, extractor, SystemMetrics.Recycle, (c, m, d) => Log.Write(c, m, d));
        Files.RegisterRpc(Sessions.Rpc);
        Sessions.SessionCreated += Files.Attach;
        Sessions.SessionClosed += s => { Files.AbortDevice(s.DeviceId); Agent!.CancelAll(s.DeviceId); };

        Ai = new AiService(_http, Config.Ai) { Persist = ai => UpdateConfig(c => c with { Ai = ai }), Secrets = protector };
        Ai.Changed += () => StateChanged?.Invoke();
        Ai.RegisterRpc(Sessions.Rpc);

        var tools = new ToolRegistry();
        FileTools.Register(tools, Sandbox, extractor, SystemMetrics.Recycle);
        new WindowsAgentTools(Screen, Sandbox).Register(tools);
        Agent = new AgentEngine(Ai, tools, Devices.Get, id => Config.AgentPolicies.GetValueOrDefault(id) ?? new AgentPolicy(), (c, m, d) => Log.Write(c, m, d));
        Agent.TaskEvent += ForwardAgentEvent;
        RegisterHostRpc();

        Devices.Changed += () => StateChanged?.Invoke();
    }

    public void UpdateConfig(Func<HostConfig, HostConfig> change)
    {
        Config = change(Config);
        Config.Save(_configPath);
        StateChanged?.Invoke();
    }

    public async Task StartAsync()
    {
        _ = Ai.ScanAsync(CancellationToken.None).ContinueWith(_ => Ai.RefreshModelsAsync(CancellationToken.None), TaskScheduler.Default);
        if (Config.LanEnabled)
        {
            try
            {
                Lan = new LanSignalingServer(Keys, Config.LanPort);
                Lan.EnvelopeReceived += (env, reply) => _ = DispatchAsync(env, e => reply(e));
                Lan.Start();
                _discovery = new LanDiscoveryResponder(() => new JsonObject
                {
                    ["t"] = "host", ["v"] = 1, ["id"] = Keys.DeviceId, ["name"] = Environment.MachineName,
                    ["edPk"] = Enc.B64u(Keys.EdPk), ["xPk"] = Enc.B64u(Keys.XPk), ["ws"] = Lan.Port,
                });
                Log.Write("network", $"LAN endpoint listening on port {Lan.Port}");
            }
            catch (System.Net.Sockets.SocketException e) { Log.Write("network", $"LAN endpoint unavailable: {e.Message}"); }
        }
        await ConnectInternetAsync().ConfigureAwait(false);
        Log.Write("app", $"RemotePC AI host started (data: {DataDir})");
    }

    public async Task ConnectInternetAsync()
    {
        if (Internet is not null) { await Internet.DisposeAsync().ConfigureAwait(false); Internet = null; }
        if (string.IsNullOrWhiteSpace(Config.SignalingUrl)) return;
        try
        {
            Internet = new SignalingClient(new Uri(Config.SignalingUrl), Keys, "host");
            Internet.EnvelopeReceived += env => _ = DispatchAsync(env, e => Internet?.Send(e) ?? false);
            Internet.StateChanged += s => { Log.Write("network", $"signaling {s}"); StateChanged?.Invoke(); };
            Internet.Start();
        }
        catch (ArgumentException e) { Log.Write("network", "invalid signaling URL: " + e.Message); }
    }

    /// <summary>Single entry point for envelopes from either transport.</summary>
    private async Task DispatchAsync(Envelope env, EnvelopeSender reply)
    {
        var msg = Router.Open(env);
        if (msg is null) return;
        try
        {
            if (msg.Typ == "pair.request")
            {
                Log.Write("pairing", $"pairing request from {msg.From}");
                var result = await Pairing.HandleRequestAsync(env, msg.Payload, CancellationToken.None).ConfigureAwait(false);
                Log.Write("pairing", (bool?)result["ok"] == true ? $"device {msg.From} paired" : $"pairing failed: {result["reason"]}", msg.From);
                reply(Router.SealForPairing(env, "pair.result", result));
                return;
            }
            await Sessions.HandleAsync(msg, reply).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            Log.Write("error", $"{msg.Typ}: {e.Message}");
        }
    }

    private async Task<List<RTCIceServer>> IceServersAsync(CancellationToken ct)
    {
        var list = new List<RTCIceServer>();
        var turn = Internet is { State: SignalingState.Online } ? await Internet.RequestTurnAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false) : null;
        foreach (var s in turn?["iceServers"]?.AsArray() ?? [])
        {
            var urls = s?["urls"] switch { JsonArray a => string.Join(',', a.Select(u => (string?)u)), JsonValue v => (string?)v, _ => null };
            if (urls is null) continue;
            list.Add(new RTCIceServer { urls = urls, username = (string?)s!["username"], credential = (string?)s["credential"], credentialType = RTCIceCredentialType.password });
        }
        if (list.Count == 0) list.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
        return list;
    }

    private void ForwardAgentEvent(AgentTask t, string ev, JsonObject payload)
    {
        payload["taskId"] = t.Id;
        foreach (var s in Sessions.Sessions.Where(s => s.DeviceId == t.DeviceId)) s.SendEvent(ev, payload);
        StateChanged?.Invoke();
    }

    private void RegisterHostRpc()
    {
        var rpc = Sessions.Rpc;
        rpc.Register("sys.info", Permission.None, (_, ctx) => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["hostName"] = Environment.MachineName,
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["version"] = typeof(HostRuntime).Assembly.GetName().Version?.ToString(),
            ["permissions"] = new JsonArray(PermissionNames.ToNames(ctx.Device.Permissions).Select(n => (JsonNode)n).ToArray()),
            ["remoteInput"] = Config.RemoteInputEnabled,
        }));
        rpc.Register("sys.metrics", Permission.None, (_, _) =>
        {
            var (total, avail) = SystemMetrics.Memory();
            return Task.FromResult<JsonNode?>(new JsonObject { ["cpu"] = Math.Round(SystemMetrics.SampleCpuPercent(TimeSpan.FromMilliseconds(250))), ["ramUsedGb"] = Math.Round((total - avail) / 1073741824.0, 1), ["ramTotalGb"] = Math.Round(total / 1073741824.0, 1) });
        });
        rpc.Register("screen.capture", Permission.ViewScreen, (p, ctx) =>
        {
            var png = Screen.CapturePng((int?)p?["display"] ?? ctx.Session.DisplayIndex, Math.Clamp((int?)p?["maxWidth"] ?? 1920, 320, 3840));
            Log.Write("screen", "screenshot sent to phone", ctx.Device.Id);
            return Task.FromResult<JsonNode?>(new JsonObject { ["png"] = Convert.ToBase64String(png) });
        });
        rpc.Register("clipboard.get", Permission.Clipboard, (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["text"] = _clipboard.GetText() }));
        rpc.Register("clipboard.set", Permission.Clipboard, (p, ctx) =>
        {
            var text = (string?)p?["text"] ?? "";
            var ok = _clipboard.SetText(text);
            Log.Write("clipboard", $"phone set clipboard ({text.Length} chars)", ctx.Device.Id);
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = ok });
        });

        rpc.Register("agent.run", Permission.AiAgent, (p, ctx) =>
        {
            try { return Task.FromResult<JsonNode?>(Agent.Start(ctx.Device, (string?)p?["request"] ?? "").ToJson()); }
            catch (ArgumentException e) { throw new RpcException("bad_request", e.Message); }
        });
        rpc.Register("agent.list", Permission.AiAgent, (_, ctx) => Task.FromResult<JsonNode?>(new JsonArray(Agent.Tasks.Where(t => t.DeviceId == ctx.Device.Id).Select(t => (JsonNode)t.ToJson()).ToArray())));
        rpc.Register("agent.status", Permission.AiAgent, (p, ctx) => Task.FromResult<JsonNode?>(Own(p, ctx).ToJson()));
        rpc.Register("agent.cancel", Permission.None, (p, ctx) => Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = Agent.Cancel((string?)p?["taskId"] ?? "", ctx.Device.Id) }));
        rpc.Register("agent.pause", Permission.AiAgent, (p, ctx) => Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = Agent.Pause((string?)p?["taskId"] ?? "", ctx.Device.Id, (bool?)p?["pause"] ?? true) }));
        rpc.Register("agent.approve", Permission.AiAgent, (p, ctx) => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["ok"] = Agent.Approve((string?)p?["taskId"] ?? "", ctx.Device.Id, (string?)p?["approvalId"] ?? "", (bool?)p?["approved"] ?? false, (string?)p?["plan"]),
        }));
        rpc.Register("agent.image", Permission.AiAgent, (p, ctx) =>
        {
            var t = Own(p, ctx);
            return Task.FromResult<JsonNode?>(t.Images.TryGetValue((int?)p?["step"] ?? -1, out var png) ? new JsonObject { ["png"] = Convert.ToBase64String(png) } : throw new RpcException("not_found", "no image"));
        });
        rpc.Register("agent.templates", Permission.AiAgent, (_, _) => Task.FromResult<JsonNode?>(new JsonArray(
            Templates.Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["title"] = t.Title, ["request"] = t.Request }).ToArray())));
    }

    public static readonly (string Id, string Title, string Request)[] Templates =
    [
        ("screenshot", "Chụp màn hình", "Chụp màn hình máy tính và gửi cho tôi."),
        ("resources", "Kiểm tra tài nguyên", "Cho tôi biết mức sử dụng CPU, RAM và dung lượng ổ đĩa hiện tại."),
        ("read-doc", "Đọc tài liệu", "Đọc file tôi chỉ định trong Documents và tóm tắt nội dung chính."),
        ("create-file", "Tạo file", "Tạo một file ghi chú mới trên Desktop với nội dung tôi cung cấp."),
        ("summarize-folder", "Tóm tắt thư mục", "Liệt kê và tóm tắt các file trong thư mục Downloads."),
        ("app-status", "Trạng thái ứng dụng", "Liệt kê các cửa sổ đang mở và cho biết ứng dụng nào đang được dùng."),
    ];

    private AgentTask Own(JsonNode? p, RpcContext ctx)
    {
        var t = Agent.Get((string?)p?["taskId"] ?? "");
        return t is not null && t.DeviceId == ctx.Device.Id ? t : throw new RpcException("not_found", "task not found");
    }

    /// <summary>Emergency disconnect: end every session and cancel every agent task immediately.</summary>
    public async Task EmergencyDisconnectAsync()
    {
        Agent.CancelAll();
        await Sessions.CloseAllAsync("emergency").ConfigureAwait(false);
        Pairing.CancelOffer();
        Log.Write("security", "EMERGENCY DISCONNECT by PC owner");
    }

    public async Task RevokeAllAsync()
    {
        Agent.CancelAll();
        await Sessions.CloseAllAsync("revoked").ConfigureAwait(false);
        Devices.RevokeAll();
        Log.Write("security", "all devices revoked by PC owner");
    }

    public string QrUri(PairingOffer offer)
    {
        var lan = Lan is null ? [] : NetworkInfo.LanAddresses().Select(a => $"{a}:{Lan.Port}");
        return Pairing.BuildQrUri(offer, Environment.MachineName, Config.SignalingUrl, lan);
    }

    public async ValueTask DisposeAsync()
    {
        Agent.CancelAll();
        await Sessions.CloseAllAsync("host_exit").ConfigureAwait(false);
        if (Internet is not null) await Internet.DisposeAsync().ConfigureAwait(false);
        if (Lan is not null) await Lan.DisposeAsync().ConfigureAwait(false);
        _discovery?.Dispose();
        Ai.Dispose();
        _http.Dispose();
        Keys.Dispose();
    }

    /// <summary>Input sink that honours the global "remote input enabled" switch.</summary>
    private sealed class GatedInput(IInputSink inner, Func<bool> enabled) : IInputSink
    {
        public bool Move(double nx, double ny, Core.Input.PixelRect m) => enabled() && inner.Move(nx, ny, m);
        public bool Button(string b, bool d, double nx, double ny, Core.Input.PixelRect m) => enabled() && inner.Button(b, d, nx, ny, m);
        public bool Wheel(int dx, int dy) => enabled() && inner.Wheel(dx, dy);
        public bool Key(ushort vk, bool down) => enabled() && inner.Key(vk, down);
        public bool Combo(IReadOnlyList<ushort> vks) => enabled() && inner.Combo(vks);
        public bool Text(string text) => enabled() && inner.Text(text);
        public void ReleaseAll() => inner.ReleaseAll();
    }

    internal static string Pretty(JsonNode n) => n.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
