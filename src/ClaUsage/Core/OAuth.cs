using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace ClaUsage.Core;

/// <summary>What a provider's sign-in asks the UI to show.</summary>
public interface ILoginUi
{
    /// <summary>The browser was opened at <paramref name="url"/>; the user may also paste a code or callback URL.</summary>
    void ShowBrowser(Uri url);

    /// <summary>A device code flow: show <paramref name="userCode"/> and a button to open <paramref name="verifyUrl"/>.</summary>
    void ShowDeviceCode(string userCode, Uri verifyUrl);

    /// <summary>Asks for an API key, with a link to the page that creates one.</summary>
    Task<string> RequestApiKey(Uri createKeyUrl, string hint);

    /// <summary>Completes when the user pastes a code and presses Continue.</summary>
    Task<string> PastedCode { get; }
}

public readonly record struct Pkce(string Verifier, string Challenge)
{
    public static Pkce Generate()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new Pkce(verifier, challenge);
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// A one-shot HTTP listener on 127.0.0.1 (and ::1 when available) that waits for the OAuth redirect.
/// Raw sockets instead of HttpListener so no URL ACL or admin rights are needed.
/// </summary>
public sealed class LoopbackServer : IDisposable
{
    private readonly List<TcpListener> _listeners = new();
    private readonly string _path;
    private readonly string _state;
    private readonly TaskCompletionSource<string> _code = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();

    public string RedirectUri { get; }
    public Task<string> Code => _code.Task;

    private LoopbackServer(int port, string path, string state)
    {
        _path = path;
        _state = state;

        var v4 = new TcpListener(IPAddress.Loopback, port);
        v4.Start();
        _listeners.Add(v4);
        var actualPort = ((IPEndPoint)v4.LocalEndpoint).Port;
        try
        {
            var v6 = new TcpListener(IPAddress.IPv6Loopback, actualPort);
            v6.Start();
            _listeners.Add(v6);
        }
        catch (SocketException)
        {
            // IPv6 loopback unavailable or taken: 127.0.0.1 alone is fine.
        }

        RedirectUri = $"http://localhost:{actualPort}{path}";
        foreach (var listener in _listeners) _ = Task.Run(() => AcceptLoop(listener));
    }

    /// <summary>Throws <see cref="SocketException"/> when a fixed port is already in use.</summary>
    public static LoopbackServer Start(string state, int port = 0, string path = "/callback") => new(port, path, state);

    private async Task AcceptLoop(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var firstLine = request.Split("\r\n", 2)[0].Split(' ');
                if (firstLine.Length < 2 || firstLine[0] != "GET")
                {
                    await Reply(stream, 405, "Only GET is supported.").ConfigureAwait(false);
                    return;
                }
                var uri = new Uri("http://localhost" + firstLine[1]);
                if (uri.AbsolutePath != _path)
                {
                    await Reply(stream, 404, "Not found.").ConfigureAwait(false);
                    return;
                }
                var query = HttpUtility.ParseQueryString(uri.Query);
                var error = query["error"];
                if (!string.IsNullOrEmpty(error))
                {
                    await Reply(stream, 400, "Sign-in failed. You can close this tab and try again in ClaUsage.").ConfigureAwait(false);
                    _code.TrySetException(new ProviderException($"Authorization failed: {query["error_description"] ?? error}"));
                    return;
                }
                var code = query["code"];
                if (string.IsNullOrEmpty(code) || query["state"] != _state)
                {
                    await Reply(stream, 400, "This sign-in link doesn't match. Try again from ClaUsage.").ConfigureAwait(false);
                    return;
                }
                await Reply(stream, 200, "Signed in. You can close this tab and go back to ClaUsage.").ConfigureAwait(false);
                _code.TrySetResult(code);
            }
            catch
            {
                // A broken browser connection is not a failed sign-in.
            }
        }
    }

    private static async Task Reply(NetworkStream stream, int status, string message)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>ClaUsage</title>" +
                   "<body style=\"font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:90vh;background:#111;color:#eee\">" +
                   $"<p style=\"font-size:18px\">{WebUtility.HtmlEncode(message)}</p></body>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch { /* already stopped */ }
        }
        _code.TrySetCanceled();
    }
}

public static class OAuthFlow
{
    public static void OpenBrowser(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Write($"could not open browser: {e.Message}");
        }
    }

    /// <summary>Opens the authorize page and returns the code from the redirect or from a pasted value.</summary>
    public static async Task<string> AuthorizeInBrowser(
        string authorizeUrl,
        string clientId,
        string scopes,
        string state,
        Pkce pkce,
        LoopbackServer server,
        IEnumerable<KeyValuePair<string, string>> extraQuery,
        ILoginUi ui,
        CancellationToken ct)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("response_type", "code"),
            new("redirect_uri", server.RedirectUri),
            new("scope", scopes),
            new("code_challenge", pkce.Challenge),
            new("code_challenge_method", "S256"),
            new("state", state),
        };
        query.AddRange(extraQuery);
        var url = new Uri(authorizeUrl + "?" + string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")));

        ui.ShowBrowser(url);
        OpenBrowser(url);

        var cancelled = Task.Delay(Timeout.Infinite, ct);
        var winner = await Task.WhenAny(server.Code, ui.PastedCode, cancelled);
        ct.ThrowIfCancellationRequested();
        if (winner == server.Code) return await server.Code;
        return ParsePastedCode(await ui.PastedCode, state);
    }

    /// <summary>Accepts a whole callback URL, a <c>code#state</c> pair, or a bare code.</summary>
    public static string ParsePastedCode(string value, string expectedState)
    {
        var trimmed = value.Trim();
        string? code;
        string? state = null;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            code = query["code"];
            state = query["state"];
        }
        else if (trimmed.Contains('#'))
        {
            var parts = trimmed.Split('#', 2);
            code = parts[0];
            state = parts[1];
        }
        else
        {
            code = trimmed;
        }
        if (string.IsNullOrWhiteSpace(code)) throw new ProviderException("The pasted code is empty.");
        if (state is not null && state != expectedState) throw new ProviderException("The pasted code belongs to a different sign-in. Try again.");
        return code;
    }
}

public sealed record TokenResponse(string AccessToken, string? RefreshToken, double? ExpiresIn, string? IdToken)
{
    public static TokenResponse Parse(JsonNode? node)
    {
        var access = node?["access_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(access)) throw new ProviderException("The sign-in service returned no access token.");
        return new TokenResponse(
            access,
            node?["refresh_token"]?.GetValue<string>(),
            Num(node?["expires_in"]),
            node?["id_token"]?.GetValue<string>());
    }

    public static double? Num(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }
}

public static class Http
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public sealed record Response(HttpStatusCode Status, string Body, HttpResponseMessage Raw)
    {
        public int Code => (int)Status;
        public bool Ok => Code is >= 200 and < 300;
        public JsonNode? Json()
        {
            try { return JsonNode.Parse(Body); } catch { return null; }
        }
        public string? Header(string name) =>
            Raw.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        public TimeSpan? RetryAfter()
        {
            var ra = Raw.Headers.RetryAfter;
            if (ra?.Delta is { } delta) return delta;
            if (ra?.Date is { } date) return date - DateTimeOffset.UtcNow;
            return null;
        }
    }

    public static async Task<Response> Send(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage raw;
        try
        {
            raw = await Client.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ProviderException("Couldn't reach the provider. Check your connection.");
        }
        var body = await raw.Content.ReadAsStringAsync(ct);
        return new Response(raw.StatusCode, body, raw);
    }

    public static Task<Response> PostJson(string url, object payload, CancellationToken ct, IDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (headers is not null) foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        return Send(request, ct);
    }

    public static Task<Response> PostForm(string url, IDictionary<string, string> fields, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return Send(request, ct);
    }

    public static Task<Response> Get(string url, IDictionary<string, string> headers, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        return Send(request, ct);
    }

    public static bool IsAuthFailure(Response r) => r.Code is 401 or 403;

    /// <summary>Only a refusal of the refresh token itself means the sign-in expired.</summary>
    public static TokenResponse ParseRenewal(Response r)
    {
        if (r.Code == 429) throw new RateLimitedException(r.RetryAfter() ?? TimeSpan.FromMinutes(15));
        if (r.Code is 400 or 401 or 403)
        {
            var json = r.Json();
            var error = json?["error"];
            var code = error is JsonValue ? error.GetValue<string>() : error?["code"]?.GetValue<string>() ?? json?["code"]?.GetValue<string>();
            if (code is "invalid_grant" or "refresh_token_expired" or "refresh_token_reused" or "refresh_token_invalidated" or "token_expired")
                throw new SignInExpiredException("Sign-in expired. Sign in again.");
        }
        if (!r.Ok) throw new ProviderException($"Token renewal failed (HTTP {r.Code}).");
        return TokenResponse.Parse(r.Json());
    }
}

public static class Jwt
{
    public static JsonNode? Payload(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            return JsonNode.Parse(Convert.FromBase64String(b64));
        }
        catch
        {
            return null;
        }
    }

    public static DateTimeOffset? Expiry(string? token) =>
        TokenResponse.Num(Payload(token)?["exp"]) is { } exp ? DateTimeOffset.FromUnixTimeSeconds((long)exp) : null;
}

public static class JsonRead
{
    public static double? Num(JsonNode? node) => TokenResponse.Num(node);

    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s)) return s.Equals("true", StringComparison.OrdinalIgnoreCase) ? true : s.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : null;
        return null;
    }

    /// <summary>ISO 8601 string, unix seconds, or unix milliseconds.</summary>
    public static DateTimeOffset? Date(JsonNode? node)
    {
        if (Str(node) is { } text && !string.IsNullOrWhiteSpace(text))
        {
            if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) return d;
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return null;
        }
        if (Num(node) is { } n && double.IsFinite(n))
        {
            var ms = Math.Abs(n) < 1e10 ? n * 1000 : n;
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        }
        return null;
    }

    public static string TitleCase(string raw, char separator)
    {
        return string.Join(" ", raw.Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
}
