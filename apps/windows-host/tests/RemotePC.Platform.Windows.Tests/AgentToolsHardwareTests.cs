using System.Diagnostics;
using System.Text.Json.Nodes;
using RemotePC.Core.Agent;
using RemotePC.Core.AI;
using RemotePC.Core.Files;
using RemotePC.Core.Protocol;
using RemotePC.Core.Security;
using RemotePC.Core.Tests;
using RemotePC.Platform.Windows.Agent;
using RemotePC.Platform.Windows.Capture;
using Xunit;
using Xunit.Abstractions;

namespace RemotePC.Platform.Windows.Tests;

[Trait("Category", "Hardware")]
public sealed class AgentToolsHardwareTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private (ToolRegistry Reg, FileSandbox Sandbox) Tools()
    {
        var desk = Path.Combine(_dir.Path, "Desktop");
        Directory.CreateDirectory(desk);
        var sandbox = new FileSandbox([new SandboxRoot("Desktop", desk, true)]);
        var reg = new ToolRegistry();
        FileTools.Register(reg, sandbox, new DocumentExtractor(), SystemMetrics.Recycle);
        new WindowsAgentTools(new DesktopScreenSource(), sandbox).Register(reg);
        return (reg, sandbox);
    }

    private static Task<ToolResult> Run(ToolRegistry reg, string name, JsonObject args) =>
        reg.Get(name)!.ExecuteAsync(args, new ToolContext { TaskId = "t", Device = null!, Cancellation = CancellationToken.None, Progress = _ => { } });

    [Fact]
    public async Task Types_Vietnamese_Into_A_Window_And_Verifies_Via_UI_Automation()
    {
        var (reg, _) = Tools();
        // A throwaway text window owned by this test (never touches the user's own apps).
        const string title = "RPC-Agent-Test-Window";
        var script = $"Add-Type -AssemblyName System.Windows.Forms; $f=New-Object Windows.Forms.Form; $f.Text='{title}'; $t=New-Object Windows.Forms.TextBox; $t.Multiline=$true; $t.Dock='Fill'; $f.Controls.Add($t); $f.Add_Shown({{$t.Focus()}}); [Windows.Forms.Application]::Run($f)";
        using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -STA -Command \"{script}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            ToolResult? activate = null;
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(250);
                activate = await Run(reg, "window.activate", new JsonObject { ["title"] = title });
                if (activate.Ok) break;
            }
            output.WriteLine("activate: " + activate?.Summary);
            Assert.True(activate?.Ok, "test window could not be brought to the foreground: " + activate?.Summary);

            var typed = await Run(reg, "keyboard.type_text", new JsonObject { ["text"] = "Xin chào Việt Nam — đã kiểm tra" });
            Assert.True(typed.Ok, typed.Summary);
            await Task.Delay(300);
            var read = await Run(reg, "ui.read_text", new JsonObject());
            output.WriteLine("read: " + read.Summary);
            Assert.True(read.Ok, read.Summary);
            Assert.Contains("Xin chào Việt Nam — đã kiểm tra", read.Summary, StringComparison.Ordinal);

            var list = await Run(reg, "window.list", new JsonObject());
            Assert.Contains(title, list.Data!.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task Screenshot_System_And_App_List_Tools_Work()
    {
        var (reg, _) = Tools();
        var shot = await Run(reg, "screen.capture", new JsonObject());
        Assert.True(shot.Ok);
        Assert.NotNull(shot.ImagePng);
        output.WriteLine(shot.Summary);
        var cpu = await Run(reg, "system.get_cpu_usage", new JsonObject());
        output.WriteLine(cpu.Summary);
        Assert.Matches(@"CPU \d+%", cpu.Summary);
        var apps = await Run(reg, "app.list", new JsonObject());
        Assert.Contains("Notepad", apps.Data!.ToJsonString(), StringComparison.Ordinal);
        var del = await Run(reg, "file.create", new JsonObject { ["path"] = "Desktop/tmp.txt", ["content"] = "x" });
        Assert.True(del.Ok);
        var gone = await Run(reg, "file.delete", new JsonObject { ["path"] = "Desktop/tmp.txt" });
        Assert.True(gone.Ok && gone.Verified, gone.Summary);
    }

    /// <summary>The full agent loop with a real local model (qwen2.5:0.5b). Small models are unreliable planners, so
    /// the assertion is on safety + outcome, and the transcript is logged for the test report.</summary>
    [Fact]
    [Trait("Category", "LocalAI")]
    public async Task Real_Model_Creates_A_Folder_On_The_Sandboxed_Desktop()
    {
        var (reg, _) = Tools();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        using var ai = new AiService(http, new AiConfig());
        await ai.ScanAsync(default);
        var models = await ai.RefreshModelsAsync(default);
        var model = models.FirstOrDefault(m => m.Tools == true);
        Assert.NotNull(model);
        ai.Select(model.ProviderId, model.Id);

        using var keys = DeviceKeys.Generate();
        var device = new PairedDevice { Id = keys.DeviceId, Name = "t", Platform = "t", EdPk = Enc.B64u(keys.EdPk), XPk = Enc.B64u(keys.XPk), Permissions = Permission.AiAgent | Permission.UseAi };
        var engine = new AgentEngine(ai, reg, _ => device, _ => new AgentPolicy { AutoApproveWrite = true, MaxSteps = 6 }, (c, m, d) => output.WriteLine($"[{c}] {m}"));
        var task = engine.Start(device, "Create a folder named Homework on the Desktop.");
        for (var i = 0; i < 600 && task.State is not (AgentTaskState.Completed or AgentTaskState.Failed or AgentTaskState.Cancelled); i++) await Task.Delay(200);
        output.WriteLine($"state={task.State} answer={task.FinalAnswer} error={task.Error}");
        output.WriteLine("plan: " + task.Plan);
        foreach (var s in task.Steps) output.WriteLine($"step {s.Index}: {s.Tool} {s.Args.ToJsonString()} → {s.Status} {s.Result ?? s.Error}");
        Assert.True(Directory.Exists(Path.Combine(_dir.Path, "Desktop", "Homework")), "model did not create the folder (see transcript)");
    }
}
