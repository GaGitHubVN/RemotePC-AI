using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RemotePC.Core.Files;
using RemotePC.Core.Protocol;
using RemotePC.Core.Security;
using RemotePC.Core.Session;
using Xunit;

namespace RemotePC.Core.Tests;

[Trait("Category", "Integration")]
public sealed class FileTransferE2ETests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly DeviceKeys _host = DeviceKeys.Generate();
    private readonly DeviceKeys _phoneKeys = DeviceKeys.Generate();
    private DeviceStore _store = null!;
    private SessionManager _mgr = null!;
    private TestPhone _phone = null!;
    private string _recv = null!;

    public async Task InitializeAsync()
    {
        _recv = Path.Combine(_dir.Path, "Received");
        Directory.CreateDirectory(_recv);
        _store = new DeviceStore(_dir.File("d.dat"), new TestProtector());
        _store.Upsert(new PairedDevice
        {
            Id = _phoneKeys.DeviceId, Name = "Phone", Platform = "t", EdPk = Enc.B64u(_phoneKeys.EdPk), XPk = Enc.B64u(_phoneKeys.XPk),
            Permissions = Permission.SendFiles | Permission.ReceiveFiles | Permission.ModifyFiles,
        });
        var router = new PeerRouter(_host, _store, TimeProvider.System);
        _mgr = new SessionManager(router, _store, new FakeScreenSource(), new RecordingInput(), TimeProvider.System)
        {
            IceServerProvider = _ => Task.FromResult(new List<SIPSorcery.Net.RTCIceServer>()),
        };
        var files = new FileTransferService(new FileSandbox([new SandboxRoot("Received", _recv, true)]), new DocumentExtractor(), null, (_, _, _) => { });
        files.RegisterRpc(_mgr.Rpc);
        _mgr.SessionCreated += files.Attach;
        _phone = new TestPhone(_phoneKeys, _host, async env =>
        {
            var m = router.Open(env);
            if (m is not null) await _mgr.HandleAsync(m, e => { _phone.Receive(e); return true; });
        });
        await _phone.OpenSessionAsync();
        await _phone.ConnectWebRtcAsync();
        await _phone.Connected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await _phone.WaitChannelsOpenAsync();
    }

    public async Task DisposeAsync()
    {
        await _phone.DisposeAsync();
        await _mgr.CloseAllAsync("end");
        _dir.Dispose();
    }

    private async Task<JsonObject> UploadAsync(string name, byte[] data, byte[]? claimedSha = null, int stopAfter = int.MaxValue, long startOffset = -1)
    {
        var begin = await _phone.CallAsync("file.begin", new JsonObject { ["name"] = name, ["size"] = data.Length, ["sha256"] = Convert.ToHexString(claimedSha ?? SHA256.HashData(data)) });
        Assert.Null(begin["e"]);
        var id = (string)begin["r"]!["id"]!;
        var offset = startOffset >= 0 ? startOffset : (long)begin["r"]!["offset"]!;
        var sent = 0;
        while (offset < data.Length && sent < stopAfter)
        {
            var n = (int)Math.Min(FileTransferService.MaxChunk, data.Length - offset);
            while (_phone.FileBuffered > HostSession.MaxFileBuffered) await Task.Delay(2);
            _phone.SendFileFrame(FileTransferService.Frame(id, offset, data.AsSpan((int)offset, n)));
            offset += n;
            sent++;
        }
        // Wait until the host has written everything we sent.
        for (var i = 0; i < 200; i++)
        {
            var st = await _phone.CallAsync("file.status", new JsonObject { ["id"] = id });
            if ((long)st["r"]!["offset"]! >= offset) break;
            await Task.Delay(25);
        }
        return new JsonObject { ["id"] = id, ["offset"] = offset };
    }

    [Fact]
    public async Task Upload_Then_Download_Round_Trips_With_Checksums()
    {
        var data = RandomNumberGenerator.GetBytes(1_500_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var up = await UploadAsync("ảnh chụp.bin", data);
        var pingSw = System.Diagnostics.Stopwatch.StartNew();
        await _phone.CallAsync("ping");
        Console.WriteLine($"upload throughput: {data.Length / 1024.0 / sw.Elapsed.TotalSeconds:F0} KiB/s over loopback; ctl ping after upload {pingSw.ElapsedMilliseconds} ms");
        var end = await _phone.CallAsync("file.end", new JsonObject { ["id"] = up["id"]!.DeepClone() });
        Assert.Null(end["e"]);
        Assert.Equal("Received/ảnh chụp.bin", (string?)end["r"]!["path"]);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_recv, "ảnh chụp.bin")));
        Assert.Empty(Directory.GetFiles(_recv, "*.rpcpart"));

        // Same name again → never overwrites.
        var up2 = await UploadAsync("ảnh chụp.bin", [1, 2, 3]);
        var end2 = await _phone.CallAsync("file.end", new JsonObject { ["id"] = up2["id"]!.DeepClone() });
        Assert.Equal("Received/ảnh chụp (2).bin", (string?)end2["r"]!["path"]);

        // Download it back.
        var dl = await _phone.CallAsync("file.download", new JsonObject { ["path"] = "Received/ảnh chụp.bin" });
        var id = Convert.FromHexString((string)dl["r"]!["id"]!);
        var expectedSha = (string)dl["r"]!["sha256"]!;
        var buf = new byte[data.Length];
        long received = 0;
        for (var i = 0; i < 400 && received < data.Length; i++)
        {
            while (_phone.FileFrames.TryDequeue(out var f))
            {
                Assert.Equal(id, f[..16]);
                var off = BinaryPrimitives.ReadInt64BigEndian(f.AsSpan(16));
                Buffer.BlockCopy(f, 24, buf, (int)off, f.Length - 24);
                received += f.Length - 24;
            }
            await Task.Delay(25);
        }
        Assert.Equal(data.Length, received);
        Assert.Equal(expectedSha, Convert.ToHexString(SHA256.HashData(buf)).ToLowerInvariant());
        Assert.Equal(data, buf);
    }

    [Fact]
    public async Task Checksum_Mismatch_Discards_The_File()
    {
        var data = RandomNumberGenerator.GetBytes(100_000);
        var up = await UploadAsync("x.bin", data, claimedSha: SHA256.HashData([9]));
        var end = await _phone.CallAsync("file.end", new JsonObject { ["id"] = up["id"]!.DeepClone() });
        Assert.Equal("checksum_mismatch", (string?)end["e"]!["code"]);
        Assert.Empty(Directory.GetFiles(_recv));
    }

    [Fact]
    public async Task Interrupted_Upload_Resumes_From_Host_Offset()
    {
        var data = RandomNumberGenerator.GetBytes(400_000);
        var first = await UploadAsync("big.bin", data, stopAfter: 3);
        Assert.Equal(3 * FileTransferService.MaxChunk, (long)first["offset"]!);
        var resumed = await UploadAsync("big.bin", data); // file.begin returns the host's offset
        Assert.Equal(first["id"]!.ToString(), resumed["id"]!.ToString());
        var end = await _phone.CallAsync("file.end", new JsonObject { ["id"] = resumed["id"]!.DeepClone() });
        Assert.Null(end["e"]);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_recv, "big.bin")));
    }

    [Fact]
    public async Task Rejects_Traversal_Names_And_Requires_Delete_Confirmation()
    {
        var bad = await _phone.CallAsync("file.begin", new JsonObject { ["name"] = "..\\..\\evil.exe", ["size"] = 1, ["sha256"] = new string('0', 64) });
        Assert.NotNull(bad["e"]);
        var bad2 = await _phone.CallAsync("fs.list", new JsonObject { ["path"] = "Received/../../" });
        Assert.NotNull(bad2["e"]);

        File.WriteAllText(Path.Combine(_recv, "del.txt"), "x");
        var noConfirm = await _phone.CallAsync("fs.delete", new JsonObject { ["paths"] = new JsonArray("Received/del.txt") });
        Assert.Equal("confirm_required", (string?)noConfirm["e"]!["code"]);
        Assert.True(File.Exists(Path.Combine(_recv, "del.txt")));
        var ok = await _phone.CallAsync("fs.delete", new JsonObject { ["paths"] = new JsonArray("Received/del.txt"), ["confirmed"] = true });
        Assert.Single(ok["r"]!["deleted"]!.AsArray());
        Assert.False(File.Exists(Path.Combine(_recv, "del.txt")));

        var list = await _phone.CallAsync("fs.list", new JsonObject { ["path"] = "Received" });
        Assert.NotNull(list["r"]!["items"]);
    }

    [Fact]
    public async Task Permissions_Are_Enforced_On_The_Host()
    {
        _store.Update(_phoneKeys.DeviceId, d => d with { Permissions = Permission.ViewScreen });
        var r = await _phone.CallAsync("file.begin", new JsonObject { ["name"] = "a.txt", ["size"] = 1, ["sha256"] = new string('0', 64) });
        Assert.Equal("forbidden", (string?)r["e"]!["code"]);
        var d = await _phone.CallAsync("fs.list", new JsonObject { ["path"] = "Received" });
        Assert.Equal("forbidden", (string?)d["e"]!["code"]);
    }
}
