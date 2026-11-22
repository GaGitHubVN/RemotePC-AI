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
    /// <summary>Cloud AI accounts added by the PC owner or a paired phone. Keys are DPAPI-protected and never sent out.</summary>
    public List<CloudEndpoint> CloudEndpoints { get; init; } = [];
    public int MaxConcurrentInferences { get; init; } = 1;
}

public sealed record CustomEndpoint(string Id, string Name, string Url);

/// <param name="Kind">"openai" (OpenAI-compatible chat completions) or "anthropic" (Claude Messages API).</param>
/// <param name="ProtectedKey">Base64 of the DPAPI-protected API key.</param>
/// <param name="KeyHint">Last 4 characters, for display only.</param>
public sealed record CloudEndpoint(string Id, string Name, string Kind, string BaseUrl, string ProtectedKey, string KeyHint)
{
    /// <summary>"key" (pasted API key) or "oauth" (key obtained by signing in to the account).</summary>
    public string Auth { get; init; } = "key";
}

/// <summary>Well-known cloud AI services. Custom OpenAI-compatible URLs are allowed too (HTTPS only).</summary>
/// <param name="OAuth">The service lets this app sign in to the user's account and obtain a key (PKCE), see <see cref="CloudOAuth"/>.</param>
public sealed record CloudPreset(string Id, string Name, string Kind, string BaseUrl, string KeyUrl, bool OAuth = false)
{
    public static readonly IReadOnlyList<CloudPreset> All =
    [
        new("anthropic", "Claude (Anthropic)", "anthropic", "https://api.anthropic.com/", "https://platform.claude.com/settings/keys"),
        new("openai", "OpenAI", "openai", "https://api.openai.com/v1/", "https://platform.openai.com/api-keys"),
        new("gemini", "Google Gemini", "openai", "https://generativelanguage.googleapis.com/v1beta/openai/", "https://aistudio.google.com/apikey"),
        new("openrouter", "OpenRouter", "openai", "https://openrouter.ai/api/v1/", "https://openrouter.ai/keys", OAuth: true),
        new("groq", "Groq", "openai", "https://api.groq.com/openai/v1/", "https://console.groq.com/keys"),
        new("deepseek", "DeepSeek", "openai", "https://api.deepseek.com/v1/", "https://platform.deepseek.com/api_keys"),
        new("xai", "xAI Grok", "openai", "https://api.x.ai/v1/", "https://console.x.ai/"),
        new("mistral", "Mistral AI", "openai", "https://api.mistral.ai/v1/", "https://console.mistral.ai/api-keys"),
    ];
}

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
    /// <summary>Protects cloud API keys at rest (DPAPI on Windows). Without it cloud accounts cannot be added.</summary>
    public ISecretProtector? Secrets { get; set; }
    private const string KeyPurpose = "RemotePC.ai.cloud-key";

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
        foreach (var c in Config.CloudEndpoints)
        {
            var key = UnprotectKey(c);
            if (key is null || !Uri.TryCreate(c.BaseUrl, UriKind.Absolute, out var u) || u.Scheme != "https") continue;
            list.Add(c.Kind == "anthropic"
                ? new AnthropicProvider(c.Id, c.Name, key, u.Host == "api.anthropic.com" ? null : u)
                : new OpenAiCompatibleProvider(_http, u, c.Id, c.Name, key, remote: true));
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

    private string? UnprotectKey(CloudEndpoint c)
    {
        if (Secrets is null) return null;
        try { return System.Text.Encoding.UTF8.GetString(Secrets.Unprotect(Convert.FromBase64String(c.ProtectedKey), KeyPurpose)); }
        catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException or InvalidOperationException) { return null; }
    }

    /// <summary>Adds (or replaces) a cloud AI account. The key is encrypted immediately and never returned.</summary>
    public CloudEndpoint AddCloud(string presetId, string apiKey, string? customUrl = null, string? customName = null)
    {
        if (Secrets is null) throw new InvalidOperationException("secret storage unavailable");
        apiKey = apiKey.Trim();
        if (apiKey.Length is < 8 or > 400 || apiKey.Any(char.IsWhiteSpace)) throw new ArgumentException("That does not look like an API key.");
        CloudPreset preset;
        if (presetId == "custom")
        {
            if (!Uri.TryCreate(customUrl?.Trim(), UriKind.Absolute, out var u) || u.Scheme != "https")
                throw new ArgumentException("A custom endpoint must be an https:// URL (OpenAI-compatible).");
            preset = new CloudPreset("custom-" + u.Host.Replace('.', '-'), string.IsNullOrWhiteSpace(customName) ? u.Host : customName.Trim(), "openai", u.ToString(), "");
        }
        else preset = CloudPreset.All.FirstOrDefault(p => p.Id == presetId) ?? throw new ArgumentException("unknown cloud service");
        return Store(preset, apiKey, "key");
    }

    private CloudEndpoint Store(CloudPreset preset, string apiKey, string auth)
    {
        var ep = new CloudEndpoint("cloud-" + preset.Id, preset.Name, preset.Kind, preset.BaseUrl,
            Convert.ToBase64String(Secrets!.Protect(System.Text.Encoding.UTF8.GetBytes(apiKey), KeyPurpose)), apiKey[^4..]) { Auth = auth };
        UpdateConfig(c => c with { CloudEndpoints = [.. c.CloudEndpoints.Where(x => x.Id != ep.Id), ep] });
        return ep;
    }

    // ---- account sign-in (OAuth PKCE) ----

    private sealed record PendingSignIn(string Preset, string Verifier, DateTimeOffset Expires);
    private readonly Dictionary<string, PendingSignIn> _signIns = [];
    /// <summary>Token exchange endpoint override (tests only).</summary>
    internal string ExchangeEndpoint { get; set; } = CloudOAuth.OpenRouterExchange;

    /// <summary>
    /// Starts an account sign-in. The verifier stays on this PC; the returned URL is opened in a browser (PC or phone).
    /// With <paramref name="callback"/> null the service shows a one-time code that the user pastes back.
    /// </summary>
    public (string FlowId, string Url) StartSignIn(string presetId, string? callback = null, string? state = null)
    {
        if (Secrets is null) throw new InvalidOperationException("secret storage unavailable");
        var preset = CloudPreset.All.FirstOrDefault(p => p.Id == presetId && p.OAuth)
            ?? throw new ArgumentException("This service does not support signing in with an account; use an API key.");
        var (verifier, challenge) = CloudOAuth.NewPkce();
        var id = CloudOAuth.NewState();
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var k in _signIns.Where(x => x.Value.Expires < now).Select(x => x.Key).ToList()) _signIns.Remove(k);
            if (_signIns.Count >= 8) _signIns.Remove(_signIns.MinBy(x => x.Value.Expires).Key);
            _signIns[id] = new PendingSignIn(preset.Id, verifier, now.AddMinutes(10));
        }
        return (id, CloudOAuth.AuthUrl(challenge, callback, state, "RemotePC AI - " + Environment.MachineName));
    }

    /// <summary>Exchanges the sign-in code for the account key and stores it like a pasted key. One use per flow.</summary>
    public async Task<CloudEndpoint> FinishSignInAsync(string flowId, string code, CancellationToken ct)
    {
        PendingSignIn? p;
        lock (_lock)
        {
            if (_signIns.Remove(flowId, out p) && p.Expires < DateTimeOffset.UtcNow) p = null;
        }
        if (p is null) throw new ArgumentException("This sign-in has expired. Start again.");
        var key = await CloudOAuth.ExchangeAsync(_http, code, p.Verifier, ct, ExchangeEndpoint).ConfigureAwait(false);
        return Store(CloudPreset.All.First(x => x.Id == p.Preset), key, "oauth");
    }

    public void RemoveCloud(string id)
    {
        UpdateConfig(c => c with
        {
            CloudEndpoints = [.. c.CloudEndpoints.Where(x => x.Id != id)],
            ProviderId = c.ProviderId == id ? null : c.ProviderId,
            ModelId = c.ProviderId == id ? null : c.ModelId,
        });
        lock (_lock)
        {
            _providers.RemoveAll(p => p.Id == id);
            _status.Remove(id);
            _models.Remove(id);
        }
        Changed?.Invoke();
    }

    public JsonArray CloudJson() => new([.. Config.CloudEndpoints.Select(c => (JsonNode)new JsonObject
    {
        ["id"] = c.Id, ["name"] = c.Name, ["kind"] = c.Kind, ["baseUrl"] = c.BaseUrl, ["keyHint"] = "…" + c.KeyHint, ["auth"] = c.Auth,
    })]);

    public static JsonArray PresetsJson() => new([.. CloudPreset.All.Select(p => (JsonNode)new JsonObject
    {
        ["id"] = p.Id, ["name"] = p.Name, ["kind"] = p.Kind, ["keyUrl"] = p.KeyUrl, ["oauth"] = p.OAuth,
    })]);

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
        rpc.Register("ai.cloud.list", Permission.UseAi, (_, _) =>
            Task.FromResult<JsonNode?>(new JsonObject { ["presets"] = PresetsJson(), ["accounts"] = CloudJson() }));
        rpc.Register("ai.cloud.add", Permission.UseAi, async (p, ctx) =>
        {
            CloudEndpoint ep;
            try { ep = AddCloud((string?)p?["preset"] ?? "", (string?)p?["apiKey"] ?? "", (string?)p?["url"], (string?)p?["name"]); }
            catch (ArgumentException e) { throw new RpcException("bad_request", e.Message); }
            catch (InvalidOperationException e) { throw new RpcException("unavailable", e.Message); }
            return await AddedJsonAsync(ep, ctx.Cancellation).ConfigureAwait(false);
        });
        rpc.Register("ai.cloud.signin.start", Permission.UseAi, (p, _) =>
        {
            try
            {
                var (id, url) = StartSignIn((string?)p?["preset"] ?? "");
                return Task.FromResult<JsonNode?>(new JsonObject { ["flowId"] = id, ["url"] = url, ["expiresIn"] = 600 });
            }
            catch (ArgumentException e) { throw new RpcException("bad_request", e.Message); }
            catch (InvalidOperationException e) { throw new RpcException("unavailable", e.Message); }
        });
        rpc.Register("ai.cloud.signin.finish", Permission.UseAi, async (p, ctx) =>
        {
            CloudEndpoint ep;
            try { ep = await FinishSignInAsync((string?)p?["flowId"] ?? "", (string?)p?["code"] ?? "", ctx.Cancellation).ConfigureAwait(false); }
            catch (ArgumentException e) { throw new RpcException("bad_request", e.Message); }
            catch (AiProviderException e) { throw new RpcException("ai_error", e.Message); }
            catch (HttpRequestException e) { throw new RpcException("ai_unavailable", "The sign-in service is not reachable: " + e.Message); }
            return await AddedJsonAsync(ep, ctx.Cancellation).ConfigureAwait(false);
        });
        rpc.Register("ai.cloud.remove", Permission.UseAi, (p, _) =>
        {
            RemoveCloud((string?)p?["id"] ?? "");
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
            catch (HttpRequestException e) { throw new RpcException("ai_unavailable", "The AI service is not reachable: " + e.Message); }
            return new JsonObject
            {
                ["content"] = content.ToString(),
                ["model"] = Config.ModelId,
                ["usage"] = usage is null ? null : JsonSerializer.SerializeToNode(usage, Protocol.ProtocolJson.Options),
            };
        });
    }

    /// <summary>Probes the new account, refreshes models and, when nothing is selected yet, selects a sensible model.</summary>
    public async Task<JsonObject> AddedJsonAsync(CloudEndpoint ep, CancellationToken ct)
    {
        await ScanAsync(ct).ConfigureAwait(false);
        await RefreshModelsAsync(ct).ConfigureAwait(false);
        var st = Statuses.FirstOrDefault(s => s.Id == ep.Id);
        if (st?.Running == true && Selected is null && DefaultModel(ep.Id) is { } m)
        {
            try { Select(ep.Id, m); } catch (ArgumentException) { }
        }
        var o = StatusJson();
        o["added"] = new JsonObject { ["id"] = ep.Id, ["ok"] = st?.Running == true, ["error"] = st?.Error };
        return o;
    }

    internal string? DefaultModel(string providerId)
    {
        List<AiModel> ms;
        lock (_lock) ms = [.. _models.GetValueOrDefault(providerId) ?? []];
        string[] prefer = ["claude-sonnet", "anthropic/claude-sonnet", "gpt-5", "openai/gpt-5", "gemini-3", "gemini-2.5-pro", "grok-4", "mistral-large", "deepseek-chat"];
        foreach (var pre in prefer)
            if (ms.FirstOrDefault(m => m.Id.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit.Id;
        return ms.FirstOrDefault()?.Id;
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
            ["cloud"] = CloudJson(),
            ["selected"] = sel is null ? null : new JsonObject { ["provider"] = sel.Value.Provider.Id, ["model"] = Config.ModelId, ["info"] = sel.Value.Model is null ? null : ModelJson(sel.Value.Model) },
        };
    }
}
