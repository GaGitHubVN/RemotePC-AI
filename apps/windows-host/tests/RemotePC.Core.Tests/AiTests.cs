using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RemotePC.Core.AI;
using Xunit;
using Xunit.Abstractions;

namespace RemotePC.Core.Tests;

/// <summary>TEST-ONLY HTTP handler replaying canned provider responses.</summary>
internal sealed class CannedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(string Path, string Body)> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.RequestUri!.AbsolutePath, body));
        return respond(request, body);
    }
    public static HttpResponseMessage Ok(string body, string type = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
}

public class AiTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Ollama_Parses_Models_Capabilities_And_Streams_With_Tool_Calls()
    {
        var handler = new CannedHandler((req, body) => req.RequestUri!.AbsolutePath switch
        {
            "/api/version" => CannedHandler.Ok("""{"version":"0.40.16"}"""),
            "/api/tags" => CannedHandler.Ok("""{"models":[{"name":"qwen2.5:0.5b","size":397000000,"details":{"family":"qwen2","parameter_size":"494M","quantization_level":"Q4_K_M"}}]}"""),
            "/api/show" => CannedHandler.Ok("""{"capabilities":["completion","tools"],"model_info":{"qwen2.context_length":32768}}"""),
            "/api/chat" => CannedHandler.Ok(
                "{\"message\":{\"role\":\"assistant\",\"content\":\"Xin \"},\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"chào\"},\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"screen.capture\",\"arguments\":{\"monitor\":0}}}]},\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":12,\"eval_count\":20,\"eval_duration\":500000000}\n", "application/x-ndjson"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var p = new OllamaProvider(new HttpClient(handler), new Uri("http://127.0.0.1:11434"));
        Assert.True((await p.ProbeAsync(default)).Running);
        var m = Assert.Single(await p.ListModelsAsync(default));
        Assert.Equal(32768, m.ContextLength);
        Assert.True(m.Tools);
        Assert.False(m.Vision);
        Assert.Equal("Q4_K_M", m.Quantization);

        var chunks = new List<ChatChunk>();
        await foreach (var c in p.ChatAsync(new ChatRequest("qwen2.5:0.5b", [new ChatMessage("user", "hi")])
        {
            Tools = [new ToolDefinition("screen.capture", "x", new JsonObject { ["type"] = "object" })],
            Options = new ChatOptions { Temperature = 0.2, MaxTokens = 50 },
        }, default)) chunks.Add(c);
        Assert.Equal("Xin chào", string.Concat(chunks.Select(c => c.Delta)));
        var call = chunks.SelectMany(c => c.ToolCalls ?? []).Single();
        Assert.Equal("screen.capture", call.Name);
        Assert.Equal(0, (int)call.Arguments["monitor"]!);
        var usage = chunks.Last().Usage!;
        Assert.Equal(40, usage.TokensPerSecond!.Value, 1);

        var sent = JsonNode.Parse(handler.Requests.Last(r => r.Path == "/api/chat").Body)!;
        Assert.Equal(0.2, (double)sent["options"]!["temperature"]!);
        Assert.Equal(50, (int)sent["options"]!["num_predict"]!);
        Assert.Equal("screen.capture", (string?)sent["tools"]![0]!["function"]!["name"]);
    }

    [Fact]
    public async Task OpenAi_Compatible_Reassembles_Fragmented_Tool_Arguments()
    {
        var sse = string.Join("\n\n", [
            """data: {"choices":[{"delta":{"content":"Đang "}}]}""",
            """data: {"choices":[{"delta":{"content":"mở"}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"app.launch","arguments":"{\"na"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"me\":\"notepad\"}"}}]},"finish_reason":"tool_calls"}]}""",
            """data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":7}}""",
            "data: [DONE]", ""]);
        var handler = new CannedHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => CannedHandler.Ok("""{"data":[{"id":"llama-3.2-3b","max_context_length":8192}]}"""),
            "/v1/chat/completions" => CannedHandler.Ok(sse, "text/event-stream"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var p = new OpenAiCompatibleProvider(new HttpClient(handler), new Uri("http://127.0.0.1:1234"), "lmstudio", "LM Studio");
        Assert.True((await p.ProbeAsync(default)).Running);
        var m = Assert.Single(await p.ListModelsAsync(default));
        Assert.Equal(8192, m.ContextLength);
        Assert.Null(m.Tools); // unknown, not invented

        var chunks = new List<ChatChunk>();
        await foreach (var c in p.ChatAsync(new ChatRequest("llama-3.2-3b", [new ChatMessage("user", "x") { Images = ["iVBOR"] }]), default)) chunks.Add(c);
        Assert.Equal("Đang mở", string.Concat(chunks.Select(c => c.Delta)));
        var call = chunks.Last().ToolCalls!.Single();
        Assert.Equal("app.launch", call.Name);
        Assert.Equal("notepad", (string?)call.Arguments["name"]);
        Assert.Equal("tool_calls", chunks.Last().FinishReason);
        Assert.Equal(7, chunks.Last().Usage!.CompletionTokens);
        var sent = JsonNode.Parse(handler.Requests.Last().Body)!;
        Assert.Equal("image_url", (string?)sent["messages"]![0]!["content"]![1]!["type"]);
    }

    [Fact]
    public async Task Probe_Reports_Not_Running_On_Connection_Refused()
    {
        var p = new OllamaProvider(new HttpClient(), new Uri("http://127.0.0.1:1")); // nothing listens on port 1
        var s = await p.ProbeAsync(default);
        Assert.False(s.Running);
        Assert.NotNull(s.Error);
    }

    [Fact]
    public async Task Service_Refuses_Images_For_Non_Vision_Model()
    {
        var handler = new CannedHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/api/version" => CannedHandler.Ok("""{"version":"1"}"""),
            "/api/tags" => CannedHandler.Ok("""{"models":[{"name":"m"}]}"""),
            "/api/show" => CannedHandler.Ok("""{"capabilities":["completion"]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var http = new HttpClient(handler);
        var svc = new AiService(http, new AiConfig());
        await svc.ScanAsync(default, [new OllamaProvider(http, new Uri("http://127.0.0.1:11434"))]);
        await svc.RefreshModelsAsync(default);
        svc.Select("ollama", "m");
        var ex = await Assert.ThrowsAsync<AiProviderException>(async () =>
        {
            await foreach (var _ in svc.ChatAsync([new ChatMessage("user", "x") { Images = ["a"] }], null, null, default)) { }
        });
        Assert.Contains("does not support images", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<AiProviderException>(async () =>
        {
            await foreach (var _ in svc.ChatAsync([new ChatMessage("user", "x")], [new ToolDefinition("t", "d", [])], null, default)) { }
        });
        Assert.Throws<ArgumentException>(() => svc.Select("ollama", "not-installed"));
    }

    [Fact]
    public void Message_Validation_Limits()
    {
        Assert.Throws<Session.RpcException>(() => AiService.ParseMessages(new JsonArray()));
        Assert.Throws<Session.RpcException>(() => AiService.ParseMessages(new JsonArray(new JsonObject { ["role"] = "tool", ["content"] = "x" })));
        Assert.Throws<Session.RpcException>(() => AiService.ParseMessages(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new string('a', AiService.MaxMessageChars + 1) })));
    }

    /// <summary>Real local runtime. Requires Ollama with qwen2.5:0.5b pulled; excluded from CI via the trait.</summary>
    [Fact]
    [Trait("Category", "LocalAI")]
    public async Task Real_Ollama_Streams_And_Calls_Tools()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var svc = new AiService(http, new AiConfig());
        var statuses = await svc.ScanAsync(default);
        foreach (var s in statuses) output.WriteLine($"{s.Name} @ {s.Endpoint}: running={s.Running} {s.Version} {s.Error}");
        Assert.Contains(statuses, s => s.Id == "ollama" && s.Running);
        var models = await svc.RefreshModelsAsync(default);
        foreach (var m in models) output.WriteLine($"model {m.Id} ctx={m.ContextLength} tools={m.Tools} vision={m.Vision}");
        var model = models.First(m => m.Id.StartsWith("qwen2.5", StringComparison.Ordinal));
        svc.Select("ollama", model.Id);

        var test = await svc.TestInferenceAsync(default);
        output.WriteLine($"test inference: ok={test.Ok} '{test.Text}' firstToken={test.Usage?.FirstTokenMs:F0}ms tps={test.Usage?.TokensPerSecond:F1}");
        Assert.True(test.Ok, test.Error);
        Assert.False(string.IsNullOrWhiteSpace(test.Text));

        var tools = new[] { new ToolDefinition("screen_capture", "Capture a screenshot of the PC screen and send it to the user.", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray() }) };
        var calls = new List<ToolCall>();
        await foreach (var c in svc.ChatAsync([new ChatMessage("system", "You can call tools."), new ChatMessage("user", "Take a screenshot of my PC.")], tools, new ChatOptions { Temperature = 0 }, default))
            calls.AddRange(c.ToolCalls ?? []);
        output.WriteLine("tool calls: " + string.Join(", ", calls.Select(c => c.Name + c.Arguments.ToJsonString())));
        Assert.Contains(calls, c => c.Name == "screen_capture");
    }
}
