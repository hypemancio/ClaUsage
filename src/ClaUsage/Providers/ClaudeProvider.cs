using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaUsage.Core;

namespace ClaUsage.Providers;

/// <summary>
/// Claude (claude.ai subscription). Signs in with claude.ai OAuth asking only for <c>user:profile</c>,
/// which covers the usage and profile endpoints and cannot run inference.
/// Endpoints and mapping follow OpenUsage (MIT, github.com/robinebers/openusage).
/// </summary>
public sealed partial class ClaudeProvider : IUsageProvider
{
    private static readonly string ClientId = Encoding.UTF8.GetString(Convert.FromBase64String("OWQxYzI1MGEtZTYxYi00NGQ5LTg4ZWQtNTk0NGQxOTYyZjVl"));
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string Scopes = "user:profile";
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";
    private const string ClaudeCodeVersion = "2.1.294";

    /// <summary>Anthropic budgets the usage endpoint per account (about 30 requests an hour, shared with Claude Code).</summary>
    private static readonly TimeSpan PlanReadInterval = TimeSpan.FromHours(1);

    public ProviderKind Kind => ProviderKind.Claude;

    public async Task<Account> LoginAsync(ILoginUi ui, CancellationToken ct)
    {
        var pkce = Pkce.Generate();
        var state = Guid.NewGuid().ToString("N");
        using var server = LoopbackServer.Start(state);
        var code = await OAuthFlow.AuthorizeInBrowser(
            AuthorizeUrl, ClientId, Scopes, state, pkce, server,
            new[] { new KeyValuePair<string, string>("code", "true") },
            ui, ct);

        var response = await Http.PostJson(TokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["state"] = state,
            ["redirect_uri"] = server.RedirectUri,
            ["code_verifier"] = pkce.Verifier,
        }, ct);
        if (!response.Ok) throw new ProviderException($"Claude sign-in failed (HTTP {response.Code}). Try again.");
        var token = TokenResponse.Parse(response.Json());

        var profile = await Http.Get(ProfileUrl, ProfileHeaders(token.AccessToken), ct);
        if (!profile.Ok) throw new ProviderException("Claude didn't return your profile. Try again.");
        var json = profile.Json();
        var accountUuid = JsonRead.Str(json?["account"]?["uuid"]) ?? throw new ProviderException("Claude returned an unexpected profile.");
        var orgUuid = JsonRead.Str(json?["organization"]?["uuid"]);
        var email = JsonRead.Str(json?["account"]?["email"]) ?? JsonRead.Str(json?["email"]) ?? "Claude Account";

        var account = new Account
        {
            Kind = Kind,
            Label = email,
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = token.ExpiresIn is { } s ? DateTimeOffset.UtcNow.AddSeconds(s) : null,
            Identity = orgUuid is null ? accountUuid : $"{accountUuid}/{orgUuid}",
        };
        StorePlan(account, json);
        return account;
    }

    public async Task RefreshTokenAsync(Account account, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(account.RefreshToken)) throw new SignInExpiredException("Sign-in expired. Sign in again.");
        var response = await Http.PostJson(TokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = account.RefreshToken!,
            ["client_id"] = ClientId,
            ["scope"] = Scopes,
        }, ct);
        var token = Http.ParseRenewal(response);
        account.AccessToken = token.AccessToken;
        if (!string.IsNullOrEmpty(token.RefreshToken)) account.RefreshToken = token.RefreshToken;
        account.ExpiresAt = token.ExpiresIn is { } s ? DateTimeOffset.UtcNow.AddSeconds(s) : null;
    }

    public async Task<UsageSnapshot?> FetchAsync(Account account, CancellationToken ct)
    {
        var response = await Http.Get(UsageUrl, UsageHeaders(account.AccessToken), ct);
        if (response.Code == 429) throw new RateLimitedException(response.RetryAfter() ?? TimeSpan.FromMinutes(15));
        if (Http.IsAuthFailure(response)) return null;
        if (!response.Ok) throw new ProviderException($"Claude usage read failed (HTTP {response.Code}).");
        var body = response.Json() ?? throw new ProviderException("Claude returned an invalid usage response.");

        await MaybeReadPlan(account, ct);
        return MapUsage(body, account.Extras.GetValueOrDefault("plan"));
    }

    public static UsageSnapshot MapUsage(JsonNode body, string? plan)
    {
        var lines = new List<MetricLine>();
        AddWindow(body["five_hour"], "Session", Periods.SessionMs, lines);
        AddWindow(body["seven_day"], "Weekly", Periods.WeekMs, lines);
        AddWindow(body["seven_day_sonnet"], "Sonnet", Periods.WeekMs, lines);
        AddWindow(body["seven_day_opus"], "Opus", Periods.WeekMs, lines);
        AddScopedWeekly(body["limits"], lines);
        AddExtraUsage(body["extra_usage"], plan, lines);
        return new UsageSnapshot { Plan = plan, Lines = lines };
    }

    /// <summary>The plan comes from the profile, read at most once an hour to spare the account's request budget.</summary>
    private static async Task MaybeReadPlan(Account account, CancellationToken ct)
    {
        if (account.Extras.TryGetValue("planReadAt", out var at)
            && DateTimeOffset.TryParse(at, out var readAt)
            && DateTimeOffset.UtcNow - readAt < PlanReadInterval)
            return;
        try
        {
            var profile = await Http.Get(ProfileUrl, ProfileHeaders(account.AccessToken), ct);
            if (profile.Ok) StorePlan(account, profile.Json());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Write($"claude plan lookup failed: {e.Message}");
        }
        account.Extras["planReadAt"] = DateTimeOffset.UtcNow.ToString("O");
    }

    private static void StorePlan(Account account, JsonNode? profile)
    {
        var plan = FormatPlan(JsonRead.Str(profile?["organization"]?["organization_type"]), JsonRead.Str(profile?["organization"]?["rate_limit_tier"]));
        if (plan is null) account.Extras.Remove("plan");
        else account.Extras["plan"] = plan;
        account.Extras["planReadAt"] = DateTimeOffset.UtcNow.ToString("O");
    }

    public static string? FormatPlan(string? type, string? tier)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var raw = (type.StartsWith("claude_") ? type["claude_".Length..] : type).Trim();
        if (raw.Length == 0) return null;
        var basePlan = JsonRead.TitleCase(raw, ' ');
        tier ??= "";
        if (basePlan == "Ultra" || (basePlan == "Max" && (tier.Contains("ultra") || tier.Contains("20x")))) return "Ultra";
        if (basePlan == "Max") return basePlan;
        var match = MultiplierRegex().Match(tier);
        return match.Success ? $"{basePlan} {match.Value}" : basePlan;
    }

    [GeneratedRegex(@"\d+x")]
    private static partial Regex MultiplierRegex();

    private static void AddWindow(JsonNode? node, string label, long periodMs, List<MetricLine> lines)
    {
        if (node is not JsonObject obj || JsonRead.Num(obj["utilization"]) is not { } used) return;
        lines.Add(MetricLine.Percent(label, used, JsonRead.Date(obj["resets_at"]), periodMs));
    }

    /// <summary>Model-scoped weekly limits (<c>kind: weekly_scoped</c>) named by <c>scope.model.display_name</c>.</summary>
    private static void AddScopedWeekly(JsonNode? limits, List<MetricLine> lines)
    {
        if (limits is not JsonArray array) return;
        foreach (var entry in array)
        {
            if (JsonRead.Str(entry?["kind"]) != "weekly_scoped") continue;
            var name = JsonRead.Str(entry?["scope"]?["model"]?["display_name"]);
            if (string.IsNullOrWhiteSpace(name) || lines.Any(l => l.Label == name)) continue;
            if (JsonRead.Num(entry?["percent"]) is not { } used) continue;
            lines.Add(MetricLine.Percent(name, used, JsonRead.Date(entry?["resets_at"]), Periods.WeekMs));
        }
    }

    private static void AddExtraUsage(JsonNode? node, string? plan, List<MetricLine> lines)
    {
        if (node is not JsonObject obj || JsonRead.Bool(obj["is_enabled"]) != true) return;
        var currency = JsonRead.Str(obj["currency"]) ?? "USD";
        var minor = Math.Pow(10, JsonRead.Num(obj["decimal_places"]) ?? 2);
        var used = Math.Round(JsonRead.Num(obj["used_credits"]) ?? 0) / minor;
        var label = plan?.StartsWith("Enterprise") == true ? "Spend" : "Extra usage";
        var limitMinor = JsonRead.Num(obj["monthly_limit"]);
        lines.Add(new MetricLine
        {
            Label = label,
            Kind = limitMinor is > 0 ? MetricKind.Progress : MetricKind.Value,
            Used = used,
            Limit = limitMinor is > 0 ? Math.Round(limitMinor.Value) / minor : null,
            Unit = currency,
            IsCurrency = true,
        });
    }

    private static Dictionary<string, string> UsageHeaders(string accessToken) => new()
    {
        ["Authorization"] = $"Bearer {accessToken.Trim()}",
        ["Accept"] = "application/json",
        ["anthropic-beta"] = "oauth-2025-04-20",
        ["User-Agent"] = $"claude-cli/{ClaudeCodeVersion} (external, cli)",
    };

    private static Dictionary<string, string> ProfileHeaders(string accessToken) => new()
    {
        ["Authorization"] = $"Bearer {accessToken.Trim()}",
        ["Accept"] = "application/json",
        ["anthropic-beta"] = "oauth-2025-04-20",
    };
}
