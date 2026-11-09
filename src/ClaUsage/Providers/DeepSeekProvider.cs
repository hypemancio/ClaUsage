using System.Text.Json.Nodes;
using ClaUsage.Core;

namespace ClaUsage.Providers;

/// <summary>
/// DeepSeek, with an API key. The official <c>/user/balance</c> endpoint returns the prepaid balance per currency
/// (USD or CNY), split into granted (promotional) and topped-up credit. DeepSeek exposes no spend history.
/// </summary>
public sealed class DeepSeekProvider : IUsageProvider
{
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";
    public static readonly Uri KeysPage = new("https://platform.deepseek.com/api_keys");
    public static readonly Uri TopUpPage = new("https://platform.deepseek.com/top_up");

    public ProviderKind Kind => ProviderKind.DeepSeek;

    public async Task<Account> LoginAsync(ILoginUi ui, CancellationToken ct)
    {
        var key = (await ui.RequestApiKey(KeysPage, "Any API key of the account shows its balance.")).Trim();
        if (key.Length < 10) throw new ProviderException("That doesn't look like a DeepSeek key.");

        // One read checks the key before it is saved.
        var response = await Get(key, ct);
        if (Http.IsAuthFailure(response)) throw new ProviderException("DeepSeek refused this key. Check it and try again.");
        if (!response.Ok) throw new ProviderException($"DeepSeek key check failed (HTTP {response.Code}).");
        Map(response.Json());

        return new Account
        {
            Kind = Kind,
            Label = $"DeepSeek ···{key[^4..]}",
            AccessToken = key,
            Identity = OpenRouterProvider.Fingerprint(key),
        };
    }

    /// <summary>An API key has no renewal: a refused key means the user has to replace it.</summary>
    public Task RefreshTokenAsync(Account account, CancellationToken ct) =>
        throw new SignInExpiredException("DeepSeek refused the API key. Replace it.");

    public async Task<UsageSnapshot?> FetchAsync(Account account, CancellationToken ct)
    {
        var response = await Get(account.AccessToken, ct);
        if (Http.IsAuthFailure(response)) return null;
        if (response.Code == 429) throw new RateLimitedException(response.RetryAfter() ?? TimeSpan.FromMinutes(5));
        if (!response.Ok) throw new ProviderException($"DeepSeek read failed (HTTP {response.Code}).");
        return Map(response.Json());
    }

    /// <summary>Maps <c>/user/balance</c>: one Balance row per currency, plus its granted and topped-up parts when both exist.</summary>
    public static UsageSnapshot Map(JsonNode? body)
    {
        var available = JsonRead.Bool(body?["is_available"]) ?? throw new ProviderException("DeepSeek returned an invalid balance.");
        if (body?["balance_infos"] is not JsonArray infos) throw new ProviderException("DeepSeek returned an invalid balance.");

        var lines = new List<MetricLine>();
        var several = infos.Count > 1;
        foreach (var info in infos)
        {
            var currency = JsonRead.Str(info?["currency"]) ?? throw new ProviderException("DeepSeek returned an invalid balance.");
            var total = JsonRead.Num(info?["total_balance"]) ?? throw new ProviderException("DeepSeek returned an invalid balance.");
            var granted = Math.Max(0, JsonRead.Num(info?["granted_balance"]) ?? 0);
            var toppedUp = Math.Max(0, JsonRead.Num(info?["topped_up_balance"]) ?? 0);
            var suffix = several ? $" ({currency})" : "";
            lines.Add(Money($"Balance{suffix}", Math.Max(0, total), currency));
            if (granted > 0 && toppedUp > 0)
            {
                lines.Add(Money($"Topped up{suffix}", toppedUp, currency));
                lines.Add(Money($"Granted{suffix}", granted, currency));
            }
        }
        return new UsageSnapshot { Plan = available ? "Pay-as-you-go" : "No balance", Lines = lines };
    }

    private static MetricLine Money(string label, double value, string currency) => new()
    {
        Label = label,
        Kind = MetricKind.Value,
        Used = value,
        Unit = currency,
        IsCurrency = true,
    };

    private static Task<Http.Response> Get(string key, CancellationToken ct) =>
        Http.Get(BalanceUrl, new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {key.Trim()}",
            ["Accept"] = "application/json",
        }, ct);
}
