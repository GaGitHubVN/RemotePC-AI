using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RemotePC.Core.AI;
using Xunit;

namespace RemotePC.Core.Tests;

public sealed class CloudAiTests
{
    private const string Key = "sk-test-0123456789abcdef";

    [Fact]
    public void Cloud_Key_Is_Encrypted_At_Rest_And_Never_Listed()
    {
        AiConfig? saved = null;
        using var ai = new AiService(new HttpClient(), new AiConfig()) { Secrets = new TestProtector(), Persist = c => saved = c };
        var ep = ai.AddCloud("openai", Key);

        Assert.NotNull(saved);
        var stored = Assert.Single(saved!.CloudEndpoints);
        Assert.Equal("cloud-openai", stored.Id);
        Assert.DoesNotContain(Key, stored.ProtectedKey, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789", System.Text.Json.JsonSerializer.Serialize(saved), StringComparison.Ordinal);
        Assert.Equal("cdef", ep.KeyHint);

        var listed = ai.CloudJson().ToJsonString() + ai.StatusJson().ToJsonString();
        Assert.DoesNotContain(Key, listed, StringComparison.Ordinal);
        Assert.Equal("…cdef", (string?)ai.CloudJson()[0]!["keyHint"]);

        ai.RemoveCloud("cloud-openai");
        Assert.Empty(ai.Config.CloudEndpoints);
    }

    [Theory]
    [InlineData("custom", "http://example.com/v1/")]   // plain HTTP refused for cloud keys
    [InlineData("custom", "not a url")]
    [InlineData("nope", null)]
    public void Rejects_Unsafe_Or_Unknown_Endpoints(string preset, string? url)
    {
        using var ai = new AiService(new HttpClient(), new AiConfig()) { Secrets = new TestProtector() };
        Assert.Throws<ArgumentException>(() => ai.AddCloud(preset, Key, url));
        Assert.Throws<ArgumentException>(() => ai.AddCloud("openai", "short"));
    }

    [Fact]
    public async Task Prefixed_Cloud_Base_Url_Bearer_Key_Model_Filter_And_Reasoning_Retry()
    {
        var seen = new List<string>();
        var auth = new List<string?>();
        var bodies = new List<string>();
        var handler = new CannedHandler((req, body) =>
        {
            seen.Add(req.RequestUri!.ToString());
            auth.Add(req.Headers.Authorization?.ToString());
            if (req.RequestUri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
                return CannedHandler.Ok("""{"data":[{"id":"models/gemini-3-pro"},{"id":"text-embedding-004"},{"id":"gpt-image-1"}]}""");
            bodies.Add(body);
            if (bodies.Count == 1)
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"Unsupported parameter: 'max_tokens'"}}""", Encoding.UTF8, "application/json") };
            return CannedHandler.Ok("data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\ndata: [DONE]\n\n", "text/event-stream");
        });
        var p = new OpenAiCompatibleProvider(new HttpClient(handler), new Uri("https://generativelanguage.googleapis.com/v1beta/openai/"), "cloud-gemini", "Gemini", Key, remote: true);

        var status = await p.ProbeAsync(CancellationToken.None);
        Assert.True(status.Running);
        Assert.True(status.Remote);
        var models = await p.ListModelsAsync(CancellationToken.None);
        Assert.Equal(["gemini-3-pro"], models.Select(m => m.Id));

        var text = new StringBuilder();
        await foreach (var c in p.ChatAsync(new ChatRequest("gemini-3-pro", [new ChatMessage("user", "hi")]) { Options = new ChatOptions { Temperature = 0.2, MaxTokens = 50 } }, CancellationToken.None))
            text.Append(c.Delta);

        Assert.Equal("OK", text.ToString());
        Assert.All(seen, u => Assert.StartsWith("https://generativelanguage.googleapis.com/v1beta/openai/", u, StringComparison.Ordinal));
        Assert.Contains("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", seen);
        Assert.All(auth, a => Assert.Equal("Bearer " + Key, a));
        var retry = JsonNode.Parse(bodies[1])!.AsObject();
        Assert.False(retry.ContainsKey("temperature"));
        Assert.False(retry.ContainsKey("max_tokens"));
        Assert.Equal(50, (int)retry["max_completion_tokens"]!);
    }

    [Fact]
    public async Task Local_Bare_Host_Still_Uses_V1()
    {
        var urls = new List<string>();
        var handler = new CannedHandler((req, _) => { urls.Add(req.RequestUri!.ToString()); return CannedHandler.Ok("""{"data":[]}"""); });
        var p = new OpenAiCompatibleProvider(new HttpClient(handler), new Uri("http://127.0.0.1:1234"), "lmstudio", "LM Studio");
        _ = await p.ListModelsAsync(CancellationToken.None);
        Assert.Equal("http://127.0.0.1:1234/v1/models", Assert.Single(urls));
    }
}
