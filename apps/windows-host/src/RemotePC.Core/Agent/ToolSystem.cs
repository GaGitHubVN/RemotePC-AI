using System.Text.Json.Nodes;
using RemotePC.Core.Security;

namespace RemotePC.Core.Agent;

/// <summary>Risk classes drive the approval policy (ADR-0006).</summary>
public enum ToolRisk
{
    /// <summary>Observes only (screenshots, listing, reading allowed files).</summary>
    Read,
    /// <summary>Changes state in a reversible / scoped way (typing, clicking, creating a file in an allowed folder).</summary>
    Write,
    /// <summary>Destructive or hard to undo (delete, overwrite, moving many files). Always needs explicit approval.</summary>
    Sensitive,
}

public sealed record ToolResult(bool Ok, string Summary, JsonNode? Data = null, byte[]? ImagePng = null, bool Verified = false)
{
    public static ToolResult Fail(string message) => new(false, message);
}

public sealed class ToolContext
{
    public required string TaskId { get; init; }
    public required PairedDevice Device { get; init; }
    public required CancellationToken Cancellation { get; init; }
    public required Action<string> Progress { get; init; }
}

public interface IAgentTool
{
    /// <summary>Dotted name, e.g. "file.create". Exposed to models as "file_create".</summary>
    string Name { get; }
    string Description { get; }
    /// <summary>JSON Schema subset: object with typed properties, required, additionalProperties=false.</summary>
    JsonObject Schema { get; }
    ToolRisk Risk { get; }
    TimeSpan Timeout { get; }
    /// <summary>Human readable description of what this call will do, shown in approval cards.</summary>
    string Describe(JsonObject args);
    /// <summary>Some write tools are sensitive only for certain arguments (e.g. overwrite=true).</summary>
    ToolRisk RiskFor(JsonObject args) => Risk;
    Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx);
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.Ordinal);

    public void Add(IAgentTool tool)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(tool.Name, "^[a-z]+\\.[a-z_]+$")) throw new ArgumentException("tool name must be 'group.action'");
        if (SchemaValidator.ValidateSchemaShape(tool.Schema) is { } err) throw new ArgumentException($"{tool.Name}: {err}");
        _tools.Add(tool.Name, tool);
    }

    public IReadOnlyCollection<IAgentTool> All => _tools.Values;

    public IAgentTool? Get(string name) =>
        _tools.GetValueOrDefault(name) ?? _tools.GetValueOrDefault(FromModelName(name));

    public static string ToModelName(string name) => name.Replace('.', '_');

    /// <summary>"file_create" → "file.create" (first underscore is the group separator).</summary>
    public static string FromModelName(string name)
    {
        var i = name.IndexOf('_', StringComparison.Ordinal);
        return i < 0 ? name : name[..i] + "." + name[(i + 1)..];
    }

    public IReadOnlyList<AI.ToolDefinition> Definitions(Func<IAgentTool, bool> include) =>
        _tools.Values.Where(include).Select(t => new AI.ToolDefinition(ToModelName(t.Name), t.Description, t.Schema)).ToList();
}

/// <summary>
/// Validates tool arguments against the declared schema subset. Unknown properties are rejected —
/// small models frequently invent arguments, and silently passing them on is how tools misbehave.
/// </summary>
public static class SchemaValidator
{
    public static string? ValidateSchemaShape(JsonObject schema) =>
        (string?)schema["type"] != "object" ? "schema type must be object" :
        schema["properties"] is not JsonObject ? "schema needs properties" : null;

    public static string? Validate(JsonObject schema, JsonObject args)
    {
        var props = (JsonObject)schema["properties"]!;
        foreach (var req in schema["required"]?.AsArray() ?? [])
        {
            var name = (string?)req;
            if (name is not null && args[name] is null) return $"missing required argument '{name}'";
        }
        foreach (var (key, value) in args)
        {
            if (props[key] is not JsonObject p) return $"unknown argument '{key}'";
            if (value is null) continue;
            var err = ValidateValue(key, p, value);
            if (err is not null) return err;
        }
        return null;
    }

    private static string? ValidateValue(string key, JsonObject p, JsonNode value)
    {
        var type = (string?)p["type"];
        switch (type)
        {
            case "string":
                if (value is not JsonValue sv || !sv.TryGetValue<string>(out var s)) return $"'{key}' must be a string";
                if ((int?)p["maxLength"] is { } max && s.Length > max) return $"'{key}' is longer than {max} characters";
                if (p["enum"] is JsonArray en && !en.Any(e => (string?)e == s)) return $"'{key}' must be one of {en.ToJsonString()}";
                return null;
            case "integer":
                if (value is not JsonValue iv || !iv.TryGetValue<double>(out var d) || d != Math.Floor(d)) return $"'{key}' must be an integer";
                return Range(key, p, d);
            case "number":
                if (value is not JsonValue nv || !nv.TryGetValue<double>(out var n)) return $"'{key}' must be a number";
                return Range(key, p, n);
            case "boolean":
                return value is JsonValue bv && bv.TryGetValue<bool>(out _) ? null : $"'{key}' must be true or false";
            case "array":
                if (value is not JsonArray arr) return $"'{key}' must be an array";
                if ((int?)p["maxItems"] is { } mi && arr.Count > mi) return $"'{key}' has more than {mi} items";
                if (p["items"] is JsonObject items)
                {
                    foreach (var item in arr)
                    {
                        if (item is null) return $"'{key}' contains null";
                        if (ValidateValue(key + "[]", items, item) is { } e) return e;
                    }
                }
                return null;
            default:
                return $"'{key}' has unsupported schema type";
        }
    }

    private static string? Range(string key, JsonObject p, double v)
    {
        if ((double?)p["minimum"] is { } min && v < min) return $"'{key}' must be ≥ {min}";
        if ((double?)p["maximum"] is { } max && v > max) return $"'{key}' must be ≤ {max}";
        return null;
    }
}

/// <summary>Small helpers to declare schemas tersely.</summary>
public static class S
{
    public static JsonObject Obj(params (string Name, JsonObject Prop, bool Required)[] props)
    {
        var p = new JsonObject();
        var req = new JsonArray();
        foreach (var (name, prop, required) in props)
        {
            p[name] = prop;
            if (required) req.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req, ["additionalProperties"] = false };
    }

    public static JsonObject Str(string desc, int maxLength = 4096, params string[] oneOf)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = desc, ["maxLength"] = maxLength };
        if (oneOf.Length > 0) o["enum"] = new JsonArray(oneOf.Select(x => (JsonNode)x).ToArray());
        return o;
    }

    public static JsonObject Whole(string desc, int min = int.MinValue, int max = int.MaxValue) =>
        new() { ["type"] = "integer", ["description"] = desc, ["minimum"] = min, ["maximum"] = max };

    public static JsonObject Bool(string desc) => new() { ["type"] = "boolean", ["description"] = desc };

    public static JsonObject StrArray(string desc, int maxItems) =>
        new() { ["type"] = "array", ["description"] = desc, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 64 }, ["maxItems"] = maxItems };
}
