using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemotePC.Core.Agent;
using RemotePC.Core.AI;
using RemotePC.Core.Media;

namespace RemotePC.Host.Services;

/// <summary>Non-secret settings, stored as JSON in the data folder. Secrets live in DPAPI files.</summary>
public sealed record HostConfig
{
    public string? SignalingUrl { get; init; }
    public bool LanEnabled { get; init; } = true;
    public int LanPort { get; init; } = Core.Signaling.LanSignalingServer.DefaultPort;
    public QualityPreset DefaultQuality { get; init; } = QualityPreset.Auto;
    public bool RemoteInputEnabled { get; init; } = true;
    public string? ReceiveFolder { get; init; }
    public List<string> ExtraRoots { get; init; } = [];
    public AiConfig Ai { get; init; } = new();
    public Dictionary<string, AgentPolicy> AgentPolicies { get; init; } = [];
    public string Theme { get; init; } = "System"; // System | Light | Dark
    public string Language { get; init; } = "vi";
    public bool ReducedMotion { get; init; }
    public bool LaunchAtLogin { get; init; }
    public int LogRetentionDays { get; init; } = 14;
    public string UpdateRepo { get; init; } = "";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static HostConfig Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(path), Json) ?? new HostConfig();
        }
        catch (JsonException) { File.Copy(path, path + ".corrupt", true); }
        return new HostConfig();
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }
}

public static class DataPaths
{
    /// <summary>Portable mode: "data" next to the exe when writable (USB stick); otherwise %LOCALAPPDATA%.</summary>
    public static string Resolve()
    {
        var exeDir = AppContext.BaseDirectory;
        var portable = Path.Combine(exeDir, "data");
        try
        {
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return portable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePC-AI");
            Directory.CreateDirectory(local);
            return local;
        }
    }
}
