using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RemotePC.Core.Media;

namespace RemotePC.Platform.Windows.MediaFoundation;

public sealed record EncoderInfo(string Name, bool IsHardware, bool IsAsync, VideoCodec Codec);

/// <summary>
/// H.264 encoder on top of a Media Foundation transform. Supports synchronous (Microsoft software) and
/// asynchronous (hardware: Intel QSV, NVIDIA NVENC, AMD AMF) MFTs with system-memory NV12 input.
/// Not thread-safe: call from the capture thread only.
/// </summary>
public sealed class MfH264Encoder : IVideoEncoder
{
    private readonly IMFTransform _mft;
    private readonly IMFMediaEventGenerator? _events;
    private readonly ICodecAPI? _codec;
    private readonly IMFActivate _activate;
    private readonly int _fps;
    private readonly bool _providesSamples;
    private readonly uint _outputSize;
    private byte[]? _sequenceHeader;
    private int _inputCredits;
    private bool _disposed;
    private readonly Queue<long> _pendingCapture = new();

    public string Name { get; }
    public bool IsHardware { get; }
    public bool IsAsync { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Lists H.264 encoders available on this machine (hardware first).</summary>
    public static IReadOnlyList<EncoderInfo> Enumerate(VideoCodec codec = VideoCodec.H264)
    {
        MfNative.EnsureStarted();
        var list = new List<EncoderInfo>();
        foreach (var (act, hw, async) in EnumActivates(codec))
        {
            list.Add(new EncoderInfo(FriendlyName(act), hw, async, codec));
            Marshal.ReleaseComObject(act);
        }
        return list;
    }

    private static List<(IMFActivate Act, bool Hw, bool Async)> EnumActivates(VideoCodec codec)
    {
        var sub = codec switch { VideoCodec.Hevc => MfGuids.FormatHEVC, VideoCodec.Av1 => MfGuids.FormatAV1, _ => MfGuids.FormatH264 };
        var input = new MftRegisterTypeInfo { guidMajorType = MfGuids.MediaTypeVideo, guidSubtype = MfGuids.FormatNV12 };
        var output = new MftRegisterTypeInfo { guidMajorType = MfGuids.MediaTypeVideo, guidSubtype = sub };
        var result = new List<(IMFActivate, bool, bool)>();
        foreach (var (flags, hw, async) in new[]
                 {
                     (MfConst.MFT_ENUM_FLAG_HARDWARE | MfConst.MFT_ENUM_FLAG_ASYNCMFT | MfConst.MFT_ENUM_FLAG_SORTANDFILTER, true, true),
                     (MfConst.MFT_ENUM_FLAG_SYNCMFT | MfConst.MFT_ENUM_FLAG_SORTANDFILTER, false, false),
                 })
        {
            if (MfNative.MFTEnumEx(MfGuids.CategoryVideoEncoder, flags, input, output, out var arr, out var count) < 0 || arr == IntPtr.Zero) continue;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var p = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
                    var act = (IMFActivate)Marshal.GetObjectForIUnknown(p);
                    Marshal.Release(p);
                    result.Add((act, hw, async));
                }
            }
            finally { Marshal.FreeCoTaskMem(arr); }
        }
        return result;
    }

    private static string FriendlyName(IMFActivate act)
    {
        if (act.GetStringLength(MfGuids.FriendlyName, out var len) < 0) return "unknown";
        var sb = new StringBuilder((int)len + 1);
        return act.GetString(MfGuids.FriendlyName, sb, len + 1, out _) < 0 ? "unknown" : sb.ToString();
    }

    /// <summary>Creates the best available encoder; falls back from hardware to software on failure.</summary>
    public static MfH264Encoder Create(int width, int height, int fps, int kbps, bool preferHardware = true, Action<string>? log = null)
    {
        MfNative.EnsureStarted();
        var candidates = EnumActivates(VideoCodec.H264);
        if (!preferHardware) candidates = candidates.Where(c => !c.Hw).ToList();
        Exception? last = null;
        try
        {
            foreach (var (act, hw, async) in candidates)
            {
                var name = FriendlyName(act);
                try
                {
                    var enc = new MfH264Encoder(act, name, hw, async, width, height, fps, kbps);
                    log?.Invoke($"encoder selected: {name} (hardware={hw}, async={async}) {width}x{height}@{fps} {kbps}kbps");
                    candidates.Remove((act, hw, async));
                    return enc;
                }
                catch (Exception e) when (e is MediaFoundationException or COMException or InvalidCastException)
                {
                    log?.Invoke($"encoder {name} unavailable: {e.Message}");
                    last = e;
                }
            }
        }
        finally
        {
            foreach (var c in candidates) Marshal.ReleaseComObject(c.Act);
        }
        throw new NotSupportedException("No usable H.264 Media Foundation encoder. On Windows N editions install the Media Feature Pack.", last);
    }

    private MfH264Encoder(IMFActivate act, string name, bool hw, bool async, int width, int height, int fps, int kbps)
    {
        _activate = act;
        Name = name;
        IsHardware = hw;
        Width = width;
        Height = height;
        _fps = fps;
        MfNative.Check(act.ActivateObject(MfGuids.IID_IMFTransform, out var obj), "ActivateObject");
        _mft = (IMFTransform)obj;
        try
        {
            if (_mft.GetAttributes(out var attrs) >= 0)
            {
                if (attrs.GetUINT32(MfGuids.TransformAsync, out var isAsync) >= 0 && isAsync != 0)
                {
                    IsAsync = true;
                    attrs.SetUINT32(MfGuids.TransformAsyncUnlock, 1);
                }
                attrs.SetUINT32(MfGuids.LowLatency, 1);
                Marshal.ReleaseComObject(attrs);
            }
            if (IsAsync) _events = (IMFMediaEventGenerator)_mft;
            _codec = _mft as ICodecAPI;

            // Encoders require the output type before the input type.
            MfNative.Check(MfNative.MFCreateMediaType(out var outType), "MFCreateMediaType");
            outType.SetGUID(MfGuids.MT_MajorType, MfGuids.MediaTypeVideo);
            outType.SetGUID(MfGuids.MT_Subtype, MfGuids.FormatH264);
            outType.SetUINT32(MfGuids.MT_AvgBitrate, (uint)(kbps * 1000));
            outType.SetUINT64(MfGuids.MT_FrameSize, Pack(width, height));
            outType.SetUINT64(MfGuids.MT_FrameRate, Pack(fps, 1));
            outType.SetUINT64(MfGuids.MT_PixelAspectRatio, Pack(1, 1));
            outType.SetUINT32(MfGuids.MT_InterlaceMode, 2); // progressive
            outType.SetUINT32(MfGuids.MT_Mpeg2Profile, 66); // constrained baseline: universally decodable by mobile WebRTC
            MfNative.Check(_mft.SetOutputType(0, outType, 0), "SetOutputType");
            Marshal.ReleaseComObject(outType);

            MfNative.Check(MfNative.MFCreateMediaType(out var inType), "MFCreateMediaType");
            inType.SetGUID(MfGuids.MT_MajorType, MfGuids.MediaTypeVideo);
            inType.SetGUID(MfGuids.MT_Subtype, MfGuids.FormatNV12);
            inType.SetUINT64(MfGuids.MT_FrameSize, Pack(width, height));
            inType.SetUINT64(MfGuids.MT_FrameRate, Pack(fps, 1));
            inType.SetUINT64(MfGuids.MT_PixelAspectRatio, Pack(1, 1));
            inType.SetUINT32(MfGuids.MT_InterlaceMode, 2);
            MfNative.Check(_mft.SetInputType(0, inType, 0), "SetInputType");
            Marshal.ReleaseComObject(inType);

            ConfigureCodec(kbps);
            CacheSequenceHeader();

            _mft.GetOutputStreamInfo(0, out var info);
            _providesSamples = (info.dwFlags & (MfConst.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MfConst.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
            _outputSize = Math.Max(info.cbSize, (uint)(width * height * 3 / 2));

            MfNative.Check(_mft.ProcessMessage(MfConst.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero), "BEGIN_STREAMING");
            MfNative.Check(_mft.ProcessMessage(MfConst.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero), "START_OF_STREAM");
        }
        catch
        {
            Marshal.ReleaseComObject(_mft);
            throw;
        }
    }

    private static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    private void ConfigureCodec(int kbps)
    {
        if (_codec is null) return;
        SetCodec(MfGuids.CodecApi_RateControlMode, 0u); // CBR
        SetCodec(MfGuids.CodecApi_MeanBitRate, (uint)(kbps * 1000));
        SetCodec(MfGuids.CodecApi_GopSize, (uint)(_fps * 4)); // periodic IDR; PLI/FIR also force one
        SetCodec(MfGuids.CodecApi_BPictureCount, 0u);
        SetCodec(MfGuids.CodecApi_LowLatencyMode, true);
        SetCodec(MfGuids.CodecApi_QualityVsSpeed, 33u); // favour speed
    }

    private bool SetCodec(Guid api, object value)
    {
        if (_codec is null || _codec.IsSupported(api) != 0) return false;
        var v = value;
        return _codec.SetValue(api, ref v) >= 0;
    }

    private void CacheSequenceHeader()
    {
        if (_mft.GetOutputCurrentType(0, out var t) < 0) return;
        if (t.GetBlobSize(MfGuids.MT_MpegSequenceHeader, out var size) >= 0 && size > 0)
        {
            var buf = new byte[size];
            if (t.GetBlob(MfGuids.MT_MpegSequenceHeader, buf, size, out _) >= 0) _sequenceHeader = buf;
        }
        Marshal.ReleaseComObject(t);
    }

    public void SetBitrate(int kbps) => SetCodec(MfGuids.CodecApi_MeanBitRate, (uint)(kbps * 1000));

    public IReadOnlyList<EncodedFrame> Encode(ReadOnlySpan<byte> nv12, long timestamp100ns, long captureTicks, bool forceKeyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var output = new List<EncodedFrame>(1);
        var start = Stopwatch.GetTimestamp();
        if (forceKeyFrame) SetCodec(MfGuids.CodecApi_ForceKeyFrame, 1u);

        if (IsAsync)
        {
            PumpEvents(output, start, waitForOutput: false);
            if (_inputCredits <= 0) PumpEvents(output, start, waitForOutput: false, waitForInputMs: 30);
            if (_inputCredits <= 0) return output; // encoder busy: drop this frame (backpressure)
        }

        var sample = CreateInputSample(nv12, timestamp100ns);
        try
        {
            var hr = _mft.ProcessInput(0, sample, 0);
            if (hr == MfConst.MF_E_NOTACCEPTING && !IsAsync)
            {
                Drain(output, start);
                hr = _mft.ProcessInput(0, sample, 0);
            }
            MfNative.Check(hr, "ProcessInput");
            _pendingCapture.Enqueue(captureTicks);
            if (IsAsync) _inputCredits--;
        }
        finally { Marshal.ReleaseComObject(sample); }

        if (IsAsync) PumpEvents(output, start, waitForOutput: true);
        else Drain(output, start);
        return output;
    }

    private IMFSample CreateInputSample(ReadOnlySpan<byte> nv12, long ts)
    {
        MfNative.Check(MfNative.MFCreateMemoryBuffer((uint)nv12.Length, out var buffer), "MFCreateMemoryBuffer");
        buffer.Lock(out var ptr, out _, out _);
        unsafe { nv12.CopyTo(new Span<byte>((void*)ptr, nv12.Length)); }
        buffer.Unlock();
        buffer.SetCurrentLength((uint)nv12.Length);
        MfNative.Check(MfNative.MFCreateSample(out var sample), "MFCreateSample");
        sample.AddBuffer(buffer);
        Marshal.ReleaseComObject(buffer);
        sample.SetSampleTime(ts);
        sample.SetSampleDuration(10_000_000L / _fps);
        return sample;
    }

    private void PumpEvents(List<EncodedFrame> output, long start, bool waitForOutput, int waitForInputMs = 0)
    {
        var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * (waitForOutput ? 50 : waitForInputMs) / 1000);
        var gotOutput = false;
        while (true)
        {
            var hr = _events!.GetEvent(MfConst.MF_EVENT_FLAG_NO_WAIT, out var evt);
            if (hr == MfConst.MF_E_NO_EVENTS_AVAILABLE)
            {
                var done = waitForOutput ? gotOutput : (waitForInputMs == 0 || _inputCredits > 0);
                if (done || Stopwatch.GetTimestamp() > deadline) return;
                Thread.Sleep(1);
                continue;
            }
            MfNative.Check(hr, "GetEvent");
            evt.GetType(out var type);
            Marshal.ReleaseComObject(evt);
            if (type == MfConst.METransformNeedInput) _inputCredits++;
            else if (type == MfConst.METransformHaveOutput)
            {
                if (PullOne(output, start)) gotOutput = true;
            }
        }
    }

    private void Drain(List<EncodedFrame> output, long start)
    {
        while (PullOne(output, start)) { }
    }

    /// <summary>Calls ProcessOutput once. Returns false when the MFT needs more input.</summary>
    private bool PullOne(List<EncodedFrame> output, long start)
    {
        IMFSample? ownSample = null;
        var db = new MftOutputDataBuffer { dwStreamID = 0 };
        if (!_providesSamples)
        {
            MfNative.Check(MfNative.MFCreateMemoryBuffer(_outputSize, out var buf), "MFCreateMemoryBuffer");
            MfNative.Check(MfNative.MFCreateSample(out ownSample), "MFCreateSample");
            ownSample.AddBuffer(buf);
            Marshal.ReleaseComObject(buf);
            db.pSample = Marshal.GetIUnknownForObject(ownSample);
        }
        try
        {
            var hr = _mft.ProcessOutput(0, 1, ref db, out _);
            if (hr == MfConst.MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
            if (hr == MfConst.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                // Re-negotiate output type (rare for encoders); keep current settings.
                if (_mft.GetOutputAvailableType(0, 0, out var t) >= 0)
                {
                    _mft.SetOutputType(0, t, 0);
                    Marshal.ReleaseComObject(t);
                }
                CacheSequenceHeader();
                return true;
            }
            MfNative.Check(hr, "ProcessOutput");
            if (db.pSample == IntPtr.Zero) return true;
            var sample = (IMFSample)Marshal.GetObjectForIUnknown(db.pSample);
            sample.ConvertToContiguousBuffer(out var cbuf);
            cbuf.Lock(out var ptr, out _, out var len);
            var data = new byte[len];
            Marshal.Copy(ptr, data, 0, (int)len);
            cbuf.Unlock();
            Marshal.ReleaseComObject(cbuf);
            if (!ReferenceEquals(sample, ownSample)) Marshal.ReleaseComObject(sample);

            var key = H264.IsKeyFrame(data);
            if (key && _sequenceHeader is { Length: > 0 } && !H264.NalTypes(data).Contains(H264.NalSps))
                data = [.. _sequenceHeader, .. data];
            var capture = _pendingCapture.Count > 0 ? _pendingCapture.Dequeue() : start;
            output.Add(new EncodedFrame(data, key, capture, start, Stopwatch.GetTimestamp(), Width, Height));
            return true;
        }
        finally
        {
            if (db.pSample != IntPtr.Zero) Marshal.Release(db.pSample);
            if (db.pEvents != IntPtr.Zero) Marshal.Release(db.pEvents);
            if (ownSample is not null) Marshal.ReleaseComObject(ownSample);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mft.ProcessMessage(MfConst.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
        _mft.ProcessMessage(MfConst.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
        _activate.ShutdownObject();
        Marshal.ReleaseComObject(_mft);
        Marshal.ReleaseComObject(_activate);
    }
}
