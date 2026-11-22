using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RemotePC.Core.AI;

/// <summary>
/// "Sign in with your account" for cloud AI services that officially let third-party apps obtain a key through an
/// OAuth PKCE login (today: OpenRouter, which then gives access to Claude, GPT, Gemini and others, billed to the
/// user's own OpenRouter account). The code verifier never leaves this PC; the resulting key is DPAPI-protected
/// like a pasted one. Consumer chat subscriptions (ChatGPT Plus, Claude Pro, Gemini app) do not offer such a login
/// to third-party apps, so they are deliberately not imitated here.
/// </summary>
public static class CloudOAuth
{
    public const string OpenRouterAuth = "https://openrouter.ai/auth";
    public const string OpenRouterExchange = "https://openrouter.ai/api/v1/auth/keys";

    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, Challenge(verifier));
    }

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(18));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <param name="callback">A http://localhost:port/… URL (PC browser), or null: OpenRouter then shows the code to paste (phone).</param>
    public static string AuthUrl(string challenge, string? callback, string? state, string keyLabel)
    {
        var q = new StringBuilder(OpenRouterAuth).Append("?code_challenge=").Append(challenge).Append("&code_challenge_method=S256")
            .Append("&key_label=").Append(Uri.EscapeDataString(keyLabel));
        if (callback is not null) q.Append("&callback_url=").Append(Uri.EscapeDataString(callback));
        if (state is not null) q.Append("&state=").Append(Uri.EscapeDataString(state));
        return q.ToString();
    }

    /// <summary>Exchanges the one-time code (valid 10 minutes) for the account's API key.</summary>
    public static async Task<string> ExchangeAsync(HttpClient http, string code, string verifier, CancellationToken ct, string endpoint = OpenRouterExchange)
    {
        code = code.Trim();
        if (code.Length is < 4 or > 512 || code.Any(char.IsWhiteSpace)) throw new ArgumentException("That does not look like a sign-in code.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await http.PostAsJsonAsync(endpoint, new JsonObject
        {
            ["code"] = code, ["code_verifier"] = verifier, ["code_challenge_method"] = "S256",
        }, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Observed: 400 {"error":{"message":"Invalid code"}} for a wrong code; the docs list 403 for wrong/expired codes.
            var msg = resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest
                ? "The sign-in code is wrong or has expired (codes are valid for 10 minutes). Sign in again."
                : $"Sign-in failed ({(int)resp.StatusCode}).";
            throw new AiProviderException(msg);
        }
        var body = await resp.Content.ReadFromJsonAsync<JsonObject>(cts.Token).ConfigureAwait(false);
        var key = (string?)body?["key"];
        if (string.IsNullOrWhiteSpace(key)) throw new AiProviderException("The service did not return a key.");
        return key;
    }
}

/// <summary>
/// One-shot loopback HTTP receiver for the browser redirect of a sign-in started on this PC. Listens on 127.0.0.1
/// (and ::1 when available) only, accepts a single GET /callback with the expected state and stops.
/// </summary>
public sealed class LoopbackRedirect : IDisposable
{
    private readonly TcpListener _v4;
    private readonly TcpListener? _v6;
    public int Port { get; }
    public string CallbackUrl => $"http://localhost:{Port}/callback";

    public LoopbackRedirect()
    {
        _v4 = new TcpListener(IPAddress.Loopback, 0);
        _v4.Start();
        Port = ((IPEndPoint)_v4.LocalEndpoint).Port;
        try
        {
            _v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
            _v6.Start();
        }
        catch (SocketException) { _v6 = null; }
    }

    /// <summary>Waits for the redirect and returns the code. Throws on a state mismatch, denial or timeout.</summary>
    public async Task<string> WaitForCodeAsync(string state, string successHtml, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var accepts = new Dictionary<Task<TcpClient>, TcpListener> { [_v4.AcceptTcpClientAsync(cts.Token).AsTask()] = _v4 };
        if (_v6 is not null) accepts[_v6.AcceptTcpClientAsync(cts.Token).AsTask()] = _v6;
        try
        {
            while (true)
            {
                var done = await Task.WhenAny(accepts.Keys).ConfigureAwait(false);
                var listener = accepts[done];
                accepts.Remove(done);
                using var client = await done.ConfigureAwait(false);
                var (path, query) = await ReadRequestLineAsync(client, cts.Token).ConfigureAwait(false);
                if (path != "/callback")
                {
                    // Browsers also ask for /favicon.ico and the like: answer and keep listening.
                    await RespondAsync(client, 404, "Not found", cts.Token).ConfigureAwait(false);
                    accepts[listener.AcceptTcpClientAsync(cts.Token).AsTask()] = listener;
                    continue;
                }
                var q = ParseQuery(query);
                var code = q.GetValueOrDefault("code");
                var ok = code is { Length: > 0 } && q.GetValueOrDefault("state") == state;
                await RespondAsync(client, ok ? 200 : 400, ok ? successHtml : "Sign-in was cancelled or did not match this request. You can close this tab.", cts.Token).ConfigureAwait(false);
                if (!ok) throw new AiProviderException(q.ContainsKey("code") ? "Sign-in response did not match (state)." : "Sign-in was cancelled.");
                return code!;
            }
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            foreach (var t in accepts.Keys) _ = t.ContinueWith(x => { if (x.IsCompletedSuccessfully) x.Result.Dispose(); }, TaskScheduler.Default);
        }
    }

    private static async Task<(string Path, string Query)> ReadRequestLineAsync(TcpClient c, CancellationToken ct)
    {
        var s = c.GetStream();
        var buf = new byte[8192];
        var n = 0;
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromSeconds(10));
        while (n < buf.Length)
        {
            var r = await s.ReadAsync(buf.AsMemory(n), t.Token).ConfigureAwait(false);
            if (r == 0) break;
            n += r;
            if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0].Split(' ');
        if (line.Length < 2 || line[0] != "GET") return ("", "");
        var target = line[1];
        var i = target.IndexOf('?');
        return i < 0 ? (target, "") : (target[..i], target[(i + 1)..]);
    }

    internal static Dictionary<string, string> ParseQuery(string q)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            d[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return d;
    }

    private static async Task RespondAsync(TcpClient c, int status, string message, CancellationToken ct)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>RemotePC AI</title><body style=\"font-family:system-ui;padding:40px\"><h2>RemotePC AI</h2><p>{WebUtility.HtmlEncode(message)}</p></body>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        var s = c.GetStream();
        await s.WriteAsync(head, ct).ConfigureAwait(false);
        await s.WriteAsync(body, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _v4.Stop();
        _v6?.Stop();
    }
}
