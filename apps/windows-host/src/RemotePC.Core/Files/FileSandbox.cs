namespace RemotePC.Core.Files;

public sealed record SandboxRoot(string Alias, string FullPath, bool Writable);

public sealed class SandboxException(string message) : Exception(message);

/// <summary>
/// Maps virtual paths like "Desktop/Homework/a.txt" onto a fixed set of root folders chosen by the PC owner,
/// rejecting traversal ("..", absolute paths), alternate data streams, reserved device names and any
/// symlink/junction inside the tree (which could point outside the root).
/// </summary>
public sealed class FileSandbox(IReadOnlyList<SandboxRoot> roots)
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$",
    };

    public IReadOnlyList<SandboxRoot> Roots { get; } = roots
        .Select(r => r with { FullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.FullPath)) })
        .ToList();

    public static IReadOnlyList<SandboxRoot> DefaultRoots(string receivedFolder) =>
    [
        new("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), true),
        new("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), true),
        new("Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), true),
        new("Received", receivedFolder, true),
    ];

    public sealed record Resolved(SandboxRoot Root, string FullPath, string VirtualPath);

    /// <summary>Validates a single file/folder name (no separators).</summary>
    public static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 200) throw new SandboxException("name must be 1–200 characters");
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(':') || name.Contains('/') || name.Contains('\\'))
            throw new SandboxException($"invalid name '{name}'");
        if (name.EndsWith('.') || name.EndsWith(' ')) throw new SandboxException("name cannot end with a dot or space");
        var stem = name.Split('.')[0];
        if (Reserved.Contains(stem)) throw new SandboxException($"'{name}' is a reserved Windows device name");
        return name;
    }

    public Resolved Resolve(string virtualPath, bool forWrite = false)
    {
        var parts = (virtualPath ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new SandboxException("path must start with one of: " + string.Join(", ", Roots.Select(r => r.Alias)));
        var root = Roots.FirstOrDefault(r => string.Equals(r.Alias, parts[0], StringComparison.OrdinalIgnoreCase))
                   ?? throw new SandboxException($"'{parts[0]}' is not an allowed folder. Allowed: {string.Join(", ", Roots.Select(r => r.Alias))}");
        if (forWrite && !root.Writable) throw new SandboxException($"{root.Alias} is read-only");
        foreach (var p in parts.Skip(1)) ValidateName(p);

        var full = Path.GetFullPath(Path.Combine([root.FullPath, .. parts.Skip(1)]));
        if (!full.Equals(root.FullPath, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new SandboxException("path escapes the allowed folder");

        // Reject reparse points anywhere below the root (junctions/symlinks could leave the sandbox).
        var cur = root.FullPath;
        foreach (var p in parts.Skip(1))
        {
            cur = Path.Combine(cur, p);
            var info = new FileInfo(cur);
            if (info.Exists || Directory.Exists(cur))
            {
                var attrs = File.GetAttributes(cur);
                if (attrs.HasFlag(FileAttributes.ReparsePoint)) throw new SandboxException("links and junctions are not followed");
            }
        }
        return new Resolved(root, full, string.Join('/', [root.Alias, .. parts.Skip(1)]));
    }

    public string ToVirtual(string fullPath)
    {
        foreach (var r in Roots)
        {
            if (fullPath.StartsWith(r.FullPath, StringComparison.OrdinalIgnoreCase))
                return (r.Alias + fullPath[r.FullPath.Length..].Replace('\\', '/')).TrimEnd('/');
        }
        throw new SandboxException("outside sandbox");
    }

    /// <summary>Returns a non-existing path by appending " (2)", " (3)" … — files are never overwritten implicitly.</summary>
    public static string UniquePath(string fullPath)
    {
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) return fullPath;
        var dir = Path.GetDirectoryName(fullPath)!;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);
        for (var i = 2; i < 10_000; i++)
        {
            var c = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(c) && !Directory.Exists(c)) return c;
        }
        throw new SandboxException("could not find a free file name");
    }
}
