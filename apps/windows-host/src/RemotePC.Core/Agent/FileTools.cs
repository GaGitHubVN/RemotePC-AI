using System.Text;
using System.Text.Json.Nodes;
using RemotePC.Core.Files;

namespace RemotePC.Core.Agent;

/// <summary>Agent file tools. All paths go through <see cref="FileSandbox"/>; nothing is ever executed.</summary>
public static class FileTools
{
    public static void Register(ToolRegistry reg, FileSandbox sandbox, IDocumentExtractor extractor, Func<string, bool>? recycle)
    {
        reg.Add(new Tool("file.list", "List files and folders. Use path \"\" to list the allowed root folders (Desktop, Documents, Downloads, Received).",
            S.Obj(("path", S.Str("virtual path such as 'Desktop' or 'Documents/Project'", 1024), false)), ToolRisk.Read,
            a => $"List {(string?)a["path"] ?? "allowed folders"}", (a, ct) =>
            {
                var path = (string?)a["path"] ?? "";
                if (path.Trim('/', ' ').Length == 0)
                    return Ok("Allowed folders", new JsonArray(sandbox.Roots.Select(r => (JsonNode)new JsonObject { ["name"] = r.Alias, ["type"] = "folder", ["writable"] = r.Writable }).ToArray()));
                var r = sandbox.Resolve(path);
                if (!Directory.Exists(r.FullPath)) return ToolResult.Fail($"folder not found: {r.VirtualPath}");
                var items = new DirectoryInfo(r.FullPath).EnumerateFileSystemInfos()
                    .Where(i => !i.Attributes.HasFlag(FileAttributes.Hidden) && !i.Attributes.HasFlag(FileAttributes.System))
                    .Take(500)
                    .Select(i => (JsonNode)new JsonObject
                    {
                        ["name"] = i.Name, ["type"] = i is DirectoryInfo ? "folder" : "file",
                        ["size"] = i is FileInfo f ? f.Length : null, ["modified"] = i.LastWriteTimeUtc.ToString("O"),
                    }).ToArray();
                return Ok($"{items.Length} entries in {r.VirtualPath}", new JsonArray(items));
            }));

        reg.Add(new Tool("file.read", "Read a text-like file (txt, md, csv, json, xml, yaml, code) or extract text from pdf/docx/xlsx.",
            S.Obj(("path", S.Str("virtual path of the file", 1024), true), ("max_chars", S.Whole("maximum characters to return", 100, 100_000), false)), ToolRisk.Read,
            a => $"Read {(string?)a["path"]}", async (a, ct) =>
            {
                var r = sandbox.Resolve((string)a["path"]!);
                if (!File.Exists(r.FullPath)) return ToolResult.Fail($"file not found: {r.VirtualPath}");
                var max = (int?)a["max_chars"] ?? 20_000;
                var ex = await extractor.ExtractAsync(r.FullPath, max, ct).ConfigureAwait(false);
                if (!ex.Supported) return ToolResult.Fail(ex.Message);
                return new ToolResult(true, $"{r.VirtualPath} ({ex.Kind}{(ex.Truncated ? ", truncated" : "")}):\n{ex.Text}", Verified: true);
            }));

        reg.Add(new Tool("file.create_folder", "Create a new folder (directory). Use this for folders, not file_create.",
            S.Obj(("path", S.Str("virtual path of the folder to create, e.g. 'Desktop/Homework'", 1024), true)), ToolRisk.Write,
            a => $"Create folder {(string?)a["path"]}", (a, ct) =>
            {
                var r = sandbox.Resolve((string)a["path"]!, forWrite: true);
                if (File.Exists(r.FullPath)) return ToolResult.Fail("a file with that name already exists");
                var existed = Directory.Exists(r.FullPath);
                Directory.CreateDirectory(r.FullPath);
                var ok = Directory.Exists(r.FullPath);
                return new ToolResult(ok, existed ? $"Folder {r.VirtualPath} already existed (verified)" : $"Created folder {r.VirtualPath} (verified it exists)", Verified: ok);
            }));

        reg.Add(new Tool("file.create", "Create a new text FILE with content. For folders use file_create_folder. Never overwrites unless overwrite=true (which requires approval).",
            S.Obj(("path", S.Str("virtual path of the file, e.g. 'Desktop/notes.txt'", 1024), true),
                  ("content", S.Str("text content", 200_000), false),
                  ("overwrite", S.Bool("replace an existing file"), false)), ToolRisk.Write,
            a => $"Create file {(string?)a["path"]}{((bool?)a["overwrite"] == true ? " (OVERWRITE)" : "")}",
            async (a, ct) =>
            {
                var r = sandbox.Resolve((string)a["path"]!, forWrite: true);
                var overwrite = (bool?)a["overwrite"] == true;
                if (Directory.Exists(r.FullPath)) return ToolResult.Fail("a folder with that name exists");
                if (File.Exists(r.FullPath) && !overwrite) return ToolResult.Fail($"{r.VirtualPath} already exists (set overwrite=true to replace it)");
                Directory.CreateDirectory(Path.GetDirectoryName(r.FullPath)!);
                var content = (string?)a["content"] ?? "";
                await File.WriteAllTextAsync(r.FullPath, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
                var back = await File.ReadAllTextAsync(r.FullPath, ct).ConfigureAwait(false);
                var ok = back == content;
                return new ToolResult(ok, ok ? $"Wrote {content.Length} characters to file {r.VirtualPath} (read back and verified)" : "file content mismatch after write", Verified: ok);
            }, a => (bool?)a["overwrite"] == true ? ToolRisk.Sensitive : ToolRisk.Write));

        reg.Add(new Tool("file.copy", "Copy a file to a new location inside the allowed folders. Never overwrites.",
            S.Obj(("from", S.Str("source virtual path", 1024), true), ("to", S.Str("destination virtual path", 1024), true)), ToolRisk.Write,
            a => $"Copy {(string?)a["from"]} → {(string?)a["to"]}", (a, ct) =>
            {
                var from = sandbox.Resolve((string)a["from"]!);
                var to = sandbox.Resolve((string)a["to"]!, forWrite: true);
                if (!File.Exists(from.FullPath)) return ToolResult.Fail("source file not found (folders cannot be copied)");
                if (File.Exists(to.FullPath) || Directory.Exists(to.FullPath)) return ToolResult.Fail("destination exists");
                File.Copy(from.FullPath, to.FullPath, overwrite: false);
                return new ToolResult(File.Exists(to.FullPath), $"Copied to {to.VirtualPath} (verified)", Verified: true);
            }));

        reg.Add(new Tool("file.move", "Move a file or folder to a new location inside the allowed folders. Never overwrites.",
            S.Obj(("from", S.Str("source virtual path", 1024), true), ("to", S.Str("destination virtual path", 1024), true)), ToolRisk.Write,
            a => $"Move {(string?)a["from"]} → {(string?)a["to"]}", (a, ct) =>
            {
                var from = sandbox.Resolve((string)a["from"]!, forWrite: true);
                var to = sandbox.Resolve((string)a["to"]!, forWrite: true);
                if (File.Exists(to.FullPath) || Directory.Exists(to.FullPath)) return ToolResult.Fail("destination exists");
                if (File.Exists(from.FullPath)) File.Move(from.FullPath, to.FullPath);
                else if (Directory.Exists(from.FullPath)) Directory.Move(from.FullPath, to.FullPath);
                else return ToolResult.Fail("source not found");
                var ok = (File.Exists(to.FullPath) || Directory.Exists(to.FullPath)) && !File.Exists(from.FullPath) && !Directory.Exists(from.FullPath);
                return new ToolResult(ok, $"Moved to {to.VirtualPath}" + (ok ? " (verified)" : ""), Verified: ok);
            }));

        reg.Add(new Tool("file.rename", "Rename a file or folder in place.",
            S.Obj(("path", S.Str("virtual path", 1024), true), ("new_name", S.Str("new name without folders", 200), true)), ToolRisk.Write,
            a => $"Rename {(string?)a["path"]} → {(string?)a["new_name"]}", (a, ct) =>
            {
                var r = sandbox.Resolve((string)a["path"]!, forWrite: true);
                var name = FileSandbox.ValidateName((string)a["new_name"]!);
                var target = Path.Combine(Path.GetDirectoryName(r.FullPath)!, name);
                if (File.Exists(target) || Directory.Exists(target)) return ToolResult.Fail("an item with that name exists");
                if (File.Exists(r.FullPath)) File.Move(r.FullPath, target);
                else if (Directory.Exists(r.FullPath)) Directory.Move(r.FullPath, target);
                else return ToolResult.Fail("not found");
                return new ToolResult(true, $"Renamed to {sandbox.ToVirtual(target)} (verified)", Verified: File.Exists(target) || Directory.Exists(target));
            }));

        reg.Add(new Tool("file.delete", "Delete a file or an EMPTY folder (sent to the Recycle Bin when available). Always requires the user's approval.",
            S.Obj(("path", S.Str("virtual path", 1024), true)), ToolRisk.Sensitive,
            a => $"DELETE {(string?)a["path"]}", (a, ct) =>
            {
                var r = sandbox.Resolve((string)a["path"]!, forWrite: true);
                if (r.FullPath.Equals(r.Root.FullPath, StringComparison.OrdinalIgnoreCase)) return ToolResult.Fail("cannot delete a root folder");
                if (Directory.Exists(r.FullPath) && Directory.EnumerateFileSystemEntries(r.FullPath).Any()) return ToolResult.Fail("folder is not empty; delete its files first");
                if (!File.Exists(r.FullPath) && !Directory.Exists(r.FullPath)) return ToolResult.Fail("not found");
                var recycled = recycle?.Invoke(r.FullPath) ?? false;
                if (!recycled)
                {
                    if (File.Exists(r.FullPath)) File.Delete(r.FullPath); else Directory.Delete(r.FullPath);
                }
                var gone = !File.Exists(r.FullPath) && !Directory.Exists(r.FullPath);
                return new ToolResult(gone, $"Deleted {r.VirtualPath}{(recycled ? " (in Recycle Bin)" : "")}" + (gone ? " (verified)" : ""), Verified: gone);
            }));

        reg.Add(new Tool("file.search", "Search file names (wildcards * and ?) below a folder.",
            S.Obj(("path", S.Str("folder to search in", 1024), true), ("pattern", S.Str("e.g. *.pdf or report*", 200), true), ("max_results", S.Whole("limit", 1, 500), false)), ToolRisk.Read,
            a => $"Search {(string?)a["pattern"]} in {(string?)a["path"]}", (a, ct) =>
            {
                var r = sandbox.Resolve((string)a["path"]!);
                if (!Directory.Exists(r.FullPath)) return ToolResult.Fail("folder not found");
                var max = (int?)a["max_results"] ?? 100;
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden, MaxRecursionDepth = 12 };
                var hits = Directory.EnumerateFileSystemEntries(r.FullPath, (string)a["pattern"]!, opts).Take(max).Select(p => (JsonNode)sandbox.ToVirtual(p)).ToArray();
                return Ok($"{hits.Length} match(es)", new JsonArray(hits));
            }));
    }

    private static ToolResult Ok(string summary, JsonNode data) => new(true, summary, data);

    /// <summary>Generic lambda-backed tool (sync or async body).</summary>
    public sealed class Tool : IAgentTool
    {
        private readonly Func<JsonObject, string> _describe;
        private readonly Func<JsonObject, CancellationToken, Task<ToolResult>> _run;
        private readonly Func<JsonObject, ToolRisk>? _riskFor;

        public Tool(string name, string description, JsonObject schema, ToolRisk risk, Func<JsonObject, string> describe,
            Func<JsonObject, CancellationToken, Task<ToolResult>> run, Func<JsonObject, ToolRisk>? riskFor = null, TimeSpan? timeout = null)
        {
            Name = name;
            Description = description;
            Schema = schema;
            Risk = risk;
            Timeout = timeout ?? TimeSpan.FromSeconds(30);
            _describe = describe;
            _run = run;
            _riskFor = riskFor;
        }

        public Tool(string name, string description, JsonObject schema, ToolRisk risk, Func<JsonObject, string> describe,
            Func<JsonObject, CancellationToken, ToolResult> run, Func<JsonObject, ToolRisk>? riskFor = null, TimeSpan? timeout = null)
            : this(name, description, schema, risk, describe, (a, ct) => Task.FromResult(run(a, ct)), riskFor, timeout) { }

        public string Name { get; }
        public string Description { get; }
        public JsonObject Schema { get; }
        public ToolRisk Risk { get; }
        public TimeSpan Timeout { get; }
        public string Describe(JsonObject args) => _describe(args);
        public ToolRisk RiskFor(JsonObject args) => _riskFor?.Invoke(args) ?? Risk;

        public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx)
        {
            try { return await _run(args, ctx.Cancellation).ConfigureAwait(false); }
            catch (SandboxException e) { return ToolResult.Fail(e.Message); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ToolResult.Fail(e.Message); }
        }
    }
}