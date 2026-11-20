using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using RemotePC.Core.Agent;
using RemotePC.Core.Files;
using RemotePC.Core.Input;
using RemotePC.Platform.Windows.Capture;
using RemotePC.Platform.Windows.Input;
using Tool = RemotePC.Core.Agent.FileTools.Tool;

namespace RemotePC.Platform.Windows.Agent;

/// <summary>Windows tools for the agent. UI Automation first; coordinates only relative to a fresh screenshot.</summary>
public sealed class WindowsAgentTools
{
    private readonly DesktopScreenSource _screen;
    private readonly InputInjector _input = new();
    private readonly FileSandbox _sandbox;
    private readonly Dictionary<int, double> _lastScale = [];

    public WindowsAgentTools(DesktopScreenSource screen, FileSandbox sandbox)
    {
        _screen = screen;
        _sandbox = sandbox;
    }

    public void Register(ToolRegistry reg)
    {
        // ---------------- screen ----------------
        reg.Add(new Tool("screen.capture", "Take a screenshot of a monitor. The image is sent to the user and, if the model supports vision, to you.",
            S.Obj(("monitor", S.Whole("monitor index, 0 = primary", 0, 16), false)), ToolRisk.Read,
            a => $"Screenshot of monitor {(int?)a["monitor"] ?? 0}", (a, ct) =>
            {
                var idx = (int?)a["monitor"] ?? 0;
                var mons = DesktopDuplicator.ListMonitors();
                if (idx >= mons.Count) return ToolResult.Fail($"monitor {idx} does not exist (have {mons.Count})");
                var png = _screen.CapturePng(idx, 1280);
                var w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
                var h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
                lock (_lastScale) _lastScale[idx] = (double)w / mons[idx].Bounds.Width;
                return new ToolResult(true, $"Captured monitor {idx}: screenshot is {w}x{h} pixels (mouse tools use these coordinates).", new JsonObject { ["width"] = w, ["height"] = h }, png);
            }, timeout: TimeSpan.FromSeconds(10)));

        reg.Add(new Tool("screen.list_monitors", "List monitors with resolution and position.", S.Obj(), ToolRisk.Read, _ => "List monitors", (a, ct) =>
            new ToolResult(true, "monitors", new JsonArray(DesktopDuplicator.ListMonitors().Select(m => (JsonNode)new JsonObject
            {
                ["index"] = m.Index, ["width"] = m.Bounds.Width, ["height"] = m.Bounds.Height, ["x"] = m.Bounds.X, ["y"] = m.Bounds.Y, ["primary"] = m.Primary,
            }).ToArray()))));

        reg.Add(new Tool("screen.get_resolution", "Get the resolution of a monitor.", S.Obj(("monitor", S.Whole("index", 0, 16), false)), ToolRisk.Read, _ => "Get resolution", (a, ct) =>
        {
            var mons = DesktopDuplicator.ListMonitors();
            var idx = (int?)a["monitor"] ?? 0;
            return idx < mons.Count ? new ToolResult(true, $"{mons[idx].Bounds.Width}x{mons[idx].Bounds.Height}") : ToolResult.Fail("no such monitor");
        }));

        // ---------------- windows ----------------
        reg.Add(new Tool("window.list", "List visible top-level windows (title, process, handle).", S.Obj(), ToolRisk.Read, _ => "List windows", (a, ct) =>
            new ToolResult(true, "windows", new JsonArray(Win32.VisibleWindows().Take(60).Select(w => (JsonNode)new JsonObject
            {
                ["handle"] = w.Handle.ToInt64(), ["title"] = w.Title, ["process"] = w.Process, ["minimized"] = w.Minimized,
            }).ToArray()))));

        reg.Add(new Tool("window.get_active", "Get the foreground window.", S.Obj(), ToolRisk.Read, _ => "Get active window", (a, ct) =>
        {
            var w = Win32.Foreground();
            return w is null ? ToolResult.Fail("no foreground window") : new ToolResult(true, $"Active: '{w.Title}' ({w.Process})", new JsonObject { ["handle"] = w.Handle.ToInt64(), ["title"] = w.Title, ["process"] = w.Process });
        }));

        foreach (var (name, verb, cmd) in new[] { ("window.activate", "Bring a window to the front", 9), ("window.minimize", "Minimize a window", 6), ("window.maximize", "Maximize a window", 3) })
        {
            reg.Add(new Tool(name, verb + ". Identify it by handle (from window.list) or by a part of its title.",
                S.Obj(("handle", S.Whole("window handle", 0, int.MaxValue), false), ("title", S.Str("part of the window title", 200), false)), ToolRisk.Write,
                a => $"{verb}: {(string?)a["title"] ?? a["handle"]?.ToString()}", (a, ct) =>
                {
                    var w = Win32.Find((long?)a["handle"], (string?)a["title"]);
                    if (w is null) return ToolResult.Fail("window not found");
                    Win32.Show(w.Handle, cmd);
                    if (cmd != 6) Win32.ForceForeground(w.Handle);
                    Thread.Sleep(300);
                    var ok = cmd == 6 ? Win32.IsMinimized(w.Handle) : Win32.Foreground()?.Handle == w.Handle;
                    return new ToolResult(ok, ok ? $"{verb} '{w.Title}' (verified)" : $"{verb} requested for '{w.Title}' but Windows did not confirm it (focus may be blocked)", Verified: ok);
                }));
        }

        // ---------------- mouse ----------------
        (string, System.Text.Json.Nodes.JsonObject, bool)[] xy() => new[] { ("x", S.Whole("x in pixels of the latest screenshot of that monitor", 0, 20000), true), ("y", S.Whole("y in pixels of the latest screenshot", 0, 20000), true), ("monitor", S.Whole("monitor index", 0, 16), false) };
        reg.Add(new Tool("mouse.move", "Move the mouse pointer.", S.Obj(xy()), ToolRisk.Write, a => $"Move mouse to ({a["x"]},{a["y"]})", (a, ct) => Mouse(a, null, false)));
        reg.Add(new Tool("mouse.click", "Click at a point in the latest screenshot. Prefer ui_click when the target has a name.",
            S.Obj([.. xy(), ("button", S.Str("left, right or middle", 10, "left", "right", "middle"), false)]), ToolRisk.Write,
            a => $"{(string?)a["button"] ?? "left"} click at ({a["x"]},{a["y"]})", (a, ct) => Mouse(a, (string?)a["button"] ?? "left", false)));
        reg.Add(new Tool("mouse.double_click", "Double-click at a point in the latest screenshot.", S.Obj(xy()), ToolRisk.Write,
            a => $"Double-click at ({a["x"]},{a["y"]})", (a, ct) => Mouse(a, "left", true)));
        reg.Add(new Tool("mouse.scroll", "Scroll the mouse wheel. Positive amount scrolls up.",
            S.Obj(("amount", S.Whole("notches, -20..20", -20, 20), true), ("horizontal", S.Bool("scroll horizontally"), false)), ToolRisk.Write,
            a => $"Scroll {a["amount"]}", (a, ct) =>
            {
                var n = (int)a["amount"]! * 120;
                var ok = (bool?)a["horizontal"] == true ? _input.Wheel(n, 0) : _input.Wheel(0, n);
                return new ToolResult(ok, ok ? "scrolled" : "scroll failed");
            }));
        reg.Add(new Tool("mouse.drag", "Drag from one point to another (screenshot pixel coordinates).",
            S.Obj(("from_x", S.Whole("start x", 0, 20000), true), ("from_y", S.Whole("start y", 0, 20000), true), ("to_x", S.Whole("end x", 0, 20000), true), ("to_y", S.Whole("end y", 0, 20000), true), ("monitor", S.Whole("monitor", 0, 16), false)),
            ToolRisk.Write, a => $"Drag ({a["from_x"]},{a["from_y"]}) → ({a["to_x"]},{a["to_y"]})", (a, ct) =>
            {
                var (mon, scale) = MonitorAndScale((int?)a["monitor"] ?? 0);
                if (mon is null) return ToolResult.Fail("no such monitor");
                var (fx, fy) = Norm((int)a["from_x"]!, (int)a["from_y"]!, mon.Value, scale);
                var (tx, ty) = Norm((int)a["to_x"]!, (int)a["to_y"]!, mon.Value, scale);
                _input.Button(MouseButton.Left, true, fx, fy, mon.Value);
                for (var i = 1; i <= 10; i++) { _input.MoveNormalized(fx + ((tx - fx) * i / 10), fy + ((ty - fy) * i / 10), mon.Value); Thread.Sleep(15); }
                _input.Button(MouseButton.Left, false, tx, ty, mon.Value);
                return new ToolResult(true, "dragged (take a screenshot to verify)");
            }));

        // ---------------- keyboard ----------------
        reg.Add(new Tool("keyboard.type_text", "Type Unicode text (any language) into the focused control. Refuses password fields.",
            S.Obj(("text", S.Str("text to type", 20_000), true)), ToolRisk.Write, a => $"Type {((string?)a["text"])?.Length ?? 0} characters", (a, ct) =>
            {
                if (Uia.FocusedIsPassword()) return ToolResult.Fail("the focused field is a password field; the agent never types into password fields");
                var ok = _input.Text((string)a["text"]!);
                return new ToolResult(ok, ok ? "typed (use ui_read_text to verify)" : "typing failed (the target may be elevated or on a secure desktop)");
            }, timeout: TimeSpan.FromSeconds(60)));
        reg.Add(new Tool("keyboard.press_key", "Press one key: enter, tab, esc, backspace, delete, up, down, left, right, home, end, pageup, pagedown, space, f1..f12.",
            S.Obj(("key", S.Str("key name", 20), true)), ToolRisk.Write, a => $"Press {(string?)a["key"]}", (a, ct) =>
            {
                var vk = Keys.Vk((string)a["key"]!);
                if (vk is null || vk == Keys.Win) return ToolResult.Fail("unknown or disallowed key");
                var ok = _input.Key(vk.Value, true) & _input.Key(vk.Value, false);
                return new ToolResult(ok, ok ? "pressed" : "key press failed");
            }));
        reg.Add(new Tool("keyboard.hotkey", "Press a key combination, e.g. [\"ctrl\",\"s\"]. Combinations with the Windows key need approval.",
            S.Obj(("keys", S.StrArray("keys in order, e.g. ctrl, shift, alt, win, a-z, 0-9, f1-f12", 4), true)), ToolRisk.Write,
            a => "Hotkey " + string.Join("+", a["keys"]!.AsArray().Select(k => (string?)k)), (a, ct) =>
            {
                var vks = a["keys"]!.AsArray().Select(k => Keys.Vk((string?)k ?? "")).ToList();
                if (vks.Count == 0 || vks.Any(v => v is null)) return ToolResult.Fail("unknown key in combination");
                var ok = _input.Combo(vks.Select(v => v!.Value).ToArray());
                return new ToolResult(ok, ok ? "hotkey sent" : "hotkey failed");
            }, a => a["keys"]!.AsArray().Any(k => (string?)k is "win" or "lwin" || ((string?)k == "alt" && a["keys"]!.AsArray().Any(x => (string?)x == "f4"))) ? ToolRisk.Sensitive : ToolRisk.Write));

        // ---------------- apps ----------------
        reg.Add(new Tool("app.list", "List applications that can be launched (Start menu shortcuts and built-in apps).",
            S.Obj(("filter", S.Str("optional name filter", 100), false)), ToolRisk.Read, _ => "List apps", (a, ct) =>
            {
                var f = (string?)a["filter"];
                var apps = Apps.All().Where(x => f is null || x.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).Take(200);
                return new ToolResult(true, "apps", new JsonArray(apps.Select(x => (JsonNode)x.Name).ToArray()));
            }));
        reg.Add(new Tool("app.launch", "Launch an application by its name from app.list. Optionally open an allowed folder/file (virtual path) with it. Arbitrary command lines are not supported.",
            S.Obj(("name", S.Str("app name, e.g. Notepad, Visual Studio Code, File Explorer", 200), true), ("open_path", S.Str("optional virtual path to open, e.g. Documents/Project", 1024), false)),
            ToolRisk.Write, a => $"Launch {(string?)a["name"]}{((string?)a["open_path"] is { } p ? " with " + p : "")}", async (a, ct) =>
            {
                var app = Apps.Find((string)a["name"]!);
                if (app is null) return ToolResult.Fail($"'{a["name"]}' is not in the app list; call app_list first");
                string? arg = null;
                if ((string?)a["open_path"] is { Length: > 0 } vp)
                {
                    if (!app.AcceptsPath) return ToolResult.Fail($"{app.Name} cannot open paths through the agent");
                    arg = "\"" + _sandbox.Resolve(vp).FullPath + "\"";
                }
                var before = Win32.VisibleWindows().Select(w => w.Handle).ToHashSet();
                using var p = Process.Start(new ProcessStartInfo(app.Target) { UseShellExecute = true, Arguments = arg ?? "" });
                for (var i = 0; i < 40; i++)
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    var fresh = Win32.VisibleWindows().FirstOrDefault(w => !before.Contains(w.Handle));
                    if (fresh is not null)
                    {
                        Win32.ForceForeground(fresh.Handle);
                        return new ToolResult(true, $"{app.Name} opened: window '{fresh.Title}' (verified)", new JsonObject { ["handle"] = fresh.Handle.ToInt64(), ["title"] = fresh.Title }, Verified: true);
                    }
                }
                return new ToolResult(true, $"{app.Name} was started but no new window appeared within 10 s (it may reuse an existing window)");
            }, timeout: TimeSpan.FromSeconds(20)));
        reg.Add(new Tool("app.focus", "Bring a running application's window to the front by app or window name.",
            S.Obj(("name", S.Str("application or window title", 200), true)), ToolRisk.Write, a => $"Focus {(string?)a["name"]}", (a, ct) =>
            {
                var n = (string)a["name"]!;
                var w = Win32.VisibleWindows().FirstOrDefault(x => x.Title.Contains(n, StringComparison.OrdinalIgnoreCase) || x.Process.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (w is null) return ToolResult.Fail("no window matches");
                Win32.Show(w.Handle, 9);
                Win32.ForceForeground(w.Handle);
                Thread.Sleep(200);
                var ok = Win32.Foreground()?.Handle == w.Handle;
                return new ToolResult(ok, ok ? $"focused '{w.Title}' (verified)" : "focus was not confirmed", Verified: ok);
            }));

        // ---------------- UI Automation ----------------
        reg.Add(new Tool("ui.read_text", "Read the text content of the active window (editors, documents, text boxes). Untrusted content.",
            S.Obj(("max_chars", S.Whole("limit", 100, 50_000), false)), ToolRisk.Read, _ => "Read active window text", (a, ct) =>
            {
                var t = Uia.ReadActiveText((int?)a["max_chars"] ?? 10_000);
                return t is null ? ToolResult.Fail("no readable text in the active window") : new ToolResult(true, t, Verified: true);
            }, timeout: TimeSpan.FromSeconds(15)));
        reg.Add(new Tool("ui.list_elements", "List named interactive elements (buttons, menus, fields) of the active window.",
            S.Obj(), ToolRisk.Read, _ => "List UI elements", (a, ct) => new ToolResult(true, "elements", Uia.ListElements(80)), timeout: TimeSpan.FromSeconds(15)));
        reg.Add(new Tool("ui.click", "Click (invoke) an element of the active window by its name from ui_list_elements.",
            S.Obj(("name", S.Str("element name", 200), true)), ToolRisk.Write, a => $"Click '{(string?)a["name"]}'", (a, ct) =>
            {
                var r = Uia.Click((string)a["name"]!);
                return r is null ? new ToolResult(true, "clicked") : ToolResult.Fail(r);
            }, timeout: TimeSpan.FromSeconds(15)));

        // ---------------- system ----------------
        reg.Add(new Tool("system.get_info", "Computer name, Windows version, user, uptime.", S.Obj(), ToolRisk.Read, _ => "System info", (a, ct) =>
            new ToolResult(true, "system", new JsonObject
            {
                ["computer"] = Environment.MachineName, ["os"] = RuntimeInformation.OSDescription, ["user"] = Environment.UserName,
                ["uptime_hours"] = Math.Round(Environment.TickCount64 / 3_600_000.0, 1), ["cpus"] = Environment.ProcessorCount,
            })));
        reg.Add(new Tool("system.get_cpu_usage", "Current total CPU usage in percent.", S.Obj(), ToolRisk.Read, _ => "CPU usage", (a, ct) =>
            new ToolResult(true, $"CPU {SystemMetrics.SampleCpuPercent(TimeSpan.FromMilliseconds(500)):F0}%")));
        reg.Add(new Tool("system.get_memory_usage", "RAM usage.", S.Obj(), ToolRisk.Read, _ => "Memory usage", (a, ct) =>
        {
            var (total, avail) = SystemMetrics.Memory();
            return new ToolResult(true, $"RAM {(total - avail) / 1073741824.0:F1} / {total / 1073741824.0:F1} GB used");
        }));
        reg.Add(new Tool("system.get_disk_usage", "Free and total space of fixed drives.", S.Obj(), ToolRisk.Read, _ => "Disk usage", (a, ct) =>
            new ToolResult(true, "drives", new JsonArray(DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => (JsonNode)new JsonObject
            {
                ["drive"] = d.Name, ["free_gb"] = Math.Round(d.AvailableFreeSpace / 1073741824.0, 1), ["total_gb"] = Math.Round(d.TotalSize / 1073741824.0, 1),
            }).ToArray()))));
    }

    private (PixelRect? Mon, double Scale) MonitorAndScale(int idx)
    {
        var mons = DesktopDuplicator.ListMonitors();
        if (idx >= mons.Count) return (null, 1);
        double scale;
        lock (_lastScale) scale = _lastScale.GetValueOrDefault(idx, 1.0);
        return (mons[idx].Bounds, scale);
    }

    private static (double X, double Y) Norm(int x, int y, PixelRect mon, double scale) =>
        (Math.Clamp(x / scale / mon.Width, 0, 1), Math.Clamp(y / scale / mon.Height, 0, 1));

    private ToolResult Mouse(JsonObject a, string? button, bool dbl)
    {
        var (mon, scale) = MonitorAndScale((int?)a["monitor"] ?? 0);
        if (mon is null) return ToolResult.Fail("no such monitor");
        var (nx, ny) = Norm((int)a["x"]!, (int)a["y"]!, mon.Value, scale);
        if (button is null) return new ToolResult(_input.MoveNormalized(nx, ny, mon.Value), "moved");
        var b = button switch { "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => MouseButton.Left };
        for (var i = 0; i < (dbl ? 2 : 1); i++)
        {
            _input.Button(b, true, nx, ny, mon.Value);
            _input.Button(b, false, nx, ny, mon.Value);
            if (dbl) Thread.Sleep(60);
        }
        return new ToolResult(true, "clicked (a click is not proof of success: verify with a screenshot or ui_read_text)");
    }
}

internal static class Keys
{
    public const ushort Win = 0x5B;
    private static readonly Dictionary<string, ushort> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["space"] = 0x20, ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = Win, ["lwin"] = Win, ["insert"] = 0x2D,
    };

    public static ushort? Vk(string name)
    {
        if (Map.TryGetValue(name, out var v)) return v;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        if (name.Length is 2 or 3 && (name[0] is 'f' or 'F') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 12) return (ushort)(0x70 + f - 1);
        return null;
    }
}

internal sealed record AppEntry(string Name, string Target, bool AcceptsPath);

internal static class Apps
{
    public static IEnumerable<AppEntry> All()
    {
        var list = new List<AppEntry>
        {
            new("Notepad", "notepad.exe", true),
            new("Calculator", "calc.exe", false),
            new("Paint", "mspaint.exe", true),
            new("File Explorer", "explorer.exe", true),
        };
        var code = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        }.FirstOrDefault(File.Exists);
        if (code is not null) list.Add(new("Visual Studio Code", code, true));
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 }))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                if (!list.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) list.Add(new(name, lnk, false));
            }
        }
        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static AppEntry? Find(string name)
    {
        var all = All().ToList();
        return all.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(a => a.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
               ?? (name.Equals("vscode", StringComparison.OrdinalIgnoreCase) || name.Equals("code", StringComparison.OrdinalIgnoreCase) ? all.FirstOrDefault(a => a.Name == "Visual Studio Code") : null);
    }
}

internal sealed record WinInfo(IntPtr Handle, string Title, string Process, bool Minimized);

internal static partial class Win32
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, [Out] char[] s, int n);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    public static List<WinInfo> VisibleWindows()
    {
        var list = new List<WinInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out var cloaked, 4) == 0 && cloaked != 0) return true;
            var buf = new char[256];
            var len = GetWindowText(h, buf, buf.Length);
            if (len == 0) return true;
            var title = new string(buf, 0, len);
            if (title == "Program Manager") return true;
            if (GetWindowThreadProcessId(h, out var pid) == 0) return true;
            string proc;
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); proc = p.ProcessName; } catch (ArgumentException) { proc = "?"; }
            list.Add(new WinInfo(h, title, proc, IsIconic(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WinInfo? Foreground()
    {
        var h = GetForegroundWindow();
        return VisibleWindows().FirstOrDefault(w => w.Handle == h);
    }

    public static WinInfo? Find(long? handle, string? title) =>
        VisibleWindows().FirstOrDefault(w => (handle is not null && w.Handle.ToInt64() == handle) || (title is { Length: > 0 } && w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)));

    public static void Show(IntPtr h, int cmd) => ShowWindow(h, cmd);
    public static bool IsMinimized(IntPtr h) => IsIconic(h);

    /// <summary>SetForegroundWindow with the documented AttachThreadInput technique (no foreground-lock hacks).</summary>
    public static void ForceForeground(IntPtr h)
    {
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out var _);
        var me = GetCurrentThreadId();
        if (fgThread != me) AttachThreadInput(me, fgThread, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (fgThread != me) AttachThreadInput(me, fgThread, false);
    }
}

internal static class Uia
{
    private static AutomationElement? Active(UIA3Automation a)
    {
        var h = Win32.Foreground()?.Handle;
        return h is null ? null : a.FromHandle(h.Value);
    }

    public static bool FocusedIsPassword()
    {
        try
        {
            using var a = new UIA3Automation();
            var f = a.FocusedElement();
            return f is not null && f.Properties.IsPassword.ValueOrDefault;
        }
        catch (COMException) { return false; }
    }

    public static string? ReadActiveText(int max)
    {
        using var a = new UIA3Automation();
        var w = Active(a);
        if (w is null) return null;
        var cf = a.ConditionFactory;
        var el = w.FindFirstDescendant(cf.ByControlType(ControlType.Document)) ?? w.FindFirstDescendant(cf.ByControlType(ControlType.Edit));
        if (el is null) return null;
        if (el.Properties.IsPassword.ValueOrDefault) return "[password field — not read]";
        string? text = null;
        if (el.Patterns.Text.IsSupported) text = el.Patterns.Text.Pattern.DocumentRange.GetText(max);
        else if (el.Patterns.Value.IsSupported) text = el.Patterns.Value.Pattern.Value.ValueOrDefault;
        if (text is null) return null;
        return $"Window '{w.Name}':\n" + (text.Length > max ? text[..max] : text);
    }

    public static JsonArray ListElements(int max)
    {
        using var a = new UIA3Automation();
        var w = Active(a);
        var arr = new JsonArray();
        if (w is null) return arr;
        var types = new[] { ControlType.Button, ControlType.MenuItem, ControlType.Edit, ControlType.CheckBox, ControlType.ListItem, ControlType.TabItem, ControlType.Hyperlink, ControlType.ComboBox, ControlType.TreeItem };
        foreach (var e in w.FindAllDescendants())
        {
            if (arr.Count >= max) break;
            var ct = e.Properties.ControlType.ValueOrDefault;
            var name = e.Properties.Name.ValueOrDefault;
            if (!types.Contains(ct) || string.IsNullOrWhiteSpace(name) || e.Properties.IsOffscreen.ValueOrDefault) continue;
            arr.Add(new JsonObject { ["name"] = name, ["type"] = ct.ToString(), ["enabled"] = e.Properties.IsEnabled.ValueOrDefault });
        }
        return arr;
    }

    public static string? Click(string name)
    {
        using var a = new UIA3Automation();
        var w = Active(a);
        if (w is null) return "no active window";
        var el = w.FindFirstDescendant(a.ConditionFactory.ByName(name));
        if (el is null) return $"no element named '{name}' (call ui_list_elements)";
        if (!el.Properties.IsEnabled.ValueOrDefault) return $"'{name}' is disabled";
        if (el.Patterns.Invoke.IsSupported) { el.Patterns.Invoke.Pattern.Invoke(); return null; }
        if (el.Patterns.Toggle.IsSupported) { el.Patterns.Toggle.Pattern.Toggle(); return null; }
        if (el.Patterns.SelectionItem.IsSupported) { el.Patterns.SelectionItem.Pattern.Select(); return null; }
        el.Click();
        return null;
    }
}

public static class SystemMetrics
{
    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint Low, High; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private static ulong U(FILETIME f) => ((ulong)f.High << 32) | f.Low;

    private static (ulong Idle, ulong Total) Times()
    {
        GetSystemTimes(out var i, out var k, out var u);
        return (U(i), U(k) + U(u));
    }

    private static (ulong Idle, ulong Total) _last = Times();

    /// <summary>CPU % since the previous call (non-blocking; for dashboards polling every second).</summary>
    public static double CpuPercentSinceLast()
    {
        var now = Times();
        var (pi, pt) = _last;
        _last = now;
        var total = now.Total - pt;
        return total == 0 ? 0 : Math.Clamp(100.0 * (total - (now.Idle - pi)) / total, 0, 100);
    }

    public static double SampleCpuPercent(TimeSpan window)
    {
        var a = Times();
        Thread.Sleep(window);
        var b = Times();
        var total = b.Total - a.Total;
        return total == 0 ? 0 : Math.Clamp(100.0 * (total - (b.Idle - a.Idle)) / total, 0, 100);
    }

    public static (ulong Total, ulong Available) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        return (m.ullTotalPhys, m.ullAvailPhys);
    }

    /// <summary>Sends a file/folder to the Recycle Bin (SHFileOperation FO_DELETE + FOF_ALLOWUNDO). Returns false if not possible.</summary>
    public static bool Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT { wFunc = 3, pFrom = path + "\0\0", fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400 }; // ALLOWUNDO|NOCONFIRMATION|SILENT|NOERRORUI
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
