using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClaUsage.Core;

namespace ClaUsage.Providers;

/// <summary>
/// OpenRouter, with an API key. A Management key sees the whole account (balance, spend summed over every
/// key, daily activity); a standard key sees only its own spend and limits.
/// OpenRouter's days, limits and budgets reset at midnight UTC and its weeks start on Monday.
/// Endpoints and mapping derive from MIT-licensed open source; see THIRD_PARTY_NOTICES.txt.
/// </summary>
public sealed class OpenRouterProvider : IUsageProvider
{
    private const string Base = "https://openrouter.ai/api/v1";
    private const int KeysPageSize = 100;
    private const int MaxKeysPages = 50;
    public static readonly Uri ManagementKeysPage = new("https://openrouter.ai/settings/management-keys");
    public static readonly Uri KeysPage = new("https://openrouter.ai/settings/keys");

    /// <summary>Activity ends at yesterday and changes once a day, so it's read at most hourly per key.</summary>
    private static readonly Dictionary<string, (DateTimeOffset ReadAt, double Yesterday, double Prior30)> Activity = new();

    public ProviderKind Kind => ProviderKind.OpenRouter;

    public async Task<Account> LoginAsync(ILoginUi ui, CancellationToken ct)
    {
        var key = (await ui.RequestApiKey(ManagementKeysPage, "A Management key shows the whole account. A standard key shows only its own spend.")).Trim();
        if (key.Length < 10) throw new ProviderException("That doesn't look like an OpenRouter key.");

        // One read checks the key before it is saved.
        var response = await Get("/key", key, ct);
        if (Http.IsAuthFailure(response)) throw new ProviderException("OpenRouter refused this key. Check it and try again.");
        if (!response.Ok) throw new ProviderException($"OpenRouter key check failed (HTTP {response.Code}).");
        var data = response.Json()?["data"];
        var management = JsonRead.Bool(data?["is_management_key"]) ?? throw new ProviderException("OpenRouter returned an unexpected key response.");
        var label = JsonRead.Str(data?["label"]);

        return new Account
        {
            Kind = Kind,
            Label = management ? "OpenRouter Account" : string.IsNullOrWhiteSpace(label) ? "OpenRouter Key" : $"Key {label}",
            AccessToken = key,
            Identity = Fingerprint(key),
            Extras = { ["keyType"] = management ? "management" : "standard" },
        };
    }

    /// <summary>An API key has no renewal: a refused key means the user has to replace it.</summary>
    public Task RefreshTokenAsync(Account account, CancellationToken ct) =>
        throw new SignInExpiredException("OpenRouter refused the API key. Replace it.");

    public async Task<UsageSnapshot?> FetchAsync(Account account, CancellationToken ct)
    {
        var keyResponse = await Get("/key", account.AccessToken, ct);
        if (Http.IsAuthFailure(keyResponse)) return null;
        Ensure(keyResponse);
        var keyData = keyResponse.Json()?["data"] ?? throw new ProviderException("OpenRouter returned an invalid key response.");
        var now = DateTimeOffset.UtcNow;
        var management = JsonRead.Bool(keyData["is_management_key"]) == true;

        JsonNode? credits = null;
        var keys = new List<JsonNode?>();
        (double Yesterday, double Prior30)? activity = null;
        if (management)
        {
            var creditsResponse = await Get("/credits", account.AccessToken, ct);
            // A Management key scoped to one workspace is refused the account's credits.
            if (creditsResponse.Code != 403)
            {
                Ensure(creditsResponse);
                credits = creditsResponse.Json();
            }
            for (var page = 0; page < MaxKeysPages; page++)
            {
                var r = await Get($"/keys?include_disabled=true&include_expired=true&offset={page * KeysPageSize}", account.AccessToken, ct);
                Ensure(r);
                var list = r.Json()?["data"] as JsonArray ?? throw new ProviderException("OpenRouter returned an invalid key list.");
                keys.AddRange(list);
                if (list.Count < KeysPageSize) break;
            }
            activity = await ReadActivity(account, now, ct);
        }

        var snapshot = Map(keyData, credits, management ? keys : null, activity, now);
        return snapshot;
    }

    private static async Task<(double, double)?> ReadActivity(Account account, DateTimeOffset now, CancellationToken ct)
    {
        var id = Fingerprint(account.AccessToken);
        if (Activity.TryGetValue(id, out var cached) && now - cached.ReadAt < TimeSpan.FromHours(1) && cached.ReadAt.UtcDateTime.Date == now.UtcDateTime.Date)
            return (cached.Yesterday, cached.Prior30);
        try
        {
            var r = await Get("/activity", account.AccessToken, ct);
            if (!r.Ok) { Log.Write($"openrouter activity HTTP {r.Code}"); return cached.ReadAt == default ? null : (cached.Yesterday, cached.Prior30); }
            var (yesterday, prior30) = MapActivity(r.Json(), now);
            Activity[id] = (now, yesterday, prior30);
            return (yesterday, prior30);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Write($"openrouter activity failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Maps <c>/key</c>, and for a Management key <c>/credits</c>, <c>/keys</c> and <c>/activity</c> totals.</summary>
    public static UsageSnapshot Map(JsonNode keyData, JsonNode? credits, IReadOnlyList<JsonNode?>? keys, (double Yesterday, double Prior30)? activity, DateTimeOffset now)
    {
        var lines = new List<MetricLine>();
        var management = keys is not null;

        if (credits?["data"] is { } c)
        {
            var total = JsonRead.Num(c["total_credits"]) ?? throw new ProviderException("OpenRouter returned invalid credits.");
            var used = JsonRead.Num(c["total_usage"]) ?? throw new ProviderException("OpenRouter returned invalid credits.");
            lines.Add(Money("Balance", Math.Max(0, Math.Max(0, total) - Math.Max(0, used))));
        }

        // A Management key can't run inference, so the account's spend is the sum over its listed keys.
        var sources = management ? keys! : new[] { keyData };
        lines.Add(Money("Today", sources.Sum(k => Spend(k, "daily"))));
        if (management) lines.Add(Money("Yesterday", activity?.Yesterday ?? 0));
        lines.Add(Money("This week", sources.Sum(k => Spend(k, "weekly"))));
        lines.Add(Money("This month", sources.Sum(k => Spend(k, "monthly"))));
        if (management) lines.Add(Money("Prior 30 days", activity?.Prior30 ?? 0));

        if (JsonRead.Num(keyData["limit"]) is { } limit and > 0)
        {
            var remaining = JsonRead.Num(keyData["limit_remaining"]) ?? throw new ProviderException("OpenRouter returned an invalid key limit.");
            lines.Add(Progress("Key limit", Math.Max(0, limit - Math.Max(0, remaining)), limit, true, JsonRead.Str(keyData["limit_reset"]), now));
        }
        if (keyData["effective_budget"] is JsonObject budget && JsonRead.Num(budget["limit_usd"]) is { } budgetLimit and > 0)
        {
            var spend = JsonRead.Num(budget["spend_usd"]) ?? throw new ProviderException("OpenRouter returned an invalid budget.");
            lines.Add(Progress("Budget", Math.Max(0, spend), budgetLimit, true, JsonRead.Str(budget["reset_interval"]), now));
        }
        if (keyData["free_model_daily_requests"] is JsonObject free && JsonRead.Num(free["limit"]) is { } freeLimit and > 0)
        {
            var used = JsonRead.Num(free["used"]) ?? throw new ProviderException("OpenRouter returned invalid free requests.");
            lines.Add(Progress("Free requests", Math.Max(0, used), freeLimit, false, "daily", now));
        }

        var plan = JsonRead.Bool(keyData["is_free_tier"]) is { } freeTier ? (freeTier ? "Free" : "Pay-as-you-go") : null;
        return new UsageSnapshot { Plan = plan, Lines = lines };
    }

    /// <summary>Sums <c>/activity</c> rows: yesterday (UTC) and the 30 finished UTC days before today.</summary>
    public static (double Yesterday, double Prior30) MapActivity(JsonNode? body, DateTimeOffset now)
    {
        if (body?["data"] is not JsonArray rows) throw new ProviderException("OpenRouter returned invalid activity.");
        var today = now.UtcDateTime.Date;
        double yesterday = 0, prior30 = 0;
        foreach (var row in rows)
        {
            var dateText = JsonRead.Str(row?["date"]);
            if (dateText is null || dateText.Length < 10) continue;
            if (!DateTime.TryParseExact(dateText[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            var usage = Math.Max(0, JsonRead.Num(row?["usage"]) ?? 0);
            if (day >= today || day < today.AddDays(-30)) continue;
            prior30 += usage;
            if (day == today.AddDays(-1)) yesterday += usage;
        }
        return (yesterday, prior30);
    }

    private static double Spend(JsonNode? key, string period) => Math.Max(0, JsonRead.Num(key?[$"usage_{period}"]) ?? 0);

    private static MetricLine Money(string label, double value) => new()
    {
        Label = label,
        Kind = MetricKind.Value,
        Used = value,
        Unit = "USD",
        IsCurrency = true,
    };

    private static MetricLine Progress(string label, double used, double limit, bool money, string? interval, DateTimeOffset now)
    {
        var (resetsAt, periodMs) = UtcWindowEnd(interval, now);
        return new MetricLine
        {
            Label = label,
            Kind = MetricKind.Progress,
            Used = used,
            Limit = limit,
            Unit = money ? "USD" : "requests",
            IsCurrency = money,
            ResetsAt = resetsAt,
            PeriodMs = periodMs,
        };
    }

    /// <summary>End of the current UTC day, ISO week (Monday start) or month.</summary>
    public static (DateTimeOffset?, long?) UtcWindowEnd(string? interval, DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        DateTime start, end;
        switch (interval)
        {
            case "daily":
                start = utc.Date;
                end = start.AddDays(1);
                break;
            case "weekly":
                start = utc.Date.AddDays(-(((int)utc.DayOfWeek + 6) % 7));
                end = start.AddDays(7);
                break;
            case "monthly":
                start = new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                end = start.AddMonths(1);
                break;
            default:
                return (null, null);
        }
        return (new DateTimeOffset(end, TimeSpan.Zero), (long)(end - start).TotalMilliseconds);
    }

    private static void Ensure(Http.Response r)
    {
        if (r.Code == 429) throw new RateLimitedException(r.RetryAfter() ?? TimeSpan.FromMinutes(5));
        if (!r.Ok) throw new ProviderException($"OpenRouter read failed (HTTP {r.Code}).");
    }

    private static Task<Http.Response> Get(string path, string key, CancellationToken ct) =>
        Http.Get(Base + path, new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {key.Trim()}",
            ["Accept"] = "application/json",
        }, ct);

    /// <summary>Identifies a key without storing it in clear: the same key is never added twice.</summary>
    public static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())))[..16];
}
