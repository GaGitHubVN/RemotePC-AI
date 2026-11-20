namespace RemotePC.Core.Input;

/// <summary>A monitor rectangle in physical pixels on the virtual desktop (may have negative origin).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Maps normalized [0,1] coordinates inside the streamed monitor frame to Windows SendInput absolute
/// coordinates (0..65535 across the whole virtual desktop, MOUSEEVENTF_VIRTUALDESK).
/// The client is responsible for removing letterboxing and viewport zoom before normalizing (PROTOCOL.md §5.1).
/// The host process is per-monitor DPI aware v2, so all rectangles are physical pixels.
/// </summary>
public static class CoordinateMapper
{
    public static (int X, int Y) ToPixel(double nx, double ny, PixelRect monitor)
    {
        if (double.IsNaN(nx) || double.IsNaN(ny)) throw new ArgumentException("coordinate is NaN");
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        var px = monitor.X + (int)Math.Min(monitor.Width - 1, Math.Floor(nx * monitor.Width));
        var py = monitor.Y + (int)Math.Min(monitor.Height - 1, Math.Floor(ny * monitor.Height));
        return (px, py);
    }

    public static (int X, int Y) ToAbsolute(double nx, double ny, PixelRect monitor, PixelRect virtualDesktop)
    {
        var (px, py) = ToPixel(nx, ny, monitor);
        return PixelToAbsolute(px, py, virtualDesktop);
    }

    /// <summary>Pixel center → 0..65535, matching how Windows maps absolute input back to pixels.</summary>
    public static (int X, int Y) PixelToAbsolute(int px, int py, PixelRect vd)
    {
        var ax = (int)Math.Round(((px - vd.X) + 0.5) * 65536.0 / vd.Width);
        var ay = (int)Math.Round(((py - vd.Y) + 0.5) * 65536.0 / vd.Height);
        return (Math.Clamp(ax, 0, 65535), Math.Clamp(ay, 0, 65535));
    }

    /// <summary>Inverse used by tests: absolute → pixel the way Windows resolves it.</summary>
    public static (int X, int Y) AbsoluteToPixel(int ax, int ay, PixelRect vd) =>
        (vd.X + (int)Math.Floor(ax * (double)vd.Width / 65536.0), vd.Y + (int)Math.Floor(ay * (double)vd.Height / 65536.0));
}
