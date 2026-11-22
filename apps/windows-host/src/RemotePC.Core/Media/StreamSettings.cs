namespace RemotePC.Core.Media;

public enum QualityPreset { Auto, DataSaver, Balanced, HighQuality, LowLatency, Custom }

public enum VideoCodec { H264, Hevc, Av1 }

public sealed record StreamSettings
{
    public QualityPreset Preset { get; init; } = QualityPreset.Auto;
    public int MaxWidth { get; init; } = 1920;
    public int MaxHeight { get; init; } = 1080;
    public int Fps { get; init; } = 60;
    public int TargetKbps { get; init; } = 6000;
    public int MaxKbps { get; init; } = 12000;
    public int MinKbps { get; init; } = 300;
    public VideoCodec Codec { get; init; } = VideoCodec.H264;
    public bool RenderCursor { get; init; } = true;
    public bool Audio { get; init; }

    public static StreamSettings For(QualityPreset p) => p switch
    {
        QualityPreset.DataSaver => new() { Preset = p, MaxWidth = 1280, MaxHeight = 720, Fps = 20, TargetKbps = 800, MaxKbps = 1500, MinKbps = 200 },
        QualityPreset.Balanced => new() { Preset = p, MaxWidth = 1600, MaxHeight = 900, Fps = 60, TargetKbps = 4000, MaxKbps = 8000 },
        QualityPreset.HighQuality => new() { Preset = p, MaxWidth = 2560, MaxHeight = 1440, Fps = 30, TargetKbps = 8000, MaxKbps = 16000, MinKbps = 1000 },
        QualityPreset.LowLatency => new() { Preset = p, MaxWidth = 1280, MaxHeight = 720, Fps = 60, TargetKbps = 3000, MaxKbps = 6000 },
        QualityPreset.Custom => new() { Preset = p },
        _ => new() { Preset = QualityPreset.Auto },
    };

    /// <summary>Clamps client-supplied custom values to safe ranges.</summary>
    public StreamSettings Sanitized() => this with
    {
        MaxWidth = Math.Clamp(MaxWidth, 320, 3840),
        MaxHeight = Math.Clamp(MaxHeight, 240, 2160),
        Fps = Math.Clamp(Fps, 5, 60),
        MinKbps = Math.Clamp(MinKbps, 100, 50_000),
        TargetKbps = Math.Clamp(TargetKbps, Math.Clamp(MinKbps, 100, 50_000), 50_000),
        MaxKbps = Math.Clamp(MaxKbps, Math.Clamp(TargetKbps, 100, 50_000), 80_000),
    };
}

/// <summary>
/// Loss/RTT-driven bitrate controller (AIMD), fed from RTCP receiver reports. Pure logic, unit tested.
/// </summary>
public sealed class AdaptiveBitrateController(StreamSettings settings)
{
    private double _kbps = settings.TargetKbps;
    private double _baselineRttMs = double.NaN;

    public int CurrentKbps => (int)_kbps;

    /// <summary>Recommended fps given current bitrate (lower fps before dropping resolution).</summary>
    public int RecommendedFps
    {
        get
        {
            if (settings.Preset == QualityPreset.LowLatency) return settings.Fps;
            var ratio = _kbps / settings.TargetKbps;
            return ratio < 0.35 ? Math.Max(10, settings.Fps / 2) : settings.Fps;
        }
    }

    /// <param name="fractionLost">0..1 from RTCP RR.</param>
    /// <param name="rttMs">round-trip time from RTCP, NaN if unknown.</param>
    /// <returns>true if the bitrate changed by more than 5 %.</returns>
    public bool OnReport(double fractionLost, double rttMs)
    {
        var before = _kbps;
        if (!double.IsNaN(rttMs))
            _baselineRttMs = double.IsNaN(_baselineRttMs) ? rttMs : Math.Min(_baselineRttMs * 1.01, rttMs * 0.1 + _baselineRttMs * 0.9);

        var queueing = !double.IsNaN(rttMs) && !double.IsNaN(_baselineRttMs) && rttMs > _baselineRttMs + 120;
        if (fractionLost > 0.10) _kbps *= 1 - (0.5 * fractionLost);          // heavy loss: multiplicative decrease
        else if (fractionLost > 0.02 || queueing) _kbps *= 0.85;             // mild loss or growing delay
        else _kbps = Math.Min(_kbps * 1.08 + 50, settings.MaxKbps);          // probe up

        _kbps = Math.Clamp(_kbps, settings.MinKbps, settings.MaxKbps);
        return Math.Abs(_kbps - before) / before > 0.05;
    }
}
