using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace RemotePC.Core.AI;

/// <summary>
/// Claude via the official Anthropic C# SDK (cloud). The API key lives only on the PC (DPAPI-protected in the host
/// config) and is never sent to the phone. Thinking is left to the model's default (adaptive on current models), and
/// thinking blocks are handed back unchanged on the next turn through <see cref="ChatMessage.ProviderState"/>.
/// </summary>
public sealed class AnthropicProvider : IAiProvider
{
    private readonly AnthropicClient _client;

    public AnthropicProvider(string id, string name, string apiKey, Uri? baseUrl = null)
    {
        Id = id;
        Name = name;
        Endpoint = baseUrl ?? new Uri("https://api.anthropic.com/");
        _client = baseUrl is null ? new AnthropicClient { ApiKey = apiKey } : new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl.ToString() };
    }

    public string Id { get; }
    public string Kind => "anthropic";
    public string Name { get; }
    public Uri Endpoint { get; }

    public async Task<AiProviderStatus> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var page = await _client.Models.List(cancellationToken: cts.Token).ConfigureAwait(false);
            return new AiProviderStatus(Id, Kind, Name, Endpoint.ToString(), true, null, null, true, true, ["max_tokens"]) { Remote = true };
        }
        catch (Exception e) when (e is AnthropicException or HttpRequestException or OperationCanceledException)
        {
            return new AiProviderStatus(Id, Kind, Name, Endpoint.ToString(), false, null, Describe(e), true, true, []) { Remote = true };
        }
    }

    private static string Describe(Exception e) => e switch
    {
        AnthropicUnauthorizedException => "API key rejected (401). Check the key.",
        AnthropicRateLimitException => "Rate limited (429). Try again shortly.",
        OperationCanceledException => "timed out",
        _ => e.Message,
    };

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct)
    {
        var page = await _client.Models.List(cancellationToken: ct).ConfigureAwait(false);
        var list = new List<AiModel>();
        foreach (var m in page.Items)
        {
            list.Add(new AiModel(Id, m.ID, m.DisplayName, ContextLength: m.MaxInputTokens, Tools: true, Vision: true, Family: "claude"));
        }
        return list;
    }

    public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var p = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.Options.MaxTokens ?? 32_000,
            Messages = ToMessages(request.Messages),
        };
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content));
        if (system.Length > 0) p = p with { System = system };
        if (request.Tools is { Count: > 0 })
        {
            p = p with
            {
                Tools = request.Tools.Select(t => (ToolUnion)new Tool
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = new()
                    {
                        Properties = (t.Parameters["properties"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
                        Required = (t.Parameters["required"] as JsonArray)?.Select(x => (string)x!).ToList() ?? [],
                    },
                }).ToList(),
            };
        }

        var sw = Stopwatch.StartNew();
        double? firstToken = null;
        long inTok = 0, outTok = 0;
        string? stop = null;
        var blocks = new List<ContentBlockParam>();
        var toolCalls = new List<ToolCall>();
        // Per content-block accumulators (index → state)
        var text = new Dictionary<long, StringBuilder>();
        var thinking = new Dictionary<long, (StringBuilder Text, StringBuilder Sig)>();
        var tools = new Dictionary<long, (string Id, string Name, StringBuilder Json)>();
        var order = new List<(long Index, string Kind)>();

        var stream = _client.Messages.CreateStreaming(p, ct);
        await using var e = stream.GetAsyncEnumerator(ct);
        while (true)
        {
            RawMessageStreamEvent ev;
            try
            {
                if (!await e.MoveNextAsync().ConfigureAwait(false)) break;
                ev = e.Current;
            }
            catch (AnthropicException ex) { throw new AiProviderException("Claude: " + Describe(ex), ex); }

            if (ev.TryPickStart(out var start)) inTok = start.Message.Usage.InputTokens;
            else if (ev.TryPickContentBlockStart(out var cbs))
            {
                var cb = cbs.ContentBlock;
                if (cb.TryPickText(out _)) { text[cbs.Index] = new(); order.Add((cbs.Index, "text")); }
                else if (cb.TryPickThinking(out _)) { thinking[cbs.Index] = (new(), new()); order.Add((cbs.Index, "thinking")); }
                else if (cb.TryPickRedactedThinking(out var red)) { blocks.Add(new RedactedThinkingBlockParam { Data = red.Data }); }
                else if (cb.TryPickToolUse(out var tu)) { tools[cbs.Index] = (tu.ID, tu.Name, new()); order.Add((cbs.Index, "tool")); }
            }
            else if (ev.TryPickContentBlockDelta(out var d))
            {
                if (d.Delta.TryPickText(out var t))
                {
                    firstToken ??= sw.Elapsed.TotalMilliseconds;
                    text.GetValueOrDefault(d.Index)?.Append(t.Text);
                    yield return new ChatChunk(Delta: t.Text);
                }
                else if (d.Delta.TryPickThinking(out var th))
                {
                    if (thinking.TryGetValue(d.Index, out var acc)) acc.Text.Append(th.Thinking);
                    if (th.Thinking.Length > 0) yield return new ChatChunk(ThinkingDelta: th.Thinking);
                }
                else if (d.Delta.TryPickSignature(out var sig))
                {
                    if (thinking.TryGetValue(d.Index, out var acc)) acc.Sig.Append(sig.Signature);
                }
                else if (d.Delta.TryPickInputJson(out var ij))
                {
                    if (tools.TryGetValue(d.Index, out var acc)) acc.Json.Append(ij.PartialJson);
                }
            }
            else if (ev.TryPickDelta(out var md))
            {
                stop = StopReasonText(md.Delta.StopReason);
                outTok = md.Usage.OutputTokens;
            }
        }

        // Rebuild the assistant turn in order (thinking → text → tool_use) for the next request.
        foreach (var (index, kind) in order)
        {
            switch (kind)
            {
                case "thinking":
                    var th = thinking[index];
                    blocks.Add(new ThinkingBlockParam { Thinking = th.Text.ToString(), Signature = th.Sig.ToString() });
                    break;
                case "text":
                    var s = text[index].ToString();
                    if (s.Length > 0) blocks.Add(new TextBlockParam { Text = s });
                    break;
                case "tool":
                    var tu = tools[index];
                    JsonObject args;
                    try { args = JsonNode.Parse(tu.Json.Length == 0 ? "{}" : tu.Json.ToString()) as JsonObject ?? []; }
                    catch (JsonException) { args = []; } // truncated input: the agent engine rejects missing arguments
                    toolCalls.Add(new ToolCall(tu.Id, tu.Name, args));
                    blocks.Add(new ToolUseBlockParam
                    {
                        ID = tu.Id,
                        Name = tu.Name,
                        Input = args.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
                    });
                    break;
            }
        }

        if (stop == "refusal")
            yield return new ChatChunk(Delta: "\n\n[Claude declined this request.]");
        var total = sw.Elapsed.TotalMilliseconds;
        var tps = outTok > 0 && firstToken is { } ft && total > ft ? outTok / ((total - ft) / 1000.0) : (double?)null;
        yield return new ChatChunk(ToolCalls: toolCalls.Count > 0 ? toolCalls : null, Done: true,
            Usage: new ChatUsage((int)inTok, (int)outTok, tps, firstToken, total), FinishReason: stop)
        { ProviderState = blocks };
    }

    /// <summary>Wire value of a stop reason ("end_turn", "tool_use", "refusal", …) independent of the SDK enum shape.</summary>
    internal static string? StopReasonText(Anthropic.Core.ApiEnum<string, StopReason>? reason) =>
        reason?.Json is { ValueKind: JsonValueKind.String } j ? j.GetString() : null;

    /// <summary>Maps the provider-neutral history to Messages API turns (tool results → user tool_result blocks).</summary>
    private static List<MessageParam> ToMessages(IReadOnlyList<ChatMessage> history)
    {
        var list = new List<MessageParam>();
        List<ContentBlockParam>? pendingResults = null;
        void Flush()
        {
            if (pendingResults is { Count: > 0 }) list.Add(new UserMessageParam { Content = pendingResults });
            pendingResults = null;
        }
        foreach (var m in history)
        {
            switch (m.Role)
            {
                case "system":
                    continue;
                case "tool":
                    (pendingResults ??= []).Add(new ToolResultBlockParam { ToolUseID = m.ToolCallId ?? "", Content = m.Content });
                    continue;
                case "assistant":
                    Flush();
                    if (m.ProviderState is List<ContentBlockParam> raw && raw.Count > 0)
                        list.Add(new AssistantMessageParam { Content = raw });
                    else
                    {
                        var content = new List<ContentBlockParam>();
                        if (m.Content.Length > 0) content.Add(new TextBlockParam { Text = m.Content });
                        foreach (var t in m.ToolCalls ?? [])
                            content.Add(new ToolUseBlockParam { ID = t.Id, Name = t.Name, Input = t.Arguments.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)) });
                        if (content.Count > 0) list.Add(new AssistantMessageParam { Content = content });
                    }
                    continue;
                default:
                    Flush();
                    var user = new List<ContentBlockParam>();
                    foreach (var img in m.Images ?? [])
                        user.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = img, MediaType = img.StartsWith("/9j/", StringComparison.Ordinal) ? MediaType.ImageJpeg : MediaType.ImagePng } });
                    user.Add(new TextBlockParam { Text = m.Content.Length > 0 ? m.Content : "(empty)" });
                    list.Add(new UserMessageParam { Content = user });
                    continue;
            }
        }
        Flush();
        return list;
    }
}
