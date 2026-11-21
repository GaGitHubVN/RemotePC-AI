using System.Text.Json.Nodes;

namespace RemotePC.Core.AI;

/// <summary>Model metadata. Null means the provider did not report it — never guessed.</summary>
public sealed record AiModel(
    string ProviderId, string Id, string Name,
    long? ContextLength = null, bool? Tools = null, bool? Vision = null,
    string? Family = null, string? ParameterSize = null, string? Quantization = null, long? SizeBytes = null);

public sealed record AiProviderStatus(
    string Id, string Kind, string Name, string Endpoint, bool Running, string? Version, string? Error,
    bool SupportsStreaming, bool SupportsTools, IReadOnlyList<string> SupportedOptions)
{
    /// <summary>True for cloud APIs: prompts (and, for the agent, screenshots/file text) leave this PC.</summary>
    public bool Remote { get; init; }
}

public sealed record ToolCall(string Id, string Name, JsonObject Arguments);

public sealed record ChatMessage(string Role, string Content)
{
    /// <summary>Base64 images (PNG/JPEG), only sent when the model reports vision support.</summary>
    public IReadOnlyList<string>? Images { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    /// <summary>Provider-specific copy of an assistant turn (e.g. Claude thinking blocks) replayed unchanged. Never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public object? ProviderState { get; init; }
}

public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

public sealed record ChatOptions
{
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? MaxTokens { get; init; }
    public int? ContextLength { get; init; }
}

public sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages)
{
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public ChatOptions Options { get; init; } = new();
}

public sealed record ChatUsage(int? PromptTokens, int? CompletionTokens, double? TokensPerSecond, double? FirstTokenMs, double TotalMs);

/// <summary>One streamed piece of a response.</summary>
public sealed record ChatChunk(string? Delta = null, string? ThinkingDelta = null, IReadOnlyList<ToolCall>? ToolCalls = null, bool Done = false, ChatUsage? Usage = null, string? FinishReason = null)
{
    /// <summary>On the final chunk: what the caller should store in <see cref="ChatMessage.ProviderState"/>.</summary>
    public object? ProviderState { get; init; }
}

public interface IAiProvider
{
    string Id { get; }
    string Kind { get; }
    string Name { get; }
    Uri Endpoint { get; }
    Task<AiProviderStatus> ProbeAsync(CancellationToken ct);
    Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct);
    IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, CancellationToken ct);
}

public sealed class AiProviderException(string message, Exception? inner = null) : Exception(message, inner);
