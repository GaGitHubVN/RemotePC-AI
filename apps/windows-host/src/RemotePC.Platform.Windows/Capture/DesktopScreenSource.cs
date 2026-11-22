using System.Diagnostics;
using RemotePC.Core.Media;
using RemotePC.Core.Session;
using RemotePC.Platform.Windows.MediaFoundation;

namespace RemotePC.Platform.Windows.Capture;

/// <summary>Production screen source: DXGI duplication → NV12 → Media Foundation H.264.</summary>
public sealed class DesktopScreenSource(Action<string>? log = null) : IScreenSource
{
    public IReadOnlyList<DisplayDesc> Displays() =>
        DesktopDuplicator.ListMonitors().Select(m => new DisplayDesc(m.Index, m.DeviceName, m.Bounds, m.Primary, m.RotationDegrees)).ToList();

    public IScreenPipeline Start(int displayIndex, StreamSettings settings, Action<EncodedFrame> onFrame) =>
        new Pipeline(displayIndex, settings, onFrame, log);

    public byte[] CapturePng(int displayIndex, int maxWidth = 1920)
    {
        using var dup = new DesktopDuplicator(displayIndex);
        for (var i = 0; i < 20 && !dup.HasFrame; i++)
        {
            if (dup.Acquire(100) == CaptureResult.AccessLost)
                throw new InvalidOperationException("The desktop cannot be captured right now (secure desktop, lock screen or display change).");
        }
        if (!dup.HasFrame) throw new TimeoutException("no desktop frame available");
        byte[] png = [];
        dup.MapFrame((span, pitch) => png = Png.FromBgra(span, dup.Width, dup.Height, pitch, maxWidth));
        return png;
    }

    /// <summary>
    /// Two threads so the stages overlap instead of adding up: the capture thread acquires the desktop image and
    /// converts it to NV12 while the encode thread compresses the previous frame. Only the newest converted frame is
    /// kept; if the encoder falls behind, older ones are dropped rather than queued (a queue would only add latency).
    /// </summary>
    private sealed class Pipeline : IScreenPipeline
    {
        private sealed class Nv12Frame(int w, int h)
        {
            public readonly byte[] Data = new byte[ColorConvert.Nv12Size(w, h)];
            public readonly int Width = w, Height = h;
            public long CaptureTicks;
            public TimeSpan At;
        }

        private readonly Thread _captureThread, _encodeThread;
        private readonly Action<EncodedFrame> _onFrame;
        private readonly Action<string>? _log;
        private readonly StreamSettings _settings;
        private readonly object _gate = new();
        private readonly Stack<Nv12Frame> _pool = new();
        private readonly AutoResetEvent _ready = new(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Nv12Frame? _pending;
        private volatile bool _running = true;
        private volatile bool _keyRequested = true;
        private volatile int _fps;
        private volatile int _kbps;
        private long _sent, _dropped;
        private double _captureMs, _convertMs, _encodeMs, _measuredFps;
        private string _encoderName = "";
        private bool _hardware;
        private int _w, _h;
        private int _disposed;

        public int DisplayIndex { get; }

        public Pipeline(int display, StreamSettings settings, Action<EncodedFrame> onFrame, Action<string>? log)
        {
            DisplayIndex = display;
            _settings = settings.Sanitized();
            _fps = _settings.Fps;
            _kbps = _settings.TargetKbps;
            _onFrame = onFrame;
            _log = log;
            // 1 ms scheduler resolution while streaming (the default 15.6 ms makes every short wait in the capture
            // loop and the async encoder cost a whole tick). Released in Dispose.
            _ = TimeBeginPeriod(1);
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = $"capture-{display}", Priority = ThreadPriority.AboveNormal };
            _encodeThread = new Thread(EncodeLoop) { IsBackground = true, Name = $"encode-{display}", Priority = ThreadPriority.AboveNormal };
            _encodeThread.Start();
            _captureThread.Start();
        }

        public StreamStats Stats => new((int)Math.Round(_measuredFps), _kbps, _w, _h, _encoderName, _hardware,
            _captureMs, _convertMs, _encodeMs, Interlocked.Read(ref _sent), Interlocked.Read(ref _dropped));

        public void RequestKeyFrame() => _keyRequested = true;
        public void SetBitrate(int kbps) => _kbps = Math.Clamp(kbps, _settings.MinKbps, _settings.MaxKbps);
        public void SetFps(int fps) => _fps = Math.Clamp(fps, 5, _settings.Fps);

        private Nv12Frame Rent(int w, int h)
        {
            lock (_gate)
            {
                while (_pool.TryPop(out var f))
                {
                    if (f.Width == w && f.Height == h) return f;
                }
            }
            return new Nv12Frame(w, h);
        }

        private void Return(Nv12Frame f)
        {
            lock (_gate)
            {
                if (_pool.Count < 3) _pool.Push(f);
            }
        }

        private void CaptureLoop()
        {
            DesktopDuplicator? dup = null;
            var slot = TimeSpan.Zero; // time of the last converted frame, advanced on an ideal 1/fps grid
            var dirty = false;
            try
            {
                while (_running)
                {
                    if (dup is null)
                    {
                        try { dup = new DesktopDuplicator(DisplayIndex); _keyRequested = true; }
                        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or ArgumentOutOfRangeException)
                        {
                            _log?.Invoke($"capture unavailable ({e.Message}); retrying");
                            Thread.Sleep(500);
                            continue;
                        }
                    }
                    var interval = TimeSpan.FromSeconds(1.0 / _fps);
                    // Pacing without added delay: while a fresh image is waiting, only wait until the next send slot
                    // (taking any newer image meanwhile); otherwise wait for the desktop to change.
                    var untilSlot = interval - (_clock.Elapsed - slot);
                    var timeout = dirty ? Math.Max(0, (int)untilSlot.TotalMilliseconds) : Math.Max(1, (int)interval.TotalMilliseconds);
                    var t0 = Stopwatch.GetTimestamp();
                    var r = dup.Acquire(timeout);
                    if (r == CaptureResult.AccessLost)
                    {
                        dup.Dispose();
                        dup = null;
                        continue;
                    }
                    if (r == CaptureResult.NewFrame)
                    {
                        dirty = true;
                        _captureMs = Ema(_captureMs, Ms(t0));
                    }

                    var now = _clock.Elapsed;
                    var refresh = now - slot > TimeSpan.FromSeconds(1); // keep decoders and RTCP alive on a static screen
                    if (!dup.HasFrame || (!dirty && !refresh && !_keyRequested)) continue;
                    if (now - slot < interval * 0.95) continue; // early: the next pass waits only for the remaining time

                    var (w, h) = ColorConvert.FitEven(dup.Width, dup.Height, _settings.MaxWidth, _settings.MaxHeight);
                    var frame = Rent(w, h);
                    var t1 = Stopwatch.GetTimestamp();
                    dup.MapFrame((span, pitch) => ColorConvert.BgraToNv12(span, dup.Width, dup.Height, pitch, frame.Data, w, h));
                    _convertMs = Ema(_convertMs, Ms(t1));
                    frame.CaptureTicks = dup.LastFrameTicks;
                    frame.At = now;
                    Nv12Frame? stale;
                    lock (_gate)
                    {
                        stale = _pending;
                        _pending = frame;
                    }
                    if (stale is not null) { Interlocked.Increment(ref _dropped); Return(stale); } // encoder still busy
                    _ready.Set();
                    // Advance on the ideal grid so wake-up jitter does not lower the rate; resync after a pause.
                    slot = now - slot < interval * 2 ? slot + interval : now;
                    if (slot > now) slot = now;
                    dirty = false;
                }
            }
            catch (Exception e)
            {
                _log?.Invoke($"capture pipeline stopped: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                dup?.Dispose();
                _running = false;
                _ready.Set();
            }
        }

        private void EncodeLoop()
        {
            MfH264Encoder? enc = null;
            var appliedKbps = 0;
            var fpsWindowStart = _clock.Elapsed;
            var fpsCount = 0;
            try
            {
                while (_running)
                {
                    _ready.WaitOne(250);
                    Nv12Frame? frame;
                    lock (_gate)
                    {
                        frame = _pending;
                        _pending = null;
                    }
                    if (frame is null) continue;
                    try
                    {
                        if (enc is null || enc.Width != frame.Width || enc.Height != frame.Height)
                        {
                            enc?.Dispose();
                            enc = MfH264Encoder.Create(frame.Width, frame.Height, _settings.Fps, _kbps, preferHardware: true, log: _log);
                            appliedKbps = _kbps;
                            _encoderName = enc.Name;
                            _hardware = enc.IsHardware;
                            _w = frame.Width;
                            _h = frame.Height;
                            _keyRequested = true;
                        }
                        if (appliedKbps != _kbps) { enc.SetBitrate(_kbps); appliedKbps = _kbps; }

                        var key = _keyRequested;
                        _keyRequested = false;
                        var t2 = Stopwatch.GetTimestamp();
                        var frames = enc.Encode(frame.Data, (long)(frame.At.TotalMilliseconds * 10_000), frame.CaptureTicks, key);
                        _encodeMs = Ema(_encodeMs, Ms(t2));
                        if (frames.Count == 0) { Interlocked.Increment(ref _dropped); if (key) _keyRequested = true; continue; }
                        foreach (var f in frames) _onFrame(f);
                        Interlocked.Add(ref _sent, frames.Count);
                        fpsCount += frames.Count;
                        var now = _clock.Elapsed;
                        if (now - fpsWindowStart > TimeSpan.FromSeconds(1))
                        {
                            _measuredFps = fpsCount / (now - fpsWindowStart).TotalSeconds;
                            fpsCount = 0;
                            fpsWindowStart = now;
                        }
                    }
                    finally { Return(frame); }
                }
            }
            catch (Exception e)
            {
                _log?.Invoke($"encoder stopped: {e.GetType().Name}: {e.Message}");
                _running = false;
            }
            finally
            {
                enc?.Dispose();
            }
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);

        private static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        private static double Ema(double prev, double v) => prev == 0 ? v : (prev * 0.9) + (v * 0.1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _running = false;
            _ready.Set();
            var joined = Thread.CurrentThread == _captureThread || _captureThread.Join(2000);
            joined &= Thread.CurrentThread == _encodeThread || _encodeThread.Join(2000);
            _ = TimeEndPeriod(1);
            if (joined) _ready.Dispose(); // a thread that did not stop in time may still touch it
        }
    }
}
