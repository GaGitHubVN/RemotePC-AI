// Instance API by design (per-session pressed-key tracking, mockable seam); some members happen to be stateless.
#pragma warning disable CA1822
using System.Runtime.InteropServices;
using RemotePC.Core.Input;

namespace RemotePC.Platform.Windows.Input;

public enum MouseButton { Left, Right, Middle }

/// <summary>
/// Injects input with SendInput. Works only on the interactive desktop of the user running the host:
/// it cannot drive the secure desktop (UAC prompts, Ctrl+Alt+Del, lock screen) or elevated windows from a
/// non-elevated process (UIPI). Those limits are by design and are surfaced to the client.
/// </summary>
public sealed class InputInjector
{
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>Marker on injected events so the host can recognise its own input.</summary>
    private static readonly IntPtr Marker = new(0x52504331); // "RPC1"

    private readonly HashSet<ushort> _pressedKeys = [];
    private readonly HashSet<MouseButton> _pressedButtons = [];
    private readonly Lock _lock = new();

    public static PixelRect VirtualDesktop =>
        new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79)); // SM_X/Y/CX/CYVIRTUALSCREEN

    public bool MoveNormalized(double nx, double ny, PixelRect monitor)
    {
        var (ax, ay) = CoordinateMapper.ToAbsolute(nx, ny, monitor, VirtualDesktop);
        return Send(Mouse(ax, ay, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0));
    }

    public bool Button(MouseButton b, bool down, double nx, double ny, PixelRect monitor)
    {
        var (ax, ay) = CoordinateMapper.ToAbsolute(nx, ny, monitor, VirtualDesktop);
        var flag = (b, down) switch
        {
            (MouseButton.Left, true) => MOUSEEVENTF_LEFTDOWN,
            (MouseButton.Left, false) => MOUSEEVENTF_LEFTUP,
            (MouseButton.Right, true) => MOUSEEVENTF_RIGHTDOWN,
            (MouseButton.Right, false) => MOUSEEVENTF_RIGHTUP,
            (MouseButton.Middle, true) => MOUSEEVENTF_MIDDLEDOWN,
            _ => MOUSEEVENTF_MIDDLEUP,
        };
        lock (_lock) { if (down) _pressedButtons.Add(b); else _pressedButtons.Remove(b); }
        // Move and press in one SendInput call so nothing can interleave.
        return Send(Mouse(ax, ay, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0), Mouse(0, 0, flag, 0));
    }

    /// <param name="dx">horizontal wheel delta (120 = one notch, positive = right)</param>
    /// <param name="dy">vertical wheel delta (positive = away from user / up)</param>
    public bool Wheel(int dx, int dy)
    {
        dx = Math.Clamp(dx, -1200, 1200);
        dy = Math.Clamp(dy, -1200, 1200);
        var list = new List<INPUT>(2);
        if (dy != 0) list.Add(Mouse(0, 0, MOUSEEVENTF_WHEEL, unchecked((uint)dy)));
        if (dx != 0) list.Add(Mouse(0, 0, MOUSEEVENTF_HWHEEL, unchecked((uint)dx)));
        return list.Count == 0 || Send([.. list]);
    }

    public bool Key(ushort vk, bool down)
    {
        if (vk is 0 or > 0xFE) return false;
        lock (_lock) { if (down) _pressedKeys.Add(vk); else _pressedKeys.Remove(vk); }
        var flags = (down ? 0 : KEYEVENTF_KEYUP) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        return Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags, dwExtraInfo = Marker } } });
    }

    /// <summary>Presses keys in order then releases in reverse (e.g. Ctrl+Shift+Esc).</summary>
    public bool Combo(IReadOnlyList<ushort> vks)
    {
        if (vks.Count is 0 or > 5) return false;
        var ok = true;
        foreach (var vk in vks) ok &= Key(vk, true);
        for (var i = vks.Count - 1; i >= 0; i--) ok &= Key(vks[i], false);
        return ok;
    }

    /// <summary>Types Unicode text (incl. Vietnamese, emoji via surrogate pairs) independent of keyboard layout/IME.</summary>
    public bool Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        if (text.Length > 4096) text = text[..4096];
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text.Normalize(System.Text.NormalizationForm.FormC))
        {
            if (ch == '\n')
            {
                inputs.Add(KeyInput(0x0D, false));
                inputs.Add(KeyInput(0x0D, true));
                continue;
            }
            if (ch == '\r') continue;
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE, dwExtraInfo = Marker } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, dwExtraInfo = Marker } } });
        }
        return Send([.. inputs]);
    }

    /// <summary>Releases everything this session pressed (called on disconnect so no key stays stuck).</summary>
    public void ReleaseAll()
    {
        ushort[] keys;
        MouseButton[] buttons;
        lock (_lock)
        {
            keys = [.. _pressedKeys];
            buttons = [.. _pressedButtons];
            _pressedKeys.Clear();
            _pressedButtons.Clear();
        }
        foreach (var k in keys) Send(KeyInput(k, true));
        foreach (var b in buttons)
        {
            Send(Mouse(0, 0, b switch { MouseButton.Left => MOUSEEVENTF_LEFTUP, MouseButton.Right => MOUSEEVENTF_RIGHTUP, _ => MOUSEEVENTF_MIDDLEUP }, 0));
        }
    }

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0), dwExtraInfo = Marker } },
    };

    private static INPUT Mouse(int x, int y, uint flags, uint data) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = x, dy = y, dwFlags = flags, mouseData = data, dwExtraInfo = Marker } },
    };

    private static bool IsExtended(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // PgUp PgDn End Home arrows
        or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5; // Ins Del LWin RWin Apps NumDiv NumLock RCtrl RAlt

    private static bool Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
}
