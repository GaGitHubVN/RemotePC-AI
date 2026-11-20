using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemotePC.Core.Agent;
using RemotePC.Core.Logging;
using RemotePC.Core.Media;
using RemotePC.Core.Net;
using RemotePC.Core.Security;
using RemotePC.Core.Signaling;
using RemotePC.Host.Services;
using RemotePC.Platform.Windows.Agent;

namespace RemotePC.Host.ViewModels;

public sealed partial class PermissionToggle : ObservableObject
{
    private readonly Action<Permission, bool> _changed;
    public PermissionToggle(Permission p, bool on, Action<Permission, bool> changed)
    {
        Permission = p;
        _enabled = on;
        _changed = changed;
    }
    public Permission Permission { get; }
    public string Key => "perm." + PermissionNames.NameOf(Permission);
    [ObservableProperty] private bool _enabled;
    partial void OnEnabledChanged(bool value) => _changed(Permission, value);
}

public sealed partial class DeviceVm : ObservableObject
{
    public required string Id { get; init; }
    [ObservableProperty] private string _name = "";
    public required string Platform { get; init; }
    public required string PairedAt { get; init; }
    [ObservableProperty] private string _lastSeen = "";
    [ObservableProperty] private bool _connected;
    public ObservableCollection<PermissionToggle> Permissions { get; } = [];
}

public sealed record SessionRow(string Id, string Device, string Started, string Display, string Stream, string Rtt);
public sealed record ProviderRow(string Name, string Endpoint, bool Running, string Status);
public sealed record ModelRow(string ProviderId, string Id, string Context, string Tools, string Vision, string Size, bool Selected);
public sealed record TaskRow(string Id, string Request, string State, string Steps, string Device);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly HostRuntime _rt;
    private readonly DispatcherTimer _timer;
    public Loc L => Loc.Instance;

    [ObservableProperty] private string _page = "dashboard";
    [ObservableProperty] private string _hostName = Environment.MachineName;
    [ObservableProperty] private string _deviceId = "";
    [ObservableProperty] private string _onlineText = "";
    [ObservableProperty] private bool _online;
    [ObservableProperty] private string _localIps = "";
    [ObservableProperty] private string _internetState = "";
    [ObservableProperty] private int _pairedCount;
    [ObservableProperty] private int _sessionCount;
    [ObservableProperty] private string _latency = "—";
    [ObservableProperty] private string _streamQuality = "—";
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private string _ram = "";
    [ObservableProperty] private string _aiStatus = "";
    [ObservableProperty] private string _selectedModel = "";
    [ObservableProperty] private bool _locked;
    [ObservableProperty] private string _aiTestResult = "";
    [ObservableProperty] private ModelRow? _modelSelection;
    [ObservableProperty] private int _maxSessions;
    [ObservableProperty] private int _aiConcurrency;

    // settings
    [ObservableProperty] private string _signalingUrl = "";
    [ObservableProperty] private bool _lanEnabled;
    [ObservableProperty] private bool _remoteInput;
    [ObservableProperty] private QualityPreset _quality;
    [ObservableProperty] private string _theme = "System";
    [ObservableProperty] private string _language = "vi";
    [ObservableProperty] private bool _reducedMotion;
    [ObservableProperty] private bool _launchAtLogin;
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private string _receiveFolder = "";
    [ObservableProperty] private bool _policyAutoRead = true;
    [ObservableProperty] private bool _policyAutoWrite;
    [ObservableProperty] private bool _policyReviewPlan;
    [ObservableProperty] private string _updateStatus = "";

    public ObservableCollection<DeviceVm> Devices { get; } = [];
    public ObservableCollection<SessionRow> SessionsList { get; } = [];
    public ObservableCollection<ProviderRow> Providers { get; } = [];
    public ObservableCollection<ModelRow> Models { get; } = [];
    public ObservableCollection<TaskRow> Tasks { get; } = [];
    public ObservableCollection<ActivityEntry> Activity { get; } = [];
    public ObservableCollection<string> Roots { get; } = [];
    public IReadOnlyList<QualityPreset> QualityOptions { get; } = Enum.GetValues<QualityPreset>().Where(q => q != QualityPreset.Custom).ToList();
    public IReadOnlyList<string> ThemeOptions { get; } = ["System", "Light", "Dark"];
    public IReadOnlyList<string> LanguageOptions { get; } = ["vi", "en"];

    public event Action? RequestPairing;
    public event Action<string>? ThemeChanged;

    public MainViewModel(HostRuntime rt)
    {
        _rt = rt;
        DeviceId = rt.Keys.DeviceId;
        LoadSettings();
        rt.Log.Added += e => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Activity.Insert(0, e);
            while (Activity.Count > 300) Activity.RemoveAt(Activity.Count - 1);
        });
        foreach (var e in rt.Log.Recent.Reverse().Take(300)) Activity.Add(e);
        rt.StateChanged += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Application.Current.Dispatcher);
        _timer.Start();
        Refresh();
    }

    private void LoadSettings()
    {
        var c = _rt.Config;
        SignalingUrl = c.SignalingUrl ?? "";
        LanEnabled = c.LanEnabled;
        RemoteInput = c.RemoteInputEnabled;
        Quality = c.DefaultQuality;
        Theme = c.Theme;
        Language = c.Language;
        ReducedMotion = c.ReducedMotion;
        LaunchAtLogin = c.LaunchAtLogin;
        RetentionDays = c.LogRetentionDays;
        MaxSessions = _rt.Devices.MaxConcurrentSessions;
        AiConcurrency = c.Ai.MaxConcurrentInferences;
        ReceiveFolder = _rt.Sandbox.Roots.FirstOrDefault(r => r.Alias == "Received")?.FullPath ?? "";
        Loc.Instance.Language = Language;
    }

    private void Tick()
    {
        Cpu = Math.Round(SystemMetrics.CpuPercentSinceLast());
        var (total, avail) = SystemMetrics.Memory();
        Ram = $"{(total - avail) / 1073741824.0:F1} / {total / 1073741824.0:F1} GB";
        var s = _rt.Sessions.Sessions.FirstOrDefault();
        Latency = s?.InputRttMs is { } rtt ? $"{rtt:F0} ms" : "—";
        StreamQuality = s?.Stats is { } st ? $"{st.Width}×{st.Height} · {st.Fps} fps · {st.Kbps} kbps{(st.Hardware ? " · HW" : "")}" : "—";
        if (SessionsList.Count > 0 || _rt.Sessions.Sessions.Count > 0) RefreshSessions();
    }

    public void Refresh()
    {
        var internet = _rt.Internet?.State;
        Online = internet == SignalingState.Online || _rt.Lan is not null;
        InternetState = internet switch
        {
            null => L["dash.lanonly"],
            SignalingState.Online => $"{L["dash.online"]} ({_rt.Internet!.LastRtt?.TotalMilliseconds:F0} ms)",
            _ => $"{internet} {_rt.Internet?.LastError}",
        };
        OnlineText = Online ? L["dash.online"] : L["dash.offline"];
        LocalIps = string.Join(", ", NetworkInfo.LanAddresses());
        Locked = _rt.Sessions.Locked;

        var sessions = _rt.Sessions.Sessions;
        PairedCount = _rt.Devices.All.Count;
        SessionCount = sessions.Count;
        SyncDevices();
        RefreshSessions();

        var statuses = _rt.Ai.Statuses;
        Providers.Clear();
        foreach (var p in statuses) Providers.Add(new ProviderRow(p.Name, p.Endpoint, p.Running, p.Running ? $"v{p.Version}" : p.Error ?? "stopped"));
        var sel = _rt.Ai.Selected;
        Models.Clear();
        foreach (var m in _rt.Ai.CachedModels)
        {
            Models.Add(new ModelRow(m.ProviderId, m.Id, m.ContextLength?.ToString("N0") ?? L["ai.unknown"], Tri(m.Tools), Tri(m.Vision),
                m.SizeBytes is { } b ? $"{b / 1073741824.0:F1} GB" : "", sel?.Provider.Id == m.ProviderId && _rt.Ai.Config.ModelId == m.Id));
        }
        AiStatus = statuses.Any(p => p.Running) ? string.Join(", ", statuses.Where(p => p.Running).Select(p => p.Name)) : L["dash.offline"];
        SelectedModel = _rt.Ai.Config.ModelId ?? L["ai.noModel"];

        Tasks.Clear();
        foreach (var t in _rt.Agent.Tasks.Take(50))
            Tasks.Add(new TaskRow(t.Id, t.Request, t.State.ToString(), $"{t.Steps.Count(x => x.Status == "ok")}/{t.Steps.Count}", _rt.Devices.Get(t.DeviceId)?.Name ?? t.DeviceId[..6]));

        Roots.Clear();
        foreach (var r in _rt.Sandbox.Roots) Roots.Add($"{r.Alias}  →  {r.FullPath}");
    }

    private string Tri(bool? b) => b switch { true => "✓", false => "✗", _ => L["ai.unknown"] };

    private void RefreshSessions()
    {
        SessionsList.Clear();
        foreach (var s in _rt.Sessions.Sessions)
        {
            var st = s.Stats;
            SessionsList.Add(new SessionRow(s.Id, s.Device?.Name ?? s.DeviceId, s.StartedAt.ToLocalTime().ToString("HH:mm:ss"), $"#{s.DisplayIndex}",
                st is null ? s.State.ToString() : $"{st.Encoder} {st.Width}×{st.Height} {st.Fps}fps {st.Kbps}kbps", s.InputRttMs is { } r ? $"{r:F0} ms" : "—"));
        }
        SessionCount = SessionsList.Count;
        var connected = _rt.Sessions.Sessions.Select(x => x.DeviceId).ToHashSet();
        foreach (var d in Devices) d.Connected = connected.Contains(d.Id);
    }

    private void SyncDevices()
    {
        var all = _rt.Devices.All;
        foreach (var gone in Devices.Where(d => all.All(a => a.Id != d.Id)).ToList()) Devices.Remove(gone);
        foreach (var d in all)
        {
            var vm = Devices.FirstOrDefault(x => x.Id == d.Id);
            if (vm is null)
            {
                vm = new DeviceVm { Id = d.Id, Name = d.Name, Platform = d.Platform, PairedAt = d.PairedAt.ToLocalTime().ToString("g") };
                foreach (var p in PermissionNames.All)
                    vm.Permissions.Add(new PermissionToggle(p, d.Permissions.HasFlag(p), (perm, on) => SetPermission(d.Id, perm, on)));
                Devices.Add(vm);
            }
            vm.Name = d.Name;
            vm.LastSeen = d.LastSeen?.ToLocalTime().ToString("g") ?? "—";
        }
    }

    private void SetPermission(string id, Permission p, bool on)
    {
        _rt.Devices.Update(id, d => d with { Permissions = on ? d.Permissions | p : d.Permissions & ~p });
        _rt.Log.Write("security", $"{(on ? "granted" : "removed")} {PermissionNames.NameOf(p)} for {_rt.Devices.Get(id)?.Name}", id);
        if (p == Permission.AiAgent && !on) _rt.Agent.CancelAll(id);
    }

    [RelayCommand] private void Navigate(string page) => Page = page;
    [RelayCommand] private void Pair() => RequestPairing?.Invoke();

    [RelayCommand]
    private void Revoke(DeviceVm d)
    {
        if (MessageBox.Show($"{L["btn.revoke"]}: {d.Name}?", "RemotePC AI", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _rt.Agent.CancelAll(d.Id);
        _rt.Devices.Revoke(d.Id);
        _rt.Log.Write("security", $"device revoked: {d.Name}", d.Id);
    }

    [RelayCommand]
    private void RenameDevice(DeviceVm d)
    {
        var name = Views.InputDialog.Prompt(Application.Current.MainWindow, L["btn.rename"], d.Name);
        if (!string.IsNullOrWhiteSpace(name)) _rt.Devices.Update(d.Id, x => x with { Name = name.Trim()[..Math.Min(64, name.Trim().Length)] });
    }

    [RelayCommand] private async Task DisconnectSession(SessionRow s)
    {
        var session = _rt.Sessions.Sessions.FirstOrDefault(x => x.Id == s.Id);
        if (session is not null) await _rt.Sessions.CloseSessionAsync(session, "host_disconnect");
    }

    [RelayCommand] private async Task DisconnectAll() => await _rt.Sessions.CloseAllAsync("host_disconnect");
    [RelayCommand] private async Task Emergency() => await _rt.EmergencyDisconnectAsync();

    [RelayCommand]
    private async Task RevokeAll()
    {
        if (MessageBox.Show(L["btn.revokeAll"] + "?", "RemotePC AI", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            await _rt.RevokeAllAsync();
    }

    partial void OnLockedChanged(bool value) { if (_rt.Sessions.Locked != value) _rt.Sessions.Locked = value; }
    partial void OnMaxSessionsChanged(int value) { if (value is >= 1 and <= 8 && _rt.Devices.MaxConcurrentSessions != value) _rt.Devices.MaxConcurrentSessions = value; }

    [RelayCommand] private async Task ScanProviders() { await _rt.Ai.ScanAsync(CancellationToken.None); await _rt.Ai.RefreshModelsAsync(CancellationToken.None); }
    [RelayCommand] private async Task RefreshModels() => await _rt.Ai.RefreshModelsAsync(CancellationToken.None);

    [RelayCommand]
    private void SelectModel(ModelRow? m)
    {
        m ??= ModelSelection;
        if (m is null) return;
        _rt.Ai.Select(m.ProviderId, m.Id);
        _rt.Log.Write("ai", $"model selected: {m.Id}");
    }

    [RelayCommand]
    private async Task TestInference()
    {
        AiTestResult = "…";
        var r = await _rt.Ai.TestInferenceAsync(CancellationToken.None);
        AiTestResult = r.Ok
            ? $"✓ \"{r.Text.Trim()}\" — first token {r.Usage?.FirstTokenMs:F0} ms, {r.Usage?.TokensPerSecond:F1} tok/s"
            : "✗ " + r.Error;
    }

    [RelayCommand] private void CancelTask(TaskRow t) => _rt.Agent.Cancel(t.Id, _rt.Agent.Get(t.Id)?.DeviceId ?? "");

    [RelayCommand] private void ClearLogs() { _rt.Log.Clear(); Activity.Clear(); }

    [RelayCommand]
    private void ExportLogs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"remotepc-logs-{DateTime.Now:yyyyMMdd-HHmm}.jsonl", Filter = "JSON Lines|*.jsonl" };
        if (dlg.ShowDialog() == true) _rt.Log.Export(dlg.FileName);
    }

    [RelayCommand]
    private void ChooseReceiveFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = ReceiveFolder };
        if (dlg.ShowDialog() == true)
        {
            ReceiveFolder = dlg.FolderName;
            _rt.UpdateConfig(c => c with { ReceiveFolder = dlg.FolderName });
            MessageBox.Show("Restart RemotePC AI to apply the new folder.", "RemotePC AI");
        }
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        if (!string.IsNullOrWhiteSpace(SignalingUrl) && !SignalingUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("The signaling URL must start with wss:// (TLS).", "RemotePC AI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var oldUrl = _rt.Config.SignalingUrl;
        _rt.UpdateConfig(c => c with
        {
            SignalingUrl = string.IsNullOrWhiteSpace(SignalingUrl) ? null : SignalingUrl.Trim(),
            LanEnabled = LanEnabled, RemoteInputEnabled = RemoteInput, DefaultQuality = Quality, Theme = Theme, Language = Language,
            ReducedMotion = ReducedMotion, LaunchAtLogin = LaunchAtLogin, LogRetentionDays = Math.Clamp(RetentionDays, 1, 365),
            Ai = c.Ai with { MaxConcurrentInferences = Math.Clamp(AiConcurrency, 1, 4) },
        });
        _rt.Ai.UpdateConfig(a => a with { MaxConcurrentInferences = Math.Clamp(AiConcurrency, 1, 4) });
        Autostart.Apply(LaunchAtLogin);
        Loc.Instance.Language = Language;
        ThemeChanged?.Invoke(Theme);
        if (oldUrl != _rt.Config.SignalingUrl) await _rt.ConnectInternetAsync();
        _rt.Log.Write("settings", "settings saved");
        Refresh();
    }

    [RelayCommand]
    private void SavePolicy()
    {
        var policy = new AgentPolicy { AutoApproveRead = PolicyAutoRead, AutoApproveWrite = PolicyAutoWrite, ReviewPlan = PolicyReviewPlan };
        _rt.UpdateConfig(c => c with { AgentPolicies = _rt.Devices.All.ToDictionary(d => d.Id, _ => policy) });
        _rt.Log.Write("security", $"agent policy: autoRead={policy.AutoApproveRead} autoWrite={policy.AutoApproveWrite} reviewPlan={policy.ReviewPlan}");
    }

    [RelayCommand]
    private async Task CheckUpdate()
    {
        UpdateStatus = "…";
        UpdateStatus = await Updater.CheckAsync(_rt.Config.UpdateRepo);
    }
}

/// <summary>HKCU Run entry, written only when the user enables it; removed when disabled.</summary>
public static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void Apply(bool enabled)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key, writable: true);
        if (k is null) return;
        if (enabled) k.SetValue("RemotePC-AI", $"\"{Environment.ProcessPath}\" --minimized");
        else if (k.GetValue("RemotePC-AI") is not null) k.DeleteValue("RemotePC-AI");
    }
}

/// <summary>Checks GitHub Releases for a newer version. Only reports it; never downloads or runs anything by itself.</summary>
public static class Updater
{
    public static async Task<string> CheckAsync(string repo)
    {
        if (string.IsNullOrWhiteSpace(repo) || !System.Text.RegularExpressions.Regex.IsMatch(repo, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            return "Update source not configured (Settings → UpdateRepo = owner/repo).";
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePC-AI-Updater");
        try
        {
            var json = await http.GetStringAsync(new Uri($"https://api.github.com/repos/{repo}/releases/latest"));
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            var tag = (string?)node["tag_name"] ?? "?";
            var current = typeof(Updater).Assembly.GetName().Version;
            var newer = Version.TryParse(tag.TrimStart('v').Split('-')[0], out var v) && current is not null && v > current;
            return newer ? $"New version {tag} available — download it from github.com/{repo}/releases and verify SHA256SUMS.txt." : $"Up to date ({current}).";
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return "Update check failed: " + e.Message;
        }
    }
}
