using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RemotePC.Core.Agent;
using RemotePC.Core.AI;
using RemotePC.Core.Files;
using RemotePC.Core.Protocol;
using RemotePC.Core.Security;
using Xunit;

namespace RemotePC.Core.Tests;

/// <summary>TEST-ONLY provider returning a fixed script of responses (one per ChatAsync call).</summary>
internal sealed class ScriptedProvider(params Func<ChatRequest, ChatChunk[]>[] script) : IAiProvider
{
    private int _i;
    public List<ChatRequest> Requests { get; } = [];
    public string Id => "scripted";
    public string Kind => "test";
    public string Name => "Scripted";
    public Uri Endpoint => new("http://127.0.0.1:1");
    public Task<AiProviderStatus> ProbeAsync(CancellationToken ct) => Task.FromResult(new AiProviderStatus(Id, Kind, Name, "x", true, "1", null, true, true, []));
    public Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AiModel>>([new AiModel(Id, "m", "m", 4096, Tools: true, Vision: false)]);
    public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest r, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(r);
        var step = _i < script.Length ? script[_i++] : _ => [new ChatChunk("done", Done: true)];
        foreach (var c in step(r)) { await Task.Yield(); yield return c; }
    }

    public static ChatChunk[] Text(string t) => [new ChatChunk(t), new ChatChunk(Done: true)];
    public static ChatChunk[] Call(string name, JsonObject args) => [new ChatChunk(ToolCalls: [new ToolCall("c" + Guid.NewGuid().ToString("N")[..4], name, args)], Done: true)];
}

public sealed class AgentTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FileSandbox _sandbox;
    private readonly ToolRegistry _tools = new();
    private readonly DeviceStore _store;
    private readonly DeviceKeys _phone = DeviceKeys.Generate();
    private readonly List<string> _audit = [];
    private AgentPolicy _policy = new();

    public AgentTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir.Path, "desk"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "ro"));
        _sandbox = new FileSandbox([new SandboxRoot("Desktop", Path.Combine(_dir.Path, "desk"), true), new SandboxRoot("Docs", Path.Combine(_dir.Path, "ro"), false)]);
        FileTools.Register(_tools, _sandbox, new DocumentExtractor(), null);
        _store = new DeviceStore(_dir.File("d.dat"), new TestProtector());
        _store.Upsert(new PairedDevice { Id = _phone.DeviceId, Name = "p", Platform = "t", EdPk = Enc.B64u(_phone.EdPk), XPk = Enc.B64u(_phone.XPk), Permissions = Permission.UseAi | Permission.AiAgent });
    }

    public void Dispose() { _dir.Dispose(); _phone.Dispose(); }

    private async Task<(AgentEngine Engine, ScriptedProvider Provider)> Engine(params Func<ChatRequest, ChatChunk[]>[] script)
    {
        var provider = new ScriptedProvider(script);
        var ai = new AiService(new HttpClient(), new AiConfig());
        await ai.ScanAsync(default, [provider]);
        await ai.RefreshModelsAsync(default);
        ai.Select("scripted", "m");
        var engine = new AgentEngine(ai, _tools, _store.Get, _ => _policy, (c, m, d) => { lock (_audit) _audit.Add($"{c}: {m}"); });
        return (engine, provider);
    }

    private static async Task<AgentTask> WaitFor(AgentTask t, Func<AgentTask, bool> cond, int ms = 5000)
    {
        for (var i = 0; i < ms / 20 && !cond(t); i++) await Task.Delay(20);
        Assert.True(cond(t), $"condition not met; state={t.State} error={t.Error}");
        return t;
    }

    private static bool Terminal(AgentTask t) => t.State is AgentTaskState.Completed or AgentTaskState.Failed or AgentTaskState.Cancelled;

    [Fact]
    public async Task Creates_Folder_After_Approval_Verifies_And_Completes()
    {
        var (engine, provider) = await Engine(
            _ => ScriptedProvider.Text("1. Create the folder\n2. Verify"),
            _ => ScriptedProvider.Call("file_create_folder", new JsonObject { ["path"] = "Desktop/Homework" }),
            _ => ScriptedProvider.Text("Đã tạo thư mục Homework trên Desktop."));
        var approvals = new List<ApprovalRequest>();
        engine.TaskEvent += (t, ev, p) => { if (ev == "agent.approval") lock (approvals) approvals.Add(t.Approval!); };
        var task = engine.Start(_store.Get(_phone.DeviceId)!, "Tạo một thư mục tên Homework trên Desktop");

        await WaitFor(task, t => t.State == AgentTaskState.WaitingForApproval);
        var req = task.Approval!;
        Assert.Equal(ToolRisk.Write, req.Risk);
        Assert.Contains("Create folder Desktop/Homework", req.Title, StringComparison.Ordinal);
        Assert.False(engine.Approve(task.Id, "someone-else", req.ApprovalId, true)); // other devices cannot approve
        Assert.True(engine.Approve(task.Id, _phone.DeviceId, req.ApprovalId, true));

        await WaitFor(task, Terminal);
        Assert.Equal(AgentTaskState.Completed, task.State);
        Assert.True(Directory.Exists(Path.Combine(_dir.Path, "desk", "Homework")));
        var step = Assert.Single(task.Steps);
        Assert.Equal("ok", step.Status);
        Assert.True(step.Verified);
        Assert.Contains("Homework", task.FinalAnswer, StringComparison.Ordinal);
        // The tool result reached the model wrapped as untrusted data.
        Assert.Contains(provider.Requests.Last().Messages, m => m.Role == "tool" && m.Content.Contains("untrusted", StringComparison.Ordinal));
        Assert.Contains(_audit, a => a.Contains("file.create_folder ok", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejected_Action_Is_Not_Executed()
    {
        var (engine, _) = await Engine(
            _ => ScriptedProvider.Text("plan"),
            _ => ScriptedProvider.Call("file_create", new JsonObject { ["path"] = "Desktop/x.txt", ["content"] = "hi" }),
            _ => ScriptedProvider.Text("The user declined."));
        var task = engine.Start(_store.Get(_phone.DeviceId)!, "make x.txt");
        await WaitFor(task, t => t.State == AgentTaskState.WaitingForApproval);
        engine.Approve(task.Id, _phone.DeviceId, task.Approval!.ApprovalId, false);
        await WaitFor(task, Terminal);
        Assert.Equal("rejected", task.Steps.Single().Status);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "desk", "x.txt")));
    }

    [Fact]
    public async Task Sensitive_Delete_Always_Asks_Even_With_AutoApprove_Write()
    {
        _policy = new AgentPolicy { AutoApproveWrite = true };
        File.WriteAllText(Path.Combine(_dir.Path, "desk", "old.txt"), "x");
        var (engine, _) = await Engine(
            _ => ScriptedProvider.Text("plan"),
            _ => ScriptedProvider.Call("file_delete", new JsonObject { ["path"] = "Desktop/old.txt" }),
            _ => ScriptedProvider.Text("ok"));
        var task = engine.Start(_store.Get(_phone.DeviceId)!, "delete old.txt");
        await WaitFor(task, t => t.State == AgentTaskState.WaitingForApproval);
        Assert.Equal(ToolRisk.Sensitive, task.Approval!.Risk);
        engine.Approve(task.Id, _phone.DeviceId, task.Approval.ApprovalId, true);
        await WaitFor(task, Terminal);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "desk", "old.txt")));
    }

    [Fact]
    public async Task Invalid_Or_Invented_Arguments_Are_Rejected_Before_Execution()
    {
        _policy = new AgentPolicy { AutoApproveWrite = true };
        var (engine, _) = await Engine(
            _ => ScriptedProvider.Text("plan"),
            _ => ScriptedProvider.Call("file_create_folder", new JsonObject { ["path"] = "Desktop/a", ["format"] = "png" }),
            _ => ScriptedProvider.Call("file_create", new JsonObject { ["path"] = "../../Windows/evil.txt" }),
            _ => ScriptedProvider.Call("shell_exec", new JsonObject { ["cmd"] = "format c:" }),
            _ => ScriptedProvider.Text("gave up"));
        var task = engine.Start(_store.Get(_phone.DeviceId)!, "x");
        await WaitFor(task, Terminal);
        Assert.Equal(3, task.Steps.Count);
        Assert.Contains("unknown argument 'format'", task.Steps[0].Error, StringComparison.Ordinal);
        Assert.Contains("not an allowed folder", task.Steps[1].Error, StringComparison.Ordinal);
        Assert.Contains("unknown tool", task.Steps[2].Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "desk", "a")));
    }

    [Fact]
    public async Task Revoking_Agent_Permission_Mid_Task_Denies_Further_Tools()
    {
        _policy = new AgentPolicy { AutoApproveWrite = true };
        var (engine, _) = await Engine(
            _ => ScriptedProvider.Text("plan"),
            _ =>
            {
                _store.Update(_phone.DeviceId, d => d with { Permissions = Permission.UseAi });
                return ScriptedProvider.Call("file_create_folder", new JsonObject { ["path"] = "Desktop/b" });
            },
            _ => ScriptedProvider.Text("stopped"));
        var task = engine.Start(_store.Get(_phone.DeviceId)!, "x");
        await WaitFor(task, Terminal);
        Assert.Equal("denied", task.Steps.Single().Status);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "desk", "b")));
    }

    [Fact]
    public async Task Cancel_Stops_A_Task_Waiting_For_Approval()
    {
        var (engine, _) = await Engine(
            _ => ScriptedProvider.Text("plan"),
            _ => ScriptedProvider.Call("file_create_folder", new JsonObject { ["path"] = "Desktop/c" }));
        var task = engine.Start(_store.Get(_phone.DeviceId)!, "x");
        await WaitFor(task, t => t.State == AgentTaskState.WaitingForApproval);
        Assert.True(engine.Cancel(task.Id, _phone.DeviceId));
        await WaitFor(task, Terminal);
        Assert.Equal(AgentTaskState.Cancelled, task.State);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "desk", "c")));
    }

    [Fact]
    public async Task Plan_Review_Allows_Editing()
    {
        _policy = new AgentPolicy { ReviewPlan = true };
        var (engine, provider) = await Engine(_ => ScriptedProvider.Text("1. do a thing"), _ => ScriptedProvider.Text("finished"));
        var task = engine.Start(_store.Get(_phone.DeviceId)!, "x");
        await WaitFor(task, t => t.State == AgentTaskState.WaitingForApproval);
        Assert.Equal("plan", task.Approval!.Kind);
        engine.Approve(task.Id, _phone.DeviceId, task.Approval.ApprovalId, true, "1. only list the Desktop");
        await WaitFor(task, Terminal);
        Assert.Equal("1. only list the Desktop", task.Plan);
        Assert.Contains("only list the Desktop", provider.Requests[1].Messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_Names_Round_Trip()
    {
        Assert.Equal("screen_list_monitors", ToolRegistry.ToModelName("screen.list_monitors"));
        Assert.Equal("screen.list_monitors", ToolRegistry.FromModelName("screen_list_monitors"));
        Assert.NotNull(_tools.Get("file_create"));
        Assert.All(_tools.Definitions(_ => true), d => Assert.Matches("^[a-zA-Z0-9_-]+$", d.Name));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("Desktop/../../etc")]
    [InlineData("C:/Windows")]
    [InlineData("Desktop/CON")]
    [InlineData("Desktop/a.txt:stream")]
    [InlineData("Desktop/nul.txt")]
    [InlineData("Unknown/x")]
    public void Sandbox_Rejects_Escapes(string path) => Assert.Throws<SandboxException>(() => _sandbox.Resolve(path));

    [Fact]
    public void Sandbox_Rejects_ReadOnly_Write_And_Junctions()
    {
        Assert.Throws<SandboxException>(() => _sandbox.Resolve("Docs/new.txt", forWrite: true));
        var target = Path.Combine(_dir.Path, "outside");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_dir.Path, "desk", "link");
        Directory.CreateSymbolicLink(link, target);
        Assert.Throws<SandboxException>(() => _sandbox.Resolve("Desktop/link/secret.txt"));
    }

    [Fact]
    public void Unique_Path_Never_Overwrites()
    {
        var p = Path.Combine(_dir.Path, "desk", "f.txt");
        File.WriteAllText(p, "1");
        Assert.EndsWith("f (2).txt", FileSandbox.UniquePath(p), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extractor_Classifies_Honestly()
    {
        var ex = new DocumentExtractor();
        var txt = Path.Combine(_dir.Path, "a.md");
        File.WriteAllText(txt, "# Tiêu đề\nnội dung");
        var r = await ex.ExtractAsync(txt, 1000, default);
        Assert.True(r.Supported);
        Assert.Contains("Tiêu đề", r.Text, StringComparison.Ordinal);

        var bin = Path.Combine(_dir.Path, "b.bin");
        File.WriteAllBytes(bin, [0, 1, 2, 3, 0, 255]);
        var rb = await ex.ExtractAsync(bin, 1000, default);
        Assert.False(rb.Supported);
        Assert.Equal(ContentKind.Binary, rb.Kind);

        var zip = Path.Combine(_dir.Path, "c.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            z.CreateEntry("../../evil.txt");
        var rz = await ex.ExtractAsync(zip, 1000, default);
        Assert.Contains("listing only", rz.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "..", "evil.txt")));

        var trunc = await ex.ExtractAsync(txt, 5, default);
        Assert.True(trunc.Truncated);
        Assert.Equal(5, trunc.Text.Length);
    }
}
