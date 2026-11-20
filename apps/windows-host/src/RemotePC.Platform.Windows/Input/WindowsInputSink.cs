using System.Runtime.InteropServices;
using RemotePC.Core.Input;
using RemotePC.Core.Session;

namespace RemotePC.Platform.Windows.Input;

public sealed class WindowsInputSink : IInputSink
{
    private readonly InputInjector _inj = new();

    public bool Move(double nx, double ny, PixelRect monitor) => _inj.MoveNormalized(nx, ny, monitor);

    public bool Button(string button, bool down, double nx, double ny, PixelRect monitor) =>
        _inj.Button(button switch { "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => MouseButton.Left }, down, nx, ny, monitor);

    public bool Wheel(int dx, int dy) => _inj.Wheel(dx, dy);
    public bool Key(ushort vk, bool down) => _inj.Key(vk, down);
    public bool Combo(IReadOnlyList<ushort> vks) => _inj.Combo(vks);
    public bool Text(string text) => _inj.Text(text);
    public void ReleaseAll() => _inj.ReleaseAll();
}

/// <summary>Win32 Unicode text clipboard. Retries briefly because another app may hold the clipboard open.</summary>
public sealed class WindowsClipboard : IClipboard
{
    private const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);

    public const int MaxChars = 1_000_000;

    private static bool Open()
    {
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    public string? GetText()
    {
        if (!Open()) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            try { var s = Marshal.PtrToStringUni(p); return s is { Length: > MaxChars } ? s[..MaxChars] : s; }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public bool SetText(string text)
    {
        if (text.Length > MaxChars) return false;
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (h == IntPtr.Zero) return false;
            var p = GlobalLock(h);
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(h);
            if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) { GlobalFree(h); return false; }
            return true; // ownership transferred to the system
        }
        finally { CloseClipboard(); }
    }
}
