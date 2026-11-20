using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemotePC.Core.Security;
using RemotePC.Core.Session;

namespace RemotePC.Core.AI;

public sealed record AiConfig
{
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public ChatOptions Options { get; init; } = new();
    /// <summary>Extra OpenAI-compatible endpoints added by the PC owner (never auto-discovered off-host).</summary>
    public List<CustomEndpoint> CustomEndpoints { get; init; } = [];
    public int MaxConcurrentInferences { get; init; } = 1;
}

public sealed record CustomEndpoint(string Id, string Name, string Url);

/// <summary>Discovers local AI runtimes, tracks the selected model and runs (rate-limited) inference.</summary>
public sealed class AiService : IDisposable
{
    private readonly HttpClient _http;
    private readonly Lock _lock = new();
    private List<IAiProvider> _providers = [];
    private readonly Dictionary<string, AiProviderStatus> _status = [];
    private readonly Dictionary<string, IReadOnlyList<AiModel>> _models = [];
    private SemaphoreSlim _gate;
    private AiConfig _config;

    public event Action? Changed;
    public Action<AiConfig>? Persist { get; set; }

    public AiService(HttpClient http, AiConfig config)
    {
        _http = http;
        _config = config;
        _gate = new SemaphoreSlim(Math.Clamp(config.MaxConcurrentInferences, 1, 4));
    }

    public AiConfig Config { get { lock (_lock) return _config; } }

    // Old gates replaced by UpdateConfig are not disposed: in-flight requests still release them, and
    // SemaphoreSlim only owns an OS handle if AvailableWaitHandle is touched (never here).
    public void Dispose() => _gate.Dispose();

    /// <summary>Well-known loopback endpoints. OLLAMA_HOST is honoured only when it points at this machine.</summary>
    public static IEnumerable<IAiProvider> DefaultCandidates(HttpClient http)
    {
        var ollama = new Uri("http://127.0.0.1:11434");
        var env = Environment.GetEnvironmentVariable("OLLAMA_HOST");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var s = env.Contains("://", StringComparison.Ordinal) ? env : "http://" + env;
            if (Uri.TryCreate(s, UriKind.Absolute, out var u))
            {
                var host = u.Host is "0.0.0.0" or "" ? "127.0.0.1" : u.Host;
                if (host is "127.0.0.1" or "localhost" or "::1") ollama = new UriBuilder(u) { Host = "127.0.0.1" }.Uri;
            }
        }
        yield return new OllamaProvider(http, ollama);
        yield return new OpenAiCompatibleProvider(http, new Uri("http://127.0.0.1:1234"), "lmstudio", "LM Studio");
        yield return new OpenAiCompatibleProvider(http, new Uri("http://127.0.0.1:8080"), "llamacpp", "llama.cpp server");
    }

    public async Task<IReadOnlyList<AiProviderStatus>> ScanAsync(CancellationToken ct, IEnumerable<IAiProvider>? candidates = null)
    {
        var list = (candidates ?? DefaultCandidates(_http)).ToList();
        foreach (var c in Config.CustomEndpoints)
        {
            if (Uri.TryCreate(c.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
                list.Add(new OpenAiCompatibleProvider(_http, u, c.Id, c.Name));
        }
        var statuses = await Task.WhenAll(list.Select(p => p.ProbeAsync(ct))).ConfigureAwait(false);
        lock (_lock)
        {
            _providers = list;
            _status.Clear();
            foreach (var s in statuses) _status[s.Id] = s;
        }
        Changed?.Invoke();
        return statuses;
    }

    public IReadOnlyList<AiProviderStatus> Statuses { get { lock (_lock) return _status.Values.ToList(); } }

    public IReadOnlyList<AiModel> CachedModels { get { lock (_lock) return _models.Values.SelectMany(m => m).ToList(); } }

    public async Task<IReadOnlyList<AiModel>> RefreshModelsAsync(CancellationToken ct)
    {
        List<IAiProvider> running;
        lock (_lock) running = _providers.Where(p => _status.TryGetValue(p.Id, out var s) && s.Running).ToList();
        var result = new List<AiModel>();
        foreach (var p in running)
        {
            try
            {
                var models = await p.ListModelsAsync(ct).ConfigureAwait(false);
                lock (_lock) _models[p.Id] = models;
                result.AddRange(models);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException)
            {
                lock (_lock) _status[p.Id] = _status[p.Id] with { Error = "model list failed: " + e.Message };
            }
        }
        Changed?.Invoke();
        return result;
    }

    public (IAiProvider Provider, AiModel? Model)? Selected
    {
        get
        {
            lock (_lock)
            {
                var p = _providers.FirstOrDefault(x => x.Id == _config.ProviderId);
                if (p is null) return null;
                var m = _models.GetValueOrDefault(p.Id)?.FirstOrDefault(x => x.Id == _config.ModelId);
                return (p, m);
            }
        }
    }

    public void Select(string providerId, string modelId)
    {
        lock (_lock)
        {
            if (!_providers.Any(p => p.Id == providerId)) throw new ArgumentException("unknown provider");
            if (_models.TryGetValue(providerId, out var ms) && !ms.Any(m => m.Id == modelId)) throw new ArgumentException("unknown model");
            _config = _config with { ProviderId = providerId, ModelId = modelId };
        }
        Persist?.Invoke(Config);
        Changed?.Invoke();
    }

    public void UpdateConfig(Func<AiConfig, AiConfig> change)
    {
        lock (_lock)
        {
            _config = change(_config);
            _gate = new SemaphoreSlim(Math.Clamp(_config.MaxConcurrentInferences, 1, 4));
        }
        Persist?.Invoke(Config);
        Changed?.Invoke();
    }

    /// <summary>Runs a chat on the selected model. Waits for a free inference slot (resource limit).</summary>
    public async IAsyncEnumerable<ChatChunk> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools,
        ChatOptions? options, [EnumeratorCancellation] CancellationToken ct)
    {
        var sel = Selected ?? throw new AiProviderException("No AI model selected on the PC. Open Local AI on the host and choose a model.");
        var model = Config.ModelId ?? throw new AiProviderException("No AI model selected.");
        if (messages.Any(m => m.Images is { Count: > 0 }) && sel.Model?.Vision == false)
            throw new AiProviderException($"The model '{model}' does not support images. Choose a vision-capable model.");
        if (tools is { Count: > 0 } && sel.Model?.Tools == false)
            throw new AiProviderException($"The model '{model}' does not support tool calling, so it cannot operate the PC.");
        var gate = _gate;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var req = new ChatRequest(model, messages) { Tools = tools, Options = Merge(Config.Options, options) };
            await foreach (var c in sel.Provider.ChatAsync(req, ct).ConfigureAwait(false)) yield return c;
        }
        finally { gate.Release(); }
    }

    private static ChatOptions Merge(ChatOptions host, ChatOptions? client) => client is null ? host : new ChatOptions
    {
        Temperature = Clamp(client.Temperature, 0, 2) ?? host.Temperature,
        TopP = Clamp(client.TopP, 0, 1) ?? host.TopP,
        MaxTokens = client.MaxTokens is { } m ? Math.Clamp(m, 1, host.MaxTokens ?? 32_768) : host.MaxTokens,
        ContextLength = host.ContextLength, // only the PC owner controls memory-heavy settings
    };

    private static double? Clamp(double? v, double lo, double hi) => v is null ? null : Math.Clamp(v.Value, lo, hi);

    public async Task<(bool Ok, string Text, ChatUsage? Usage, string? Error)> TestInferenceAsync(CancellationToken ct)
    {
        try
        {
            var text = new System.Text.StringBuilder();
            ChatUsage? usage = null;
            await foreach (var c in ChatAsync([new ChatMessage("user", "Reply with exactly: OK")], null, new ChatOptions { MaxTokens = 16, Temperature = 0 }, ct).ConfigureAwait(false))
            {
                text.Append(c.Delta);
                usage ??= c.Usage;
            }
            return (true, text.ToString(), usage, null);
        }
        catch (Exception e) when (e is AiProviderException or HttpRequestException or JsonException)
        {
            return (false, "", null, e.Message);
        }
    }

    // ---------------- RPC ----------------

    public static JsonNode ModelJson(AiModel m) => JsonSerializer.SerializeToNode(m, Protocol.ProtocolJson.Options)!;

    public void RegisterRpc(RpcRegistry rpc)
    {
        rpc.Register("ai.status", Permission.UseAi, (_, _) => Task.FromResult<JsonNode?>(StatusJson()));
        rpc.Register("ai.models", Permission.UseAi, async (p, ctx) =>
        {
            if ((bool?)p?["rescan"] == true) await ScanAsync(ctx.Cancellation).ConfigureAwait(false);
            await RefreshModelsAsync(ctx.Cancellation).ConfigureAwait(false);
            return StatusJson();
        });
        rpc.Register("ai.select", Permission.UseAi, (p, _) =>
        {
            try { Select((string?)p?["provider"] ?? "", (string?)p?["model"] ?? ""); }
            catch (ArgumentException e) { throw new RpcException("bad_request", e.Message); }
            return Task.FromResult<JsonNode?>(StatusJson());
        });
        rpc.Register("ai.chat", Permission.UseAi, async (p, ctx) =>
        {
            var messages = ParseMessages(p?["messages"]);
            var opts = p?["options"] is JsonObject o ? new ChatOptions
            {
                Temperature = (double?)o["temperature"], TopP = (double?)o["topP"], MaxTokens = (int?)o["maxTokens"],
            } : null;
            var content = new System.Text.StringBuilder();
            ChatUsage? usage = null;
            try
            {
                await foreach (var c in ChatAsync(messages, null, opts, ctx.Cancellation).ConfigureAwait(false))
                {
                    if (c.Delta is not null)
                    {
                        content.Append(c.Delta);
                        ctx.Emit("ai.delta", new JsonObject { ["d"] = c.Delta });
                    }
                    if (c.ThinkingDelta is not null) ctx.Emit("ai.delta", new JsonObject { ["t"] = c.ThinkingDelta });
                    if (c.Done) usage = c.Usage;
                }
            }
            catch (AiProviderException e) { throw new RpcException("ai_error", e.Message); }
            catch (HttpRequestException e) { throw new RpcException("ai_unavailable", "The local AI runtime is not reachable: " + e.Message); }
            return new JsonObject
            {
                ["content"] = content.ToString(),
                ["model"] = Config.ModelId,
                ["usage"] = usage is null ? null : JsonSerializer.SerializeToNode(usage, Protocol.ProtocolJson.Options),
            };
        });
    }

    public const int MaxMessageChars = 400_000;

    internal static List<ChatMessage> ParseMessages(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count is 0 or > 400) throw new RpcException("bad_request", "messages required (max 400)");
        var list = new List<ChatMessage>();
        var total = 0;
        foreach (var m in arr)
        {
            var role = (string?)m?["role"];
            if (role is not ("user" or "assistant" or "system")) throw new RpcException("bad_request", "invalid role");
            var content = (string?)m!["content"] ?? "";
            total += content.Length;
            if (total > MaxMessageChars) throw new RpcException("too_large", "conversation too long; start a new chat or shorten the text");
            var images = m["images"]?.AsArray().Select(i => (string?)i).Where(i => i is { Length: > 0 and < 8_000_000 }).Cast<string>().ToList();
            list.Add(new ChatMessage(role, content) { Images = images is { Count: > 0 } ? images : null });
        }
        return list;
    }

    public JsonObject StatusJson()
    {
        var sel = Selected;
        return new JsonObject
        {
            ["providers"] = JsonSerializer.SerializeToNode(Statuses, Protocol.ProtocolJson.Options),
            ["models"] = new JsonArray(CachedModels.Select(ModelJson).ToArray()),
            ["selected"] = sel is null ? null : new JsonObject { ["provider"] = sel.Value.Provider.Id, ["model"] = Config.ModelId, ["info"] = sel.Value.Model is null ? null : ModelJson(sel.Value.Model) },
        };
    }
}
