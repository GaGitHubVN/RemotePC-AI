using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemotePC.Core.AI;

/// <summary>Ollama native API: /api/version, /api/tags, /api/show, /api/chat (NDJSON streaming, tools, images).</summary>
public sealed class OllamaProvider(HttpClient http, Uri endpoint, string id = "ollama") : IAiProvider
{
    public string Id => id;
    public string Kind => "ollama";
    public string Name => "Ollama";
    public Uri Endpoint => endpoint;

    private Uri U(string path) => new(endpoint, path);

    public async Task<AiProviderStatus> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var v = await http.GetFromJsonAsync<JsonObject>(U("/api/version"), cts.Token).ConfigureAwait(false);
            return new AiProviderStatus(Id, Kind, Name, endpoint.ToString(), true, (string?)v?["version"], null, true, true,
                ["temperature", "top_p", "max_tokens", "context_length"]);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new AiProviderStatus(Id, Kind, Name, endpoint.ToString(), false, null, Describe(e), true, true, []);
        }
    }

    internal static string Describe(Exception e) => e switch
    {
        OperationCanceledException => "timed out",
        HttpRequestException h when h.InnerException is System.Net.Sockets.SocketException => "not running (connection refused)",
        _ => e.Message,
    };

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct)
    {
        var tags = await http.GetFromJsonAsync<JsonObject>(U("/api/tags"), ct).ConfigureAwait(false);
        var list = new List<AiModel>();
        foreach (var m in tags?["models"]?.AsArray() ?? [])
        {
            var name = (string?)m?["name"] ?? (string?)m?["model"];
            if (name is null) continue;
            var details = m!["details"];
            bool? tools = null, vision = null;
            long? ctx = null;
            try
            {
                using var resp = await http.PostAsJsonAsync(U("/api/show"), new { model = name }, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var show = await resp.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false);
                    if (show?["capabilities"] is JsonArray caps)
                    {
                        var set = caps.Select(c => (string?)c).ToHashSet();
                        tools = set.Contains("tools");
                        vision = set.Contains("vision");
                    }
                    if (show?["model_info"] is JsonObject info)
                    {
                        foreach (var kv in info)
                        {
                            if (kv.Key.EndsWith(".context_length", StringComparison.Ordinal) && kv.Value is JsonValue jv && jv.TryGetValue<long>(out var c)) ctx = c;
                        }
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or JsonException) { /* metadata stays unknown */ }
            list.Add(new AiModel(Id, name, name, ctx, tools, vision, (string?)details?["family"], (string?)details?["parameter_size"],
                (string?)details?["quantization_level"], (long?)m["size"]));
        }
        return list;
    }

    internal static JsonObject BuildBody(ChatRequest r)
    {
        var msgs = new JsonArray();
        foreach (var m in r.Messages)
        {
            var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
            if (m.Images is { Count: > 0 }) o["images"] = new JsonArray(m.Images.Select(i => (JsonNode)i).ToArray());
            if (m.ToolCalls is { Count: > 0 })
                o["tool_calls"] = new JsonArray(m.ToolCalls.Select(t => (JsonNode)new JsonObject { ["function"] = new JsonObject { ["name"] = t.Name, ["arguments"] = t.Arguments.DeepClone() } }).ToArray());
            if (m.ToolName is not null) o["tool_name"] = m.ToolName;
            msgs.Add(o);
        }
        var body = new JsonObject { ["model"] = r.Model, ["messages"] = msgs, ["stream"] = true };
        if (r.Tools is { Count: > 0 })
        {
            body["tools"] = new JsonArray(r.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
            }).ToArray());
        }
        var opts = new JsonObject();
        if (r.Options.Temperature is { } temp) opts["temperature"] = temp;
        if (r.Options.TopP is { } tp) opts["top_p"] = tp;
        if (r.Options.MaxTokens is { } mt) opts["num_predict"] = mt;
        if (r.Options.ContextLength is { } nc) opts["num_ctx"] = nc;
        if (opts.Count > 0) body["options"] = opts;
        return body;
    }

    public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double? firstToken = null;
        using var req = new HttpRequestMessage(HttpMethod.Post, U("/api/chat"))
        {
            Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new AiProviderException($"Ollama returned {(int)resp.StatusCode}: {Truncate(err)}");
        }
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var callIndex = 0;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            var o = JsonNode.Parse(line) as JsonObject;
            if (o is null) continue;
            if ((string?)o["error"] is { } error) throw new AiProviderException(error);
            var msg = o["message"];
            var delta = (string?)msg?["content"];
            var thinking = (string?)msg?["thinking"];
            List<ToolCall>? calls = null;
            if (msg?["tool_calls"] is JsonArray tc)
            {
                calls = [];
                foreach (var c in tc)
                {
                    var f = c?["function"];
                    var args = f?["arguments"] switch
                    {
                        JsonObject obj => (JsonObject)obj.DeepClone(),
                        JsonValue s when s.TryGetValue<string>(out var str) => JsonNode.Parse(str) as JsonObject ?? [],
                        _ => [],
                    };
                    calls.Add(new ToolCall((string?)c?["id"] ?? $"call_{callIndex++}", (string?)f?["name"] ?? "", args));
                }
            }
            if (!string.IsNullOrEmpty(delta) || calls is not null) firstToken ??= sw.Elapsed.TotalMilliseconds;
            var done = (bool?)o["done"] ?? false;
            ChatUsage? usage = null;
            if (done)
            {
                var evalCount = (int?)o["eval_count"];
                var evalNs = (long?)o["eval_duration"];
                usage = new ChatUsage((int?)o["prompt_eval_count"], evalCount,
                    evalCount is not null && evalNs is > 0 ? evalCount.Value / (evalNs.Value / 1e9) : null,
                    firstToken, sw.Elapsed.TotalMilliseconds);
            }
            if (!string.IsNullOrEmpty(delta) || !string.IsNullOrEmpty(thinking) || calls is not null || done)
                yield return new ChatChunk(string.IsNullOrEmpty(delta) ? null : delta, string.IsNullOrEmpty(thinking) ? null : thinking, calls, done, usage, (string?)o["done_reason"]);
            if (done) yield break;
        }
    }

    internal static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
