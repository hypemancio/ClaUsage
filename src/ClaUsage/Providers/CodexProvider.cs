using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClaUsage.Core;

namespace ClaUsage.Providers;

/// <summary>
/// ChatGPT (Codex) subscription limits. Signs in with plain OpenID Connect through OpenAI, the same client
/// the Codex CLI uses, on its fixed callback port 1455; falls back to a device code when the port is busy.
/// Endpoints and mapping derive from MIT-licensed open source; see THIRD_PARTY_NOTICES.txt.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const string Scopes = "openid profile email offline_access";
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const double CreditUsd = 0.04;

    public ProviderKind Kind => ProviderKind.Codex;

    public async Task<Account> LoginAsync(ILoginUi ui, CancellationToken ct)
    {
        var pkce = Pkce.Generate();
        var state = Guid.NewGuid().ToString("N");
        LoopbackServer server;
        try
        {
            server = LoopbackServer.Start(state, 1455, "/auth/callback");
        }
        catch (SocketException)
        {
            Log.Write("codex: port 1455 busy, using device code");
            return await DeviceLogin(ui, ct);
        }
        using (server)
        {
            var code = await OAuthFlow.AuthorizeInBrowser(
                AuthorizeUrl, ClientId, Scopes, state, pkce, server,
                new KeyValuePair<string, string>[]
                {
                    new("id_token_add_organizations", "true"),
                    new("codex_cli_simplified_flow", "true"),
                    new("originator", "pi"),
                },
                ui, ct);
            return await Exchange(code, pkce.Verifier, server.RedirectUri, ct);
        }
    }

    private async Task<Account> DeviceLogin(ILoginUi ui, CancellationToken ct)
    {
        var start = await Http.PostJson("https://auth.openai.com/api/accounts/deviceauth/usercode",
            new Dictionary<string, string> { ["client_id"] = ClientId }, ct);
        if (!start.Ok) throw new ProviderException($"ChatGPT device sign-in failed (HTTP {start.Code}).");
        var json = start.Json();
        var userCode = JsonRead.Str(json?["user_code"]) ?? JsonRead.Str(json?["usercode"]) ?? throw new ProviderException("ChatGPT returned no device code.");
        var deviceAuthId = JsonRead.Str(json?["device_auth_id"]) ?? throw new ProviderException("ChatGPT returned no device id.");
        var interval = TimeSpan.FromSeconds(Math.Max(2, JsonRead.Num(json?["interval"]) ?? 5));
        var verify = new Uri("https://auth.openai.com/codex/device");
        ui.ShowDeviceCode(userCode, verify);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);
            var poll = await Http.PostJson("https://auth.openai.com/api/accounts/deviceauth/token",
                new Dictionary<string, string> { ["device_auth_id"] = deviceAuthId, ["user_code"] = userCode }, ct);
            if (poll.Ok)
            {
                var token = poll.Json();
                var authCode = JsonRead.Str(token?["authorization_code"]) ?? throw new ProviderException("ChatGPT returned no authorization code.");
                var verifier = JsonRead.Str(token?["code_verifier"]) ?? throw new ProviderException("ChatGPT returned no code verifier.");
                return await Exchange(authCode, verifier, "https://auth.openai.com/deviceauth/callback", ct);
            }
            if (poll.Code is 403 or 404) continue;
            var error = JsonRead.Str(poll.Json()?["error"]) ?? "";
            if (error == "deviceauth_authorization_pending") continue;
            if (error == "slow_down") { interval += TimeSpan.FromSeconds(5); continue; }
            throw new ProviderException(error == "expired_token" ? "Device sign-in expired." : "Device sign-in failed.");
        }
        throw new ProviderException("Device sign-in expired.");
    }

    private async Task<Account> Exchange(string code, string verifier, string redirectUri, CancellationToken ct)
    {
        var response = await Http.PostForm(TokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
        }, ct);
        if (!response.Ok) throw new ProviderException($"ChatGPT sign-in failed (HTTP {response.Code}). Try again.");
        var token = TokenResponse.Parse(response.Json());
        var accountId = JsonRead.Str(Jwt.Payload(token.AccessToken)?["https://api.openai.com/auth"]?["chatgpt_account_id"]);
        var email = JsonRead.Str(Jwt.Payload(token.IdToken)?["email"]);

        var account = new Account
        {
            Kind = Kind,
            Label = email ?? "ChatGPT Account",
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = Jwt.Expiry(token.AccessToken),
            Identity = accountId is not null && email is not null ? $"{accountId}/{email}" : null,
        };
        if (accountId is not null) account.Extras["chatgpt_account_id"] = accountId;
        return account;
    }

    public async Task RefreshTokenAsync(Account account, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(account.RefreshToken)) throw new SignInExpiredException("Sign-in expired. Sign in again.");
        var response = await Http.PostForm(TokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = account.RefreshToken!,
        }, ct);
        var token = Http.ParseRenewal(response);
        account.AccessToken = token.AccessToken;
        if (!string.IsNullOrEmpty(token.RefreshToken)) account.RefreshToken = token.RefreshToken;
        account.ExpiresAt = Jwt.Expiry(token.AccessToken);
    }

    public async Task<UsageSnapshot?> FetchAsync(Account account, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {account.AccessToken}",
            ["Accept"] = "application/json",
            ["User-Agent"] = "ClaUsage",
        };
        if (account.Extras.TryGetValue("chatgpt_account_id", out var id) && !string.IsNullOrEmpty(id))
            headers["ChatGPT-Account-Id"] = id;

        var response = await Http.Get(UsageUrl, headers, ct);
        if (response.Code == 429) throw new RateLimitedException(response.RetryAfter() ?? TimeSpan.FromMinutes(10));
        if (Http.IsAuthFailure(response)) return null;
        if (!response.Ok) throw new ProviderException($"ChatGPT usage read failed (HTTP {response.Code}).");
        var body = response.Json() ?? throw new ProviderException("ChatGPT returned an invalid usage response.");
        return MapUsage(body, response.Header, DateTimeOffset.UtcNow);
    }

    public static UsageSnapshot MapUsage(JsonNode body, Func<string, string?> header, DateTimeOffset now)
    {

        var lines = new List<MetricLine>();
        lines.AddRange(WindowLines(body["rate_limit"], "Session", "Weekly", now,
            JsonRead.Num(JsonValue.Create(header("x-codex-primary-used-percent"))),
            JsonRead.Num(JsonValue.Create(header("x-codex-secondary-used-percent")))));

        if (body["additional_rate_limits"] is JsonArray extra)
        {
            var spark = extra.FirstOrDefault(e =>
                (JsonRead.Str(e?["limit_name"]) ?? "").Contains("spark", StringComparison.OrdinalIgnoreCase) ||
                (JsonRead.Str(e?["metered_feature"]) ?? "").Contains("spark", StringComparison.OrdinalIgnoreCase));
            if (spark?["rate_limit"] is { } sparkLimit)
                lines.AddRange(WindowLines(sparkLimit, "Spark", "Spark Weekly", now, null, null));
        }

        if (JsonRead.Num(body["rate_limit_reset_credits"]?["available_count"]) is { } resets and >= 1)
            lines.Add(new MetricLine { Label = "Rate limit resets", Kind = MetricKind.Value, Used = Math.Floor(resets), Text = $"{Math.Floor(resets):0} available" });

        var credits = body["credits"];
        var hasCredits = JsonRead.Bool(credits?["has_credits"]) ?? JsonRead.Bool(JsonValue.Create(header("x-codex-credits-has-credits")));
        if (hasCredits != false)
        {
            var balance = JsonRead.Num(credits?["balance"]) ?? JsonRead.Num(JsonValue.Create(header("x-codex-credits-balance")));
            if (balance is { } b)
            {
                var count = Math.Max(0, Math.Floor(b));
                lines.Add(new MetricLine
                {
                    Label = "Credits",
                    Kind = MetricKind.Value,
                    Used = count * CreditUsd,
                    Unit = "USD",
                    IsCurrency = true,
                    Text = $"{Format.Money(count * CreditUsd, "USD")} · {count:0} credits",
                });
            }
        }

        var limit = body["spend_control"]?["individual_limit"];
        if (JsonRead.Num(limit?["used"]) is { } used and >= 0 && JsonRead.Num(limit?["limit"]) is { } cap and >= 0)
        {
            lines.Add(new MetricLine
            {
                Label = "Credit limit",
                Kind = MetricKind.Progress,
                Used = used,
                Limit = cap,
                Unit = "credits",
                ResetsAt = JsonRead.Date(limit?["reset_at"]),
            });
        }

        return new UsageSnapshot { Plan = FormatPlan(JsonRead.Str(body["plan_type"])), Lines = lines };
    }

    /// <summary>
    /// Codex usually sends the 5-hour window as primary and the weekly as secondary, but can move a sole
    /// weekly limit into the primary slot, so each meter takes the window whose duration is its own first.
    /// </summary>
    private static IEnumerable<MetricLine> WindowLines(JsonNode? rateLimit, string sessionLabel, string weeklyLabel, DateTimeOffset now, double? primaryHeader, double? secondaryHeader)
    {
        var slots = new (JsonNode? Window, double? Used)?[]
        {
            Slot(rateLimit?["primary_window"], primaryHeader),
            Slot(rateLimit?["secondary_window"], secondaryHeader),
        };
        var meters = new[] { (Label: sessionLabel, Period: Periods.SessionMs), (Label: weeklyLabel, Period: Periods.WeekMs) };
        var familiar = meters.Select(m => m.Period).ToArray();

        for (var i = 0; i < meters.Length; i++)
        {
            var meter = meters[i];
            var exact = slots.FirstOrDefault(s => s is not null && PeriodMs(s.Value.Window) == meter.Period);
            var own = slots[i];
            var ownPeriod = own is null ? null : PeriodMs(own.Value.Window);
            var fallback = own is not null && (ownPeriod is null || !familiar.Contains(ownPeriod.Value)) ? own : null;
            var chosen = exact ?? fallback;
            if (chosen is null || chosen.Value.Used is not { } usedPct) continue;
            yield return MetricLine.Percent(meter.Label, usedPct, ResetDate(chosen.Value.Window, now), PeriodMs(chosen.Value.Window) ?? meter.Period);
        }
    }

    private static (JsonNode? Window, double? Used)? Slot(JsonNode? window, double? header)
    {
        if (window is not JsonObject && header is null) return null;
        return (window, JsonRead.Num(window?["used_percent"]) ?? header);
    }

    private static long? PeriodMs(JsonNode? window) =>
        JsonRead.Num(window?["limit_window_seconds"]) is { } s ? (long)(s * 1000) : null;

    private static DateTimeOffset? ResetDate(JsonNode? window, DateTimeOffset now)
    {
        if (JsonRead.Num(window?["reset_at"]) is { } at) return DateTimeOffset.FromUnixTimeSeconds((long)at);
        if (JsonRead.Num(window?["reset_after_seconds"]) is { } after) return now.AddSeconds(after);
        return null;
    }

    private static string? FormatPlan(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "prolite" => "Pro 100",
            "pro" => "Pro 200",
            "promax" => "Pro 500",
            "self_serve_business_prolite" => "Business Premium",
            _ => JsonRead.TitleCase(raw.Trim(), '_'),
        };
    }
}
