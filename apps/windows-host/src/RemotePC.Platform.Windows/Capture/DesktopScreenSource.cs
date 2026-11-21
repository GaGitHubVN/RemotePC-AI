using System.Diagnostics;
using RemotePC.Core.Media;
using RemotePC.Core.Session;
using RemotePC.Platform.Windows.MediaFoundation;

namespace RemotePC.Platform.Windows.Capture;

/// <summary>Production screen source: DXGI duplication → NV12 → Media Foundation H.264, one thread per stream.</summary>
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

    private sealed class Pipeline : IScreenPipeline
    {
        private readonly Thread _thread;
        private readonly Action<EncodedFrame> _onFrame;
        private readonly Action<string>? _log;
        private readonly StreamSettings _settings;
        private volatile bool _running = true;
        private volatile bool _keyRequested = true;
        private volatile int _fps;
        private volatile int _kbps;
        private int _appliedKbps;
        private long _sent, _dropped;
        private double _captureMs, _convertMs, _encodeMs, _measuredFps;
        private string _encoderName = "";
        private bool _hardware;
        private int _w, _h;

        public int DisplayIndex { get; }

        public Pipeline(int display, StreamSettings settings, Action<EncodedFrame> onFrame, Action<string>? log)
        {
            DisplayIndex = display;
            _settings = settings.Sanitized();
            _fps = _settings.Fps;
            _kbps = _settings.TargetKbps;
            _onFrame = onFrame;
            _log = log;
            _thread = new Thread(Run) { IsBackground = true, Name = $"capture-{display}", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public StreamStats Stats => new((int)Math.Round(_measuredFps), _kbps, _w, _h, _encoderName, _hardware,
            _captureMs, _convertMs, _encodeMs, Interlocked.Read(ref _sent), Interlocked.Read(ref _dropped));

        public void RequestKeyFrame() => _keyRequested = true;
        public void SetBitrate(int kbps) => _kbps = Math.Clamp(kbps, _settings.MinKbps, _settings.MaxKbps);
        public void SetFps(int fps) => _fps = Math.Clamp(fps, 5, _settings.Fps);

        private void Run()
        {
            DesktopDuplicator? dup = null;
            MfH264Encoder? enc = null;
            byte[] nv12 = [];
            var clock = Stopwatch.StartNew();
            var lastSent = TimeSpan.Zero;
            var dirty = false;
            var fpsWindowStart = clock.Elapsed;
            var fpsCount = 0;
            // 1 ms scheduler resolution for this pipeline (the default 15.6 ms makes every Sleep(1)/wait in the
            // capture + async encoder loop cost a whole tick). Released in finally.
            _ = TimeBeginPeriod(1);
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
                    var t0 = Stopwatch.GetTimestamp();
                    var r = dup.Acquire(Math.Max(1, (int)interval.TotalMilliseconds));
                    if (r == CaptureResult.AccessLost)
                    {
                        dup.Dispose();
                        dup = null;
                        continue;
                    }
                    if (r == CaptureResult.NewFrame) { dirty = true; _captureMs = Ema(_captureMs, Ms(t0)); }

                    var now = clock.Elapsed;
                    var refresh = now - lastSent > TimeSpan.FromSeconds(1); // keep decoders and RTCP alive on a static screen
                    if (!dup.HasFrame || (!dirty && !refresh && !_keyRequested)) continue;
                    if (now - lastSent < interval * 0.9) { Interlocked.Increment(ref _dropped); continue; } // pacing

                    var (w, h) = ColorConvert.FitEven(dup.Width, dup.Height, _settings.MaxWidth, _settings.MaxHeight);
                    if (enc is null || enc.Width != w || enc.Height != h)
                    {
                        enc?.Dispose();
                        enc = MfH264Encoder.Create(w, h, _settings.Fps, _kbps, preferHardware: true, log: _log);
                        _appliedKbps = _kbps;
                        _encoderName = enc.Name;
                        _hardware = enc.IsHardware;
                        _w = w;
                        _h = h;
                        nv12 = new byte[ColorConvert.Nv12Size(w, h)];
                        _keyRequested = true;
                    }
                    if (_appliedKbps != _kbps) { enc.SetBitrate(_kbps); _appliedKbps = _kbps; }

                    var t1 = Stopwatch.GetTimestamp();
                    dup.MapFrame((span, pitch) => ColorConvert.BgraToNv12(span, dup.Width, dup.Height, pitch, nv12, w, h));
                    _convertMs = Ema(_convertMs, Ms(t1));
                    var key = _keyRequested;
                    _keyRequested = false;
                    var t2 = Stopwatch.GetTimestamp();
                    var frames = enc.Encode(nv12, (long)(now.TotalMilliseconds * 10_000), t0, key);
                    _encodeMs = Ema(_encodeMs, Ms(t2));
                    if (frames.Count == 0) { Interlocked.Increment(ref _dropped); if (key) _keyRequested = true; continue; }
                    foreach (var f in frames) _onFrame(f);
                    Interlocked.Add(ref _sent, frames.Count);
                    lastSent = now;
                    dirty = false;
                    fpsCount += frames.Count;
                    if (now - fpsWindowStart > TimeSpan.FromSeconds(1))
                    {
                        _measuredFps = fpsCount / (now - fpsWindowStart).TotalSeconds;
                        fpsCount = 0;
                        fpsWindowStart = now;
                    }
                }
            }
            catch (Exception e)
            {
                _log?.Invoke($"capture pipeline stopped: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                _ = TimeEndPeriod(1);
                enc?.Dispose();
                dup?.Dispose();
            }
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);

        private static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        private static double Ema(double prev, double v) => prev == 0 ? v : (prev * 0.9) + (v * 0.1);

        public void Dispose()
        {
            _running = false;
            if (Thread.CurrentThread != _thread) _thread.Join(2000);
        }
    }
}
