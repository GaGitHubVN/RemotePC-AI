using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemotePC.Core.Logging;

public sealed record ActivityEntry(DateTimeOffset Time, string Category, string Message, string? DeviceId = null);

/// <summary>
/// Structured activity/audit log: in-memory ring buffer for the UI plus a daily JSONL file.
/// Every message passes through <see cref="Redact"/> so tokens, keys and secrets never reach disk.
/// Retention: files older than <see cref="RetentionDays"/> are deleted on start-up.
/// </summary>
public sealed partial class ActivityLog
{
    private readonly ConcurrentQueue<ActivityEntry> _recent = new();
    private readonly string? _dir;
    private readonly Lock _fileLock = new();
    public int RetentionDays { get; set; } = 14;
    public event Action<ActivityEntry>? Added;

    public ActivityLog(string? directory)
    {
        _dir = directory;
        if (_dir is not null)
        {
            Directory.CreateDirectory(_dir);
            ApplyRetention();
        }
    }

    public IReadOnlyList<ActivityEntry> Recent => _recent.ToArray();

    public void Write(string category, string message, string? deviceId = null)
    {
        var e = new ActivityEntry(DateTimeOffset.Now, category, Redact(message), deviceId);
        _recent.Enqueue(e);
        while (_recent.Count > 2000) _recent.TryDequeue(out _);
        if (_dir is not null)
        {
            var line = JsonSerializer.Serialize(e) + "\n";
            lock (_fileLock)
            {
                try { File.AppendAllText(Path.Combine(_dir, $"activity-{e.Time:yyyy-MM-dd}.jsonl"), line); }
                catch (IOException) { }
            }
        }
        Added?.Invoke(e);
    }

    [GeneratedRegex(@"(?i)(token|secret|password|passwd|pwd|credential|api[_-]?key|authorization|sig|proof|key)(\""?\s*[:=]\s*\""?)((?:bearer|basic)\s+[^\s"",}]+|[^\s"",}]+)")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"[A-Za-z0-9_\-]{40,}")]
    private static partial Regex LongOpaque();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9._\-]+")]
    private static partial Regex Bearer();

    /// <summary>Removes values that look like secrets (assignments, bearer tokens, long opaque strings).</summary>
    public static string Redact(string s)
    {
        s = SecretAssignment().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + "[REDACTED]");
        s = Bearer().Replace(s, "Bearer [REDACTED]");
        s = LongOpaque().Replace(s, "[REDACTED]");
        return s;
    }

    public void ApplyRetention()
    {
        if (_dir is null) return;
        foreach (var f in Directory.EnumerateFiles(_dir, "activity-*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-RetentionDays))
            {
                try { File.Delete(f); } catch (IOException) { }
            }
        }
    }

    public void Clear()
    {
        while (_recent.TryDequeue(out _)) { }
        if (_dir is null) return;
        lock (_fileLock)
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "activity-*.jsonl"))
            {
                try { File.Delete(f); } catch (IOException) { }
            }
        }
    }

    /// <summary>Exports all retained (already redacted) entries into one file.</summary>
    public void Export(string path)
    {
        if (_dir is null) { File.WriteAllLines(path, _recent.Select(e => JsonSerializer.Serialize(e))); return; }
        using var w = File.CreateText(path);
        lock (_fileLock)
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "activity-*.jsonl").Order()) w.Write(File.ReadAllText(f));
        }
    }
}
