using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RemotePC.Core.AI;
using Xunit;

namespace RemotePC.Core.Tests;

public sealed class CloudSignInTests
{
    [Fact]
    public void Pkce_Matches_Rfc7636_Vector_And_Url_Has_No_Verifier()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", CloudOAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var (v, c) = CloudOAuth.NewPkce();
        Assert.InRange(v.Length, 43, 128);
        var url = CloudOAuth.AuthUrl(c, "http://localhost:5000/callback", "st", "RemotePC AI");
        Assert.StartsWith("https://openrouter.ai/auth?code_challenge=" + c + "&code_challenge_method=S256", url, StringComparison.Ordinal);
        Assert.Contains("callback_url=http%3A%2F%2Flocalhost%3A5000%2Fcallback", url, StringComparison.Ordinal);
        Assert.DoesNotContain(v, url, StringComparison.Ordinal);
        Assert.DoesNotContain("callback_url", CloudOAuth.AuthUrl(c, null, null, "x"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignIn_Exchanges_Code_With_Verifier_Stores_Encrypted_Key_And_Is_Single_Use()
    {
        const string AccountKey = "sk-or-v1-abcdef0123456789";
        string? exchangeBody = null;
        var handler = new CannedHandler((req, body) =>
        {
            if (req.RequestUri!.AbsoluteUri == "https://exchange.test/keys")
            {
                exchangeBody = body;
                return JsonNode.Parse(body)?["code"]?.ToString() == "good-code"
                    ? CannedHandler.Ok($$"""{"key":"{{AccountKey}}"}""")
                    : new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        AiConfig? saved = null;
        using var ai = new AiService(new HttpClient(handler), new AiConfig()) { Secrets = new TestProtector(), Persist = c => saved = c, ExchangeEndpoint = "https://exchange.test/keys" };

        Assert.Throws<ArgumentException>(() => ai.StartSignIn("anthropic")); // no account sign-in for this service
        var (flow, url) = ai.StartSignIn("openrouter");
        var challenge = LoopbackRedirect.ParseQuery(new Uri(url).Query.TrimStart('?'))["code_challenge"];

        await Assert.ThrowsAsync<AiProviderException>(() => ai.FinishSignInAsync(flow, "bad-code", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => ai.FinishSignInAsync(flow, "good-code", CancellationToken.None)); // flow consumed

        (flow, url) = ai.StartSignIn("openrouter");
        challenge = LoopbackRedirect.ParseQuery(new Uri(url).Query.TrimStart('?'))["code_challenge"];
        var ep = await ai.FinishSignInAsync(flow, "good-code", CancellationToken.None);

        var sent = JsonNode.Parse(exchangeBody!)!;
        Assert.Equal("S256", (string?)sent["code_challenge_method"]);
        Assert.Equal(challenge, CloudOAuth.Challenge((string)sent["code_verifier"]!));
        Assert.Equal("cloud-openrouter", ep.Id);
        Assert.Equal("oauth", ep.Auth);
        Assert.DoesNotContain("0123456789", System.Text.Json.JsonSerializer.Serialize(saved), StringComparison.Ordinal);
        Assert.Equal("oauth", (string?)ai.CloudJson()[0]!["auth"]);
        Assert.DoesNotContain(AccountKey, ai.StatusJson().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loopback_Redirect_Accepts_Only_Matching_State()
    {
        using var http = new HttpClient();
        using (var lr = new LoopbackRedirect())
        {
            Assert.StartsWith("http://localhost:", lr.CallbackUrl, StringComparison.Ordinal);
            var wait = lr.WaitForCodeAsync("s1", "Signed in", new CancellationTokenSource(10_000).Token);
            var fav = await http.GetAsync($"http://127.0.0.1:{lr.Port}/favicon.ico");
            Assert.Equal(HttpStatusCode.NotFound, fav.StatusCode);
            var ok = await http.GetStringAsync($"http://127.0.0.1:{lr.Port}/callback?code=abc%2D1&state=s1");
            Assert.Contains("Signed in", ok, StringComparison.Ordinal);
            Assert.Equal("abc-1", await wait);
        }
        using (var lr = new LoopbackRedirect())
        {
            var wait = lr.WaitForCodeAsync("s1", "Signed in", new CancellationTokenSource(10_000).Token);
            var bad = await http.GetAsync($"http://127.0.0.1:{lr.Port}/callback?code=abc&state=forged");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            await Assert.ThrowsAsync<AiProviderException>(() => wait);
        }
    }

    [Fact]
    public void Old_Config_Without_Auth_Field_Loads_As_Key()
    {
        var json = """{"cloudEndpoints":[{"id":"cloud-openai","name":"OpenAI","kind":"openai","baseUrl":"https://api.openai.com/v1/","protectedKey":"AAAA","keyHint":"abcd"}]}""";
        var c = System.Text.Json.JsonSerializer.Deserialize<AiConfig>(json, RemotePC.Core.Protocol.ProtocolJson.Options)!;
        Assert.Equal("key", Assert.Single(c.CloudEndpoints).Auth);
    }
}
