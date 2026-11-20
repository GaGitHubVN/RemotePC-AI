using System.Diagnostics;
using RemotePC.Core.Input;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemotePC.Platform.Windows.Capture;

public sealed record MonitorInfo(int Index, string DeviceName, PixelRect Bounds, bool Primary, string AdapterName, int RotationDegrees);

public enum CaptureResult { NewFrame, NoChange, AccessLost }

/// <summary>
/// DXGI Desktop Duplication for one monitor. Produces BGRA frames in a staging texture mapped for CPU read.
/// Handles access loss (mode change, UAC secure desktop, fullscreen app switch) by reporting
/// <see cref="CaptureResult.AccessLost"/>; the caller recreates the duplicator.
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _dup;
    private ID3D11Texture2D? _staging;
    private bool _haveFrame;

    public MonitorInfo Monitor { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Cursor position relative to the monitor (pixels), or null when hidden/unknown.</summary>
    public (int X, int Y)? CursorPosition { get; private set; }
    public long LastFrameTicks { get; private set; }

    public static IReadOnlyList<MonitorInfo> ListMonitors()
    {
        var list = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var idx = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                var adapterName = adapter.Description1.Description;
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        if (!d.AttachedToDesktop) continue;
                        var r = d.DesktopCoordinates;
                        list.Add(new MonitorInfo(idx++, d.DeviceName, new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                            r.Left == 0 && r.Top == 0, adapterName, RotationDegrees(d.Rotation)));
                    }
                }
            }
        }
        return list;
    }

    private static int RotationDegrees(ModeRotation r) => r switch
    {
        ModeRotation.Rotate90 => 90,
        ModeRotation.Rotate180 => 180,
        ModeRotation.Rotate270 => 270,
        _ => 0,
    };

    public DesktopDuplicator(int monitorIndex)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var idx = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                var d = output.Description;
                if (!d.AttachedToDesktop) { output.Dispose(); continue; }
                if (idx++ != monitorIndex) { output.Dispose(); continue; }

                var r = d.DesktopCoordinates;
                Monitor = new MonitorInfo(monitorIndex, d.DeviceName, new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                    r.Left == 0 && r.Top == 0, adapter.Description1.Description, RotationDegrees(d.Rotation));
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1], out _device!, out _context!).CheckError();
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                _dup = output1.DuplicateOutput(_device);
                var dd = _dup.Description.ModeDescription;
                Width = (int)dd.Width;
                Height = (int)dd.Height;
                output.Dispose();
                adapter.Dispose();
                return;
            }
            adapter.Dispose();
        }
        throw new ArgumentOutOfRangeException(nameof(monitorIndex), "monitor not found");
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a new desktop image. On <see cref="CaptureResult.NewFrame"/>
    /// the staging texture holds the latest image; call <see cref="MapFrame"/> to read it.
    /// </summary>
    public CaptureResult Acquire(int timeoutMs)
    {
        var hr = _dup.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
        if (hr == Vortice.DXGI.ResultCode.WaitTimeout) return CaptureResult.NoChange;
        if (hr == Vortice.DXGI.ResultCode.AccessLost || hr == Vortice.DXGI.ResultCode.InvalidCall) return CaptureResult.AccessLost;
        hr.CheckError();
        try
        {
            if (info.PointerPosition.Visible) CursorPosition = (info.PointerPosition.Position.X, info.PointerPosition.Position.Y);
            else if (info.LastMouseUpdateTime != 0) CursorPosition = null;

            if (info.LastPresentTime == 0 && _haveFrame) return CaptureResult.NoChange; // pointer-only update
            using var tex = resource!.QueryInterface<ID3D11Texture2D>();
            if (_staging is null)
            {
                var desc = tex.Description;
                desc.Usage = ResourceUsage.Staging;
                desc.BindFlags = BindFlags.None;
                desc.CPUAccessFlags = CpuAccessFlags.Read;
                desc.MiscFlags = ResourceOptionFlags.None;
                desc.MipLevels = 1;
                desc.ArraySize = 1;
                _staging = _device.CreateTexture2D(desc);
            }
            _context.CopyResource(_staging, tex);
            _haveFrame = true;
            LastFrameTicks = Stopwatch.GetTimestamp();
            return CaptureResult.NewFrame;
        }
        finally
        {
            resource?.Dispose();
            _dup.ReleaseFrame();
        }
    }

    public bool HasFrame => _haveFrame;

    /// <summary>Maps the staging texture and invokes <paramref name="reader"/> with BGRA data and row pitch.</summary>
    public void MapFrame(ReadOnlySpanAction<byte, int> reader)
    {
        if (_staging is null) throw new InvalidOperationException("no frame captured yet");
        var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                var span = new ReadOnlySpan<byte>((void*)mapped.DataPointer, (int)(mapped.RowPitch * Height));
                reader(span, (int)mapped.RowPitch);
            }
        }
        finally { _context.Unmap(_staging, 0); }
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _dup.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}

public delegate void ReadOnlySpanAction<T, in TArg>(ReadOnlySpan<T> span, TArg arg);
