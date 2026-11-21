using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemotePC.Core.AI;

/// <summary>
/// OpenAI-compatible chat API (LM Studio, llama.cpp llama-server, vLLM, LocalAI, …): /v1/models and
/// /v1/chat/completions with SSE streaming. Most such servers do not report context length or
/// capabilities, so those stay null ("unknown") in the UI.
/// </summary>
public sealed class OpenAiCompatibleProvider(HttpClient http, Uri endpoint, string id, string name, string? apiKey = null, bool remote = false) : IAiProvider
{
    public string Id => id;
    public string Kind => "openai";
    public string Name => name;
    public Uri Endpoint => endpoint;
    public bool Remote => remote;

    /// <summary>
    /// API base ending in '/': a bare host ("http://127.0.0.1:1234") means "/v1/"; cloud bases keep their own prefix
    /// ("https://generativelanguage.googleapis.com/v1beta/openai/", "https://api.groq.com/openai/v1/").
    /// </summary>
    private readonly Uri _base = endpoint.AbsolutePath is "" or "/" ? new Uri(endpoint, "/v1/")
        : new Uri(endpoint.ToString().TrimEnd('/') + "/");

    private HttpRequestMessage Req(HttpMethod m, string path)
    {
        var r = new HttpRequestMessage(m, new Uri(_base, path.TrimStart('/').Replace("v1/", "", StringComparison.Ordinal)));
        if (!string.IsNullOrEmpty(apiKey)) r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return r;
    }

    public async Task<AiProviderStatus> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(remote ? 10 : 2));
            using var r = Req(HttpMethod.Get, "/v1/models");
            using var resp = await http.SendAsync(r, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<JsonObject>(cts.Token).ConfigureAwait(false);
            if (body?["data"] is not JsonArray) throw new AiProviderException("not an OpenAI-compatible /v1/models response");
            return new AiProviderStatus(Id, Kind, Name, endpoint.ToString(), true, null, null, true, true, ["temperature", "top_p", "max_tokens"]) { Remote = remote };
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or AiProviderException)
        {
            return new AiProviderStatus(Id, Kind, Name, endpoint.ToString(), false, null, OllamaProvider.Describe(e), true, true, []) { Remote = remote };
        }
    }

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct)
    {
        using var r = Req(HttpMethod.Get, "/v1/models");
        using var resp = await http.SendAsync(r, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false);
        var list = new List<AiModel>();
        foreach (var m in body?["data"]?.AsArray() ?? [])
        {
            var mid = (string?)m?["id"];
            if (mid is null) continue;
            // LM Studio's extended fields, when present.
            if (remote && !IsChatModel(mid)) continue;
            long? ctx = (long?)m!["max_context_length"] ?? (long?)m["context_length"];
            list.Add(new AiModel(Id, mid.StartsWith("models/", StringComparison.Ordinal) ? mid[7..] : mid, mid, ctx));
        }
        return list;
    }

    /// <summary>Cloud catalogues list embedding/audio/image models too; only chat models make sense here.</summary>
    internal static bool IsChatModel(string id)
    {
        var s = id.ToLowerInvariant();
        string[] skip = ["embed", "whisper", "tts", "audio", "realtime", "transcribe", "dall-e", "image", "moderation", "search", "davinci", "babbage", "guard", "aqa", "imagen", "veo"];
        return !skip.Any(s.Contains);
    }

    internal static JsonObject BuildBody(ChatRequest r)
    {
        var msgs = new JsonArray();
        foreach (var m in r.Messages)
        {
            JsonObject o;
            if (m.Images is { Count: > 0 })
            {
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Content } };
                foreach (var img in m.Images) parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + img } });
                o = new JsonObject { ["role"] = m.Role, ["content"] = parts };
            }
            else
            {
                o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
            }
            if (m.ToolCalls is { Count: > 0 })
            {
                o["tool_calls"] = new JsonArray(m.ToolCalls.Select(t => (JsonNode)new JsonObject
                {
                    ["id"] = t.Id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = t.Name, ["arguments"] = t.Arguments.ToJsonString() },
                }).ToArray());
            }
            if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
            msgs.Add(o);
        }
        var body = new JsonObject { ["model"] = r.Model, ["messages"] = msgs, ["stream"] = true, ["stream_options"] = new JsonObject { ["include_usage"] = true } };
        if (r.Tools is { Count: > 0 })
        {
            body["tools"] = new JsonArray(r.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
            }).ToArray());
        }
        if (r.Options.Temperature is { } temp) body["temperature"] = temp;
        if (r.Options.TopP is { } tp) body["top_p"] = tp;
        if (r.Options.MaxTokens is { } mt) body["max_tokens"] = mt;
        return body;
    }

    public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double? firstToken = null;
        var body = BuildBody(request);
        HttpResponseMessage resp;
        for (var attempt = 0; ; attempt++)
        {
            using var req = Req(HttpMethod.Post, "/v1/chat/completions");
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) break;
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            resp.Dispose();
            // Reasoning models (OpenAI o-series / GPT-5) reject sampling knobs and want max_completion_tokens: retry once.
            if (attempt == 0 && (int)resp.StatusCode == 400 && RelaxForReasoningModels(body)) continue;
            throw new AiProviderException($"{Name} returned {(int)resp.StatusCode}: {OllamaProvider.Truncate(err)}");
        }
        using var _ = resp;
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        // Tool call arguments arrive as string fragments keyed by index.
        var pending = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        int? promptTokens = null, completionTokens = null;
        string? finish = null;
        var completionChars = 0;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            var o = JsonNode.Parse(data) as JsonObject;
            if (o is null) continue;
            if (o["error"] is JsonNode errNode) throw new AiProviderException((string?)errNode["message"] ?? errNode.ToJsonString());
            if (o["usage"] is JsonObject u)
            {
                promptTokens = (int?)u["prompt_tokens"];
                completionTokens = (int?)u["completion_tokens"];
            }
            var choice = o["choices"]?.AsArray().FirstOrDefault();
            if (choice is null) continue;
            finish = (string?)choice["finish_reason"] ?? finish;
            var delta = choice["delta"];
            var content = (string?)delta?["content"];
            var reasoning = (string?)delta?["reasoning_content"];
            if (delta?["tool_calls"] is JsonArray tcs)
            {
                foreach (var tc in tcs)
                {
                    var idx = (int?)tc?["index"] ?? 0;
                    if (!pending.TryGetValue(idx, out var p)) p = ((string?)tc?["id"] ?? $"call_{idx}", "", new StringBuilder());
                    var fn = tc?["function"];
                    if ((string?)fn?["name"] is { Length: > 0 } nm) p.Name = nm;
                    if ((string?)fn?["arguments"] is { } a) p.Args.Append(a);
                    pending[idx] = p;
                }
                firstToken ??= sw.Elapsed.TotalMilliseconds;
            }
            if (!string.IsNullOrEmpty(content))
            {
                firstToken ??= sw.Elapsed.TotalMilliseconds;
                completionChars += content.Length;
                yield return new ChatChunk(content);
            }
            if (!string.IsNullOrEmpty(reasoning)) yield return new ChatChunk(ThinkingDelta: reasoning);
        }
        List<ToolCall>? calls = null;
        if (pending.Count > 0)
        {
            calls = [];
            foreach (var (_, p) in pending)
            {
                JsonObject args;
                try { args = JsonNode.Parse(p.Args.Length == 0 ? "{}" : p.Args.ToString()) as JsonObject ?? []; }
                catch (JsonException) { args = new JsonObject { ["_unparsed"] = p.Args.ToString() }; }
                calls.Add(new ToolCall(p.Id, p.Name, args));
            }
        }
        var total = sw.Elapsed.TotalMilliseconds;
        double? tps = completionTokens is > 0 && firstToken is not null && total > firstToken ? completionTokens / ((total - firstToken.Value) / 1000) : null;
        yield return new ChatChunk(ToolCalls: calls, Done: true, Usage: new ChatUsage(promptTokens, completionTokens, tps, firstToken, total), FinishReason: finish);
    }

    internal static bool RelaxForReasoningModels(JsonObject body)
    {
        var changed = body.Remove("temperature") | body.Remove("top_p");
        if (body["max_tokens"] is { } mt)
        {
            body.Remove("max_tokens");
            body["max_completion_tokens"] = mt.DeepClone();
            changed = true;
        }
        return changed;
    }
}
