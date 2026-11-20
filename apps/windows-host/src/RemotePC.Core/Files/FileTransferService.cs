using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RemotePC.Core.Protocol;
using RemotePC.Core.Security;
using RemotePC.Core.Session;

namespace RemotePC.Core.Files;

/// <summary>
/// Chunked, resumable, checksum-verified file transfer over the "file" data channel (PROTOCOL.md §5.4),
/// plus the file-manager RPCs (fs.*). Uploads land as "*.rpcpart" temp files and are renamed only after the
/// SHA-256 matches; names never overwrite existing files; received files are never executed.
/// </summary>
public sealed class FileTransferService
{
    public const int HeaderSize = 24;
    public const int MaxChunk = 60 * 1024;
    private const string PartSuffix = ".rpcpart";

    private readonly FileSandbox _sandbox;
    private readonly IDocumentExtractor _extractor;
    private readonly Func<string, bool>? _recycle;
    private readonly Action<string, string, string?> _audit;
    private readonly ConcurrentDictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _downloads = new(StringComparer.Ordinal);

    public long MaxFileBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public string ReceiveFolderAlias { get; set; } = "Received";
    public event Action<string, string, long, long>? Progress; // id, name, done, total

    private sealed class Upload
    {
        public required string Id { get; init; }
        public required string DeviceId { get; init; }
        public required string TempPath { get; init; }
        public required string FinalPath { get; init; }
        public required string Name { get; init; }
        public required long Size { get; init; }
        public required byte[] Sha256 { get; init; }
        public FileStream? Stream;
        public long Written;
        public readonly Lock Lock = new();
        public DateTimeOffset LastActivity = DateTimeOffset.UtcNow;
    }

    public FileTransferService(FileSandbox sandbox, IDocumentExtractor extractor, Func<string, bool>? recycle, Action<string, string, string?> audit)
    {
        _sandbox = sandbox;
        _extractor = extractor;
        _recycle = recycle;
        _audit = audit;
        CleanupStaleParts();
    }

    /// <summary>Removes leftover temp files from crashed/abandoned transfers.</summary>
    public void CleanupStaleParts(TimeSpan? olderThan = null)
    {
        var cutoff = DateTime.UtcNow - (olderThan ?? TimeSpan.FromDays(1));
        foreach (var root in _sandbox.Roots.Where(r => r.Writable && Directory.Exists(r.FullPath)))
        {
            foreach (var f in Directory.EnumerateFiles(root.FullPath, "*" + PartSuffix, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 }))
            {
                if (File.GetLastWriteTimeUtc(f) < cutoff && !_uploads.Values.Any(u => u.TempPath == f))
                {
                    try { File.Delete(f); } catch (IOException) { }
                }
            }
        }
    }

    public void Attach(HostSession session) => session.FileChunk += OnChunk;

    /// <summary>Abort transfers of a device (session closed or revoked). Partial uploads stay resumable for a day.</summary>
    public void AbortDevice(string deviceId)
    {
        foreach (var u in _uploads.Values.Where(u => u.DeviceId == deviceId))
        {
            lock (u.Lock) { u.Stream?.Dispose(); u.Stream = null; }
        }
        foreach (var (id, cts) in _downloads) { if (id.StartsWith(deviceId, StringComparison.Ordinal)) cts.Cancel(); }
    }

    private void OnChunk(HostSession session, byte[] frame)
    {
        if (frame.Length < HeaderSize || frame.Length > HeaderSize + MaxChunk) return;
        var id = Convert.ToHexString(frame, 0, 16);
        var offset = BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(16, 8));
        if (!_uploads.TryGetValue(id, out var u) || u.DeviceId != session.DeviceId) return;
        if (!session.Has(Permission.SendFiles)) return;
        lock (u.Lock)
        {
            if (u.Stream is null) return;
            if (offset != u.Written) return; // only sequential writes; the client resumes from file.status
            var len = frame.Length - HeaderSize;
            if (u.Written + len > u.Size) return;
            u.Stream.Write(frame, HeaderSize, len);
            u.Written += len;
            u.LastActivity = DateTimeOffset.UtcNow;
        }
        Progress?.Invoke(u.Id, u.Name, u.Written, u.Size);
    }

    public void RegisterRpc(RpcRegistry rpc)
    {
        rpc.Register("file.begin", Permission.SendFiles, (p, ctx) =>
        {
            var name = FileSandbox.ValidateName((string?)p?["name"] ?? "");
            var size = (long?)p?["size"] ?? -1;
            var sha = Convert.FromHexString((string?)p?["sha256"] ?? "");
            if (size < 0 || size > MaxFileBytes) throw new RpcException("too_large", $"file exceeds the {MaxFileBytes / 1073741824} GB limit");
            if (sha.Length != 32) throw new RpcException("bad_request", "sha256 must be 64 hex characters");
            var folder = _sandbox.Resolve((string?)p?["folder"] ?? ReceiveFolderAlias, forWrite: true);
            Directory.CreateDirectory(folder.FullPath);

            // Resume: same device, same name/size/hash.
            var existing = _uploads.Values.FirstOrDefault(u => u.DeviceId == ctx.Device.Id && u.Name == name && u.Size == size && u.Sha256.AsSpan().SequenceEqual(sha));
            if (existing is not null)
            {
                lock (existing.Lock)
                {
                    existing.Stream ??= new FileStream(existing.TempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 16);
                    existing.Written = existing.Stream.Length;
                    existing.Stream.Position = existing.Written;
                }
                return Task.FromResult<JsonNode?>(new JsonObject { ["id"] = existing.Id, ["offset"] = existing.Written, ["chunk"] = MaxChunk });
            }

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var final = FileSandbox.UniquePath(Path.Combine(folder.FullPath, name));
            var temp = final + "." + id[..8] + PartSuffix;
            var up = new Upload
            {
                Id = id, DeviceId = ctx.Device.Id, TempPath = temp, FinalPath = final, Name = name, Size = size, Sha256 = sha,
                Stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16),
            };
            File.SetAttributes(temp, FileAttributes.Hidden | FileAttributes.Temporary);
            _uploads[id] = up;
            _audit("file", $"upload started {name} ({size} bytes) → {folder.VirtualPath}", ctx.Device.Id);
            return Task.FromResult<JsonNode?>(new JsonObject { ["id"] = id, ["offset"] = 0, ["chunk"] = MaxChunk });
        });

        rpc.Register("file.status", Permission.SendFiles, (p, ctx) =>
        {
            var u = Own((string?)p?["id"], ctx.Device.Id);
            lock (u.Lock) return Task.FromResult<JsonNode?>(new JsonObject { ["id"] = u.Id, ["offset"] = u.Written, ["size"] = u.Size });
        });

        rpc.Register("file.end", Permission.SendFiles, async (p, ctx) =>
        {
            var u = Own((string?)p?["id"], ctx.Device.Id);
            lock (u.Lock)
            {
                if (u.Written != u.Size) throw new RpcException("incomplete", $"received {u.Written} of {u.Size} bytes");
                u.Stream?.Flush(true);
                u.Stream?.Dispose();
                u.Stream = null;
            }
            byte[] actual;
            await using (var fs = File.OpenRead(u.TempPath)) actual = await SHA256.HashDataAsync(fs, ctx.Cancellation).ConfigureAwait(false);
            _uploads.TryRemove(u.Id, out _);
            if (!CryptographicOperations.FixedTimeEquals(actual, u.Sha256))
            {
                File.Delete(u.TempPath);
                _audit("file", $"upload {u.Name} rejected: checksum mismatch", ctx.Device.Id);
                throw new RpcException("checksum_mismatch", "SHA-256 mismatch; the file was discarded");
            }
            var final = FileSandbox.UniquePath(u.FinalPath); // re-check: something may have appeared meanwhile
            File.SetAttributes(u.TempPath, FileAttributes.Normal);
            File.Move(u.TempPath, final, overwrite: false);
            _audit("file", $"upload completed {Path.GetFileName(final)} ({u.Size} bytes, sha256 ok)", ctx.Device.Id);
            return new JsonObject { ["path"] = _sandbox.ToVirtual(final), ["size"] = u.Size, ["verified"] = true };
        });

        rpc.Register("file.cancel", Permission.None, (p, ctx) =>
        {
            var id = (string?)p?["id"] ?? "";
            if (_uploads.TryGetValue(id, out var u) && u.DeviceId == ctx.Device.Id)
            {
                _uploads.TryRemove(id, out _);
                lock (u.Lock) { u.Stream?.Dispose(); u.Stream = null; }
                try { File.Delete(u.TempPath); } catch (IOException) { }
            }
            if (_downloads.TryRemove(ctx.Device.Id + ":" + id, out var cts)) cts.Cancel();
            return Task.FromResult<JsonNode?>(null);
        });

        rpc.Register("file.download", Permission.ReceiveFiles, async (p, ctx) =>
        {
            var r = _sandbox.Resolve((string?)p?["path"] ?? "");
            if (!File.Exists(r.FullPath)) throw new RpcException("not_found", "file not found");
            var size = new FileInfo(r.FullPath).Length;
            var offset = Math.Clamp((long?)p?["offset"] ?? 0, 0, size);
            byte[] sha;
            await using (var fs = File.OpenRead(r.FullPath)) sha = await SHA256.HashDataAsync(fs, ctx.Cancellation).ConfigureAwait(false);
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation);
            _downloads[ctx.Device.Id + ":" + id] = cts;
            var session = ctx.Session;
            var deviceId = ctx.Device.Id;
            _audit("file", $"download started {r.VirtualPath} ({size} bytes)", deviceId);
            // Stream after replying so the client knows the id before chunks arrive.
            _ = Task.Run(async () =>
            {
                await Task.Delay(50).ConfigureAwait(false);
                try
                {
                    await SendFileAsync(session, id, r.FullPath, offset, cts.Token).ConfigureAwait(false);
                    session.SendEvent("file.done", new JsonObject { ["id"] = id });
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or RpcException)
                {
                    session.SendEvent("file.failed", new JsonObject { ["id"] = id, ["error"] = e.Message });
                }
                finally
                {
                    _downloads.TryRemove(deviceId + ":" + id, out _);
                    cts.Dispose();
                }
            });
            return new JsonObject { ["id"] = id, ["name"] = Path.GetFileName(r.FullPath), ["size"] = size, ["offset"] = offset, ["sha256"] = Convert.ToHexString(sha).ToLowerInvariant() };
        });

        // ---- file manager ----
        rpc.Register("fs.roots", Permission.ReceiveFiles, (_, _) => Task.FromResult<JsonNode?>(new JsonArray(
            _sandbox.Roots.Select(r => (JsonNode)new JsonObject { ["name"] = r.Alias, ["writable"] = r.Writable }).ToArray())));
        rpc.Register("fs.list", Permission.ReceiveFiles, (p, _) =>
        {
            var r = _sandbox.Resolve((string?)p?["path"] ?? "");
            if (!Directory.Exists(r.FullPath)) throw new RpcException("not_found", "folder not found");
            var items = new DirectoryInfo(r.FullPath).EnumerateFileSystemInfos()
                .Where(i => !i.Attributes.HasFlag(FileAttributes.Hidden) && !i.Attributes.HasFlag(FileAttributes.System) && !i.Attributes.HasFlag(FileAttributes.ReparsePoint) && !i.Name.EndsWith(PartSuffix, StringComparison.Ordinal))
                .Take(5000).Select(Entry).ToArray();
            return Task.FromResult<JsonNode?>(new JsonObject { ["path"] = r.VirtualPath, ["items"] = new JsonArray(items) });
        });
        rpc.Register("fs.stat", Permission.ReceiveFiles, (p, _) =>
        {
            var r = _sandbox.Resolve((string?)p?["path"] ?? "");
            FileSystemInfo info = File.Exists(r.FullPath) ? new FileInfo(r.FullPath) : Directory.Exists(r.FullPath) ? new DirectoryInfo(r.FullPath) : throw new RpcException("not_found", "not found");
            var o = (JsonObject)Entry(info);
            o["path"] = r.VirtualPath;
            o["created"] = info.CreationTimeUtc.ToString("O");
            o["kind"] = info is FileInfo ? DocumentExtractor.Classify(r.FullPath).ToString() : "Folder";
            return Task.FromResult<JsonNode?>(o);
        });
        rpc.Register("fs.mkdir", Permission.SendFiles, (p, ctx) =>
        {
            var r = _sandbox.Resolve((string?)p?["path"] ?? "", forWrite: true);
            if (File.Exists(r.FullPath)) throw new RpcException("exists", "a file with that name exists");
            Directory.CreateDirectory(r.FullPath);
            _audit("file", $"folder created {r.VirtualPath}", ctx.Device.Id);
            return Task.FromResult<JsonNode?>(new JsonObject { ["path"] = r.VirtualPath });
        });
        rpc.Register("fs.rename", Permission.ModifyFiles, (p, ctx) =>
        {
            var r = _sandbox.Resolve((string?)p?["path"] ?? "", forWrite: true);
            var name = FileSandbox.ValidateName((string?)p?["name"] ?? "");
            var target = Path.Combine(Path.GetDirectoryName(r.FullPath)!, name);
            if (File.Exists(target) || Directory.Exists(target)) throw new RpcException("exists", "an item with that name exists");
            if (File.Exists(r.FullPath)) File.Move(r.FullPath, target);
            else if (Directory.Exists(r.FullPath) && r.FullPath != r.Root.FullPath) Directory.Move(r.FullPath, target);
            else throw new RpcException("not_found", "not found");
            _audit("file", $"renamed {r.VirtualPath} → {name}", ctx.Device.Id);
            return Task.FromResult<JsonNode?>(new JsonObject { ["path"] = _sandbox.ToVirtual(target) });
        });
        rpc.Register("fs.delete", Permission.ModifyFiles, (p, ctx) =>
        {
            if ((bool?)p?["confirmed"] != true) throw new RpcException("confirm_required", "deletion must be confirmed by the user");
            var paths = p?["paths"]?.AsArray().Select(x => (string?)x).Where(x => x is not null).Cast<string>().Take(200).ToList() ?? [];
            var deleted = new JsonArray();
            foreach (var vp in paths)
            {
                var r = _sandbox.Resolve(vp, forWrite: true);
                if (r.FullPath.Equals(r.Root.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
                var ok = _recycle?.Invoke(r.FullPath) ?? false;
                if (!ok)
                {
                    if (File.Exists(r.FullPath)) { File.Delete(r.FullPath); ok = true; }
                    else if (Directory.Exists(r.FullPath) && !Directory.EnumerateFileSystemEntries(r.FullPath).Any()) { Directory.Delete(r.FullPath); ok = true; }
                }
                if (ok) deleted.Add(r.VirtualPath);
            }
            _audit("file", $"deleted {deleted.Count} item(s)", ctx.Device.Id);
            return Task.FromResult<JsonNode?>(new JsonObject { ["deleted"] = deleted, ["recycleBin"] = _recycle is not null });
        });
        rpc.Register("fs.search", Permission.ReceiveFiles, (p, _) =>
        {
            var r = _sandbox.Resolve((string?)p?["path"] ?? "");
            var q = (string?)p?["query"] ?? "";
            if (q.Length is 0 or > 100) throw new RpcException("bad_request", "query required");
            var pattern = q.Contains('*') || q.Contains('?') ? q : $"*{q}*";
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint, MaxRecursionDepth = 10 };
            var hits = Directory.EnumerateFileSystemEntries(r.FullPath, pattern, opts).Take(300)
                .Select(f => { var o = (JsonObject)Entry(File.Exists(f) ? new FileInfo(f) : new DirectoryInfo(f)); o["path"] = _sandbox.ToVirtual(f); return (JsonNode)o; }).ToArray();
            return Task.FromResult<JsonNode?>(new JsonArray(hits));
        });
        rpc.Register("ai.extract", Permission.UseAi, async (p, ctx) =>
        {
            var r = _sandbox.Resolve((string?)p?["path"] ?? "");
            var max = Math.Clamp((int?)p?["maxChars"] ?? 40_000, 1000, 200_000);
            var ex = await _extractor.ExtractAsync(r.FullPath, max, ctx.Cancellation).ConfigureAwait(false);
            return new JsonObject { ["supported"] = ex.Supported, ["kind"] = ex.Kind.ToString(), ["text"] = ex.Text, ["truncated"] = ex.Truncated, ["message"] = ex.Message };
        });
    }

    private Upload Own(string? id, string deviceId) =>
        id is not null && _uploads.TryGetValue(id, out var u) && u.DeviceId == deviceId ? u : throw new RpcException("not_found", "unknown transfer");

    private static JsonNode Entry(FileSystemInfo i) => new JsonObject
    {
        ["name"] = i.Name,
        ["type"] = i is DirectoryInfo ? "folder" : "file",
        ["size"] = i is FileInfo f ? f.Length : null,
        ["modified"] = i.LastWriteTimeUtc.ToString("O"),
    };

    private async Task SendFileAsync(HostSession session, string id, string path, long offset, CancellationToken ct)
    {
        var idBytes = Convert.FromHexString(id);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        fs.Position = offset;
        var size = fs.Length;
        var buf = new byte[MaxChunk];
        while (true)
        {
            var n = await fs.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) break;
            var frame = new byte[HeaderSize + n];
            idBytes.CopyTo(frame, 0);
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(16), offset);
            Buffer.BlockCopy(buf, 0, frame, HeaderSize, n);
            await session.SendFileChunkAsync(frame, ct).ConfigureAwait(false);
            offset += n;
            Progress?.Invoke(id, Path.GetFileName(path), offset, size);
        }
    }

    public static byte[] Frame(string idHex, long offset, ReadOnlySpan<byte> payload)
    {
        var f = new byte[HeaderSize + payload.Length];
        Convert.FromHexString(idHex).CopyTo(f, 0);
        BinaryPrimitives.WriteInt64BigEndian(f.AsSpan(16), offset);
        payload.CopyTo(f.AsSpan(HeaderSize));
        return f;
    }
}
