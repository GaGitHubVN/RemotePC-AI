// RemotePC.TestHost — CI-ONLY. Runs the real RemotePC.Core stack (pairing, envelope crypto, LAN signaling,
// WebRTC sessions, RPC, file transfer) with a replayed synthetic H.264 clip instead of desktop capture, so the
// mobile app can be exercised end-to-end on an iOS Simulator / Android emulator where no Windows PC exists.
// It AUTO-APPROVES pairing requests and must never be distributed or run on a real network.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RemotePC.Core.AI;
using RemotePC.Core.Files;
using RemotePC.Core.Input;
using RemotePC.Core.Media;
using RemotePC.Core.Protocol;
using RemotePC.Core.Security;
using RemotePC.Core.Session;
using RemotePC.Core.Signaling;

var port = args.Length > 0 && int.TryParse(args[0], out var p0) ? p0 : 45900;
var uriFile = args.Length > 1 ? args[1] : null;
var shotDir = args.Length > 2 ? args[2] : null;
var data = Path.Combine(Path.GetTempPath(), "remotepc-testhost-" + Environment.ProcessId);
Directory.CreateDirectory(data);
var protector = new EphemeralProtector();
var keys = IdentityStore.LoadOrCreate(Path.Combine(data, "identity.dat"), protector);
var devices = new DeviceStore(Path.Combine(data, "devices.dat"), protector);
var router = new PeerRouter(keys, devices, TimeProvider.System);
var pairing = new PairingManager(keys, devices, TimeProvider.System)
{
    ApprovalHandler = (req, _) =>
    {
        Log("auto-approving pairing request");
        return Task.FromResult(new PairingDecision(true, Permission.ViewScreen | Permission.ControlInput | Permission.UseAi | Permission.AiAgent |
            Permission.SendFiles | Permission.ReceiveFiles | Permission.Clipboard | Permission.ModifyFiles));
    },
};
var sessions = new SessionManager(router, devices, new ClipScreenSource(Path.Combine(AppContext.BaseDirectory, "fixtures", "pattern-720p.h264s")),
    new LoggingInput(), TimeProvider.System);
sessions.LogMessage += (c, m) => Log($"[{c}] {m}");

// Sandboxed sample files so the Files screen has content.
var docs = Directory.CreateDirectory(Path.Combine(data, "Documents")).FullName;
var recv = Directory.CreateDirectory(Path.Combine(data, "Received")).FullName;
File.WriteAllText(Path.Combine(docs, "Ghi chú.txt"), "Xin chào từ RemotePC TestHost.\n");
File.WriteAllBytes(Path.Combine(docs, "report.bin"), RandomNumberGenerator.GetBytes(300 * 1024));
Directory.CreateDirectory(Path.Combine(docs, "Projects"));
var sandbox = new FileSandbox([new SandboxRoot("Documents", docs, true), new SandboxRoot("Received", recv, true)]);
var files = new FileTransferService(sandbox, new DocumentExtractor(), null, (c, m, _) => Log($"[{c}] {m}"));
files.RegisterRpc(sessions.Rpc);
sessions.SessionCreated += files.Attach;
sessions.SessionClosed += s => files.AbortDevice(s.DeviceId);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
using var ai = new AiService(http, new AiConfig());
ai.RegisterRpc(sessions.Rpc);
_ = ai.ScanAsync(CancellationToken.None);

var clip = "";
sessions.Rpc.Register("sys.info", Permission.None, (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["hostName"] = "CI-TestHost", ["os"] = "test", ["remoteInput"] = true }));
sessions.Rpc.Register("screen.capture", Permission.ViewScreen, (_, _) =>
{
    var bgra = new byte[320 * 180 * 4];
    for (var i = 0; i < bgra.Length; i += 4) { bgra[i] = 200; bgra[i + 1] = 120; bgra[i + 2] = 60; bgra[i + 3] = 255; }
    return Task.FromResult<JsonNode?>(new JsonObject { ["png"] = Convert.ToBase64String(Png.FromBgra(bgra, 320, 180, 320 * 4)) });
});
sessions.Rpc.Register("clipboard.get", Permission.Clipboard, (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["text"] = clip }));
sessions.Rpc.Register("clipboard.set", Permission.Clipboard, (q, _) =>
{
    clip = (string?)q?["text"] ?? "";
    Log($"clipboard set: {clip}");
    return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
});
sessions.Rpc.Register("agent.list", Permission.AiAgent, (_, _) => Task.FromResult<JsonNode?>(new JsonArray()));
sessions.Rpc.Register("agent.templates", Permission.AiAgent, (_, _) => Task.FromResult<JsonNode?>(new JsonArray(
    new JsonObject { ["id"] = "screenshot", ["title"] = "Chụp màn hình", ["request"] = "Chụp màn hình máy tính và gửi cho tôi." })));

var lan = new LanSignalingServer(keys, port) { AllowNonPrivateClients = true };
lan.EnvelopeReceived += (env, reply) => _ = DispatchAsync(env, e => reply(e));
lan.Start();

var offer = pairing.CreateOffer(PairingMode.Qr);
var uri = pairing.BuildQrUri(offer, "CI-TestHost", null, [$"127.0.0.1:{lan.Port}"]);
if (uriFile is not null) File.WriteAllText(uriFile, uri);
Log($"listening on {lan.Port}");
Console.WriteLine("PAIR_URI=" + uri);

// Fresh single-use pairing offer on demand (offers expire after 5 minutes; CI builds take longer than that).
using var http2 = new System.Net.HttpListener();
http2.Prefixes.Add($"http://127.0.0.1:{lan.Port + 1}/");
http2.Start();
_ = Task.Run(async () =>
{
    while (http2.IsListening)
    {
        var ctx = await http2.GetContextAsync();
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (path.StartsWith("/shot/", StringComparison.Ordinal) && shotDir is not null)
        {
            // Synchronous native screenshot of every booted simulator (macOS CI only).
            var name = new string(path[6..].Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
            var report = await ShotAsync(shotDir, name);
            Log($"shot {name}: {report}");
            var rb = System.Text.Encoding.UTF8.GetBytes(report);
            await ctx.Response.OutputStream.WriteAsync(rb);
            ctx.Response.Close();
            continue;
        }
        var o = pairing.CreateOffer(PairingMode.Qr);
        var body = System.Text.Encoding.UTF8.GetBytes(pairing.BuildQrUri(o, "CI-TestHost", null, [$"127.0.0.1:{lan.Port}"]));
        Log("issued a fresh pairing offer over HTTP");
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    }
});

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
await sessions.CloseAllAsync("host_exit");
await lan.DisposeAsync();
return 0;

async Task DispatchAsync(Envelope env, EnvelopeSender reply)
{
    var msg = router.Open(env);
    if (msg is null) return;
    try
    {
        if (msg.Typ == "pair.request")
        {
            var result = await pairing.HandleRequestAsync(env, msg.Payload, CancellationToken.None);
            Log($"pairing result ok={(bool?)result["ok"]}");
            reply(router.SealForPairing(env, "pair.result", result));
            return;
        }
        Log($"<- {msg.Typ}");
        await sessions.HandleAsync(msg, reply);
    }
    catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
    {
        Log($"error {msg.Typ}: {e.Message}");
    }
}

static void Log(string m) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {m}");

static async Task<string> Run(string file, string arguments)
{
    using var p = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
    var output = await p.StandardOutput.ReadToEndAsync();
    await p.WaitForExitAsync();
    return output;
}

static async Task<string> ShotAsync(string dir, string name)
{
    Directory.CreateDirectory(dir);
    if (!OperatingSystem.IsMacOS())
    {
        // Android emulator (Linux CI): adb screencap.
        var file = Path.Combine(dir, $"{name}-android.png");
        using var p = Process.Start(new ProcessStartInfo("adb", "exec-out screencap -p") { RedirectStandardOutput = true })!;
        await using (var fs = File.Create(file)) await p.StandardOutput.BaseStream.CopyToAsync(fs);
        await p.WaitForExitAsync();
        return $"android {new FileInfo(file).Length} bytes";
    }
    var json = JsonNode.Parse(await Run("xcrun", "simctl list devices booted -j"));
    var parts = new List<string>();
    foreach (var (runtime, devs) in json?["devices"]?.AsObject() ?? [])
    {
        foreach (var d in devs?.AsArray() ?? [])
        {
            var udid = (string?)d?["udid"];
            if (udid is null) continue;
            var rt = runtime.Split('.').Last();
            var apps = await Run("xcrun", $"simctl listapps {udid}");
            var hasApp = apps.Contains("ai.remotepc.app", StringComparison.Ordinal);
            var file = Path.Combine(dir, $"{name}-{rt}-{udid[..8]}.png");
            await Run("xcrun", $"simctl io {udid} screenshot \"{file}\"");
            parts.Add($"{d?["name"]} {rt} {udid[..8]} app={(hasApp ? "installed" : "absent")}");
        }
    }
    return string.Join("; ", parts);
}

/// <summary>Process-lifetime key; data is thrown away with the temp folder. CI only.</summary>
sealed class EphemeralProtector : ISecretProtector
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public byte[] Protect(byte[] plaintext, string purpose)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length + 16];
        using var g = new AesGcm(_key, 16);
        g.Encrypt(nonce, plaintext, ct.AsSpan(0, plaintext.Length), ct.AsSpan(plaintext.Length), System.Text.Encoding.UTF8.GetBytes(purpose));
        return [.. nonce, .. ct];
    }

    public byte[] Unprotect(byte[] data, string purpose)
    {
        var pt = new byte[data.Length - 28];
        using var g = new AesGcm(_key, 16);
        g.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, pt.Length), data.AsSpan(12 + pt.Length), pt, System.Text.Encoding.UTF8.GetBytes(purpose));
        return pt;
    }
}

/// <summary>Logs input so CI can assert that taps/keys from the phone arrived.</summary>
sealed class LoggingInput : IInputSink
{
    private static bool L(string m)
    {
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} INPUT {m}");
        return true;
    }

    public bool Move(double nx, double ny, PixelRect monitor) => true;
    public bool Button(string button, bool down, double nx, double ny, PixelRect monitor) => L($"button {button} {(down ? "down" : "up")} {nx:0.00},{ny:0.00}");
    public bool Wheel(int dx, int dy) => L($"wheel {dx},{dy}");
    public bool Key(ushort vk, bool down) => L($"key {vk} {(down ? "down" : "up")}");
    public bool Combo(IReadOnlyList<ushort> vks) => L($"combo {string.Join('+', vks)}");
    public bool Text(string text) => L($"text {text}");
    public void ReleaseAll() { }
}

/// <summary>Replays the pre-encoded clip at 30 fps; key-frame requests restart from the first IDR.</summary>
sealed class ClipScreenSource(string path) : IScreenSource
{
    private readonly List<(byte[] Au, bool Key)> _frames = Load(path);

    private static List<(byte[] Au, bool Key)> Load(string path)
    {
        var list = new List<(byte[], bool)>();
        using var br = new BinaryReader(File.OpenRead(path));
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            var len = br.ReadInt32();
            var key = br.ReadByte() == 1;
            list.Add((br.ReadBytes(len), key));
        }
        return list;
    }

    public IReadOnlyList<DisplayDesc> Displays() => [new DisplayDesc(0, "CLIP", new PixelRect(0, 0, 1280, 720), true, 0)];
    public IScreenPipeline Start(int displayIndex, StreamSettings settings, Action<EncodedFrame> onFrame) => new Pipeline(_frames, onFrame);
    public byte[] CapturePng(int displayIndex, int maxWidth = 1920) => Png.FromBgra(new byte[64 * 36 * 4], 64, 36, 64 * 4);

    private sealed class Pipeline : IScreenPipeline
    {
        private readonly Timer _timer;
        private int _i;
        private long _sent;
        private volatile bool _wantKey = true;
        public int DisplayIndex => 0;
        public StreamStats Stats => new(30, 1500, 1280, 720, "clip", false, 0, 0, 0, Interlocked.Read(ref _sent), 0);

        public Pipeline(List<(byte[] Au, bool Key)> frames, Action<EncodedFrame> onFrame)
        {
            _timer = new Timer(_ =>
            {
                if (_wantKey)
                {
                    _i = frames.FindIndex(f => f.Key);
                    _wantKey = false;
                }
                var (au, key) = frames[_i];
                _i = (_i + 1) % frames.Count;
                Interlocked.Increment(ref _sent);
                var now = Stopwatch.GetTimestamp();
                onFrame(new EncodedFrame(au, key, now, now, now, 1280, 720));
            }, null, 0, 33);
        }

        public void RequestKeyFrame() => _wantKey = true;
        public void SetBitrate(int kbps) { }
        public void SetFps(int fps) { }
        public void Dispose() => _timer.Dispose();
    }
}
