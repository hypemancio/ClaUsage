using System.Text.Json.Serialization;

namespace ClaUsage.Core;

public enum ProviderKind
{
    Claude,
    Codex,
    OpenRouter,
    DeepSeek,
}

public static class ProviderKindExtensions
{
    /// <summary>API-key providers have no sign-in to renew; a refused key is replaced.</summary>
    public static bool UsesApiKey(this ProviderKind kind) => kind is ProviderKind.OpenRouter or ProviderKind.DeepSeek;

    public static string DisplayName(this ProviderKind kind) => kind switch
    {
        ProviderKind.Claude => "Claude",
        ProviderKind.Codex => "ChatGPT",
        ProviderKind.OpenRouter => "OpenRouter",
        ProviderKind.DeepSeek => "DeepSeek",
        _ => kind.ToString(),
    };

    public static string CliName(this ProviderKind kind) => kind switch
    {
        ProviderKind.Claude => "claude",
        ProviderKind.Codex => "codex",
        ProviderKind.OpenRouter => "openrouter",
        ProviderKind.DeepSeek => "deepseek",
        _ => kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>A signed-in account. Tokens are persisted encrypted with DPAPI (see <see cref="AccountStore"/>).</summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ProviderKind Kind { get; set; }
    public string Label { get; set; } = "";
    public string? CustomName { get; set; }
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Stable identity used to avoid adding the same login twice.</summary>
    public string? Identity { get; set; }
    public Dictionary<string, string> Extras { get; set; } = new();

    /// <summary>Set from <see cref="AppSettings.HideEmails"/>.</summary>
    public static bool MaskEmails { get; set; }

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(CustomName) ? CustomName! : MaskEmails ? Mask(Label) : Label;

    /// <summary>"mario@example.com" reads "m•••@example.com"; anything else is unchanged.</summary>
    public static string Mask(string label)
    {
        var at = label.IndexOf('@');
        return at <= 0 ? label : $"{label[0]}•••{label[at..]}";
    }
}

public enum MetricKind
{
    /// <summary>Used is a percentage 0..100.</summary>
    Percent,
    /// <summary>Used out of Limit, formatted with <see cref="MetricLine.Unit"/>.</summary>
    Progress,
    /// <summary>A plain value, shown as text.</summary>
    Value,
}

public sealed class MetricLine
{
    public string Label { get; set; } = "";
    public MetricKind Kind { get; set; }
    public double Used { get; set; }
    public double? Limit { get; set; }
    /// <summary>"USD"/"EUR"... for currency, "credits" for counts, null for percent.</summary>
    public string? Unit { get; set; }
    public bool IsCurrency { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public long? PeriodMs { get; set; }
    public string? Text { get; set; }
    /// <summary>When each available item (e.g. a rate-limit reset) expires, soonest first.</summary>
    public List<DateTimeOffset>? Expiries { get; set; }
    /// <summary>Set when the row offers an action (e.g. spending a reset): the provider's id for it.</summary>
    public string? ActionId { get; set; }
    /// <summary>What the action does, shown in its confirmation.</summary>
    public string? ActionDetail { get; set; }

    /// <summary>Fraction used 0..1 (null for plain values).</summary>
    [JsonIgnore]
    public double? Fraction => Kind switch
    {
        MetricKind.Percent => Math.Clamp(Used / 100.0, 0, 1),
        MetricKind.Progress when Limit is > 0 => Math.Clamp(Used / Limit.Value, 0, 1),
        _ => null,
    };

    /// <summary>Where an even pace would be right now, 0..1, when the window is known.</summary>
    public double? PaceFraction(DateTimeOffset now)
    {
        if (ResetsAt is null || PeriodMs is null or <= 0) return null;
        var remaining = (ResetsAt.Value - now).TotalMilliseconds;
        var elapsed = 1.0 - remaining / PeriodMs.Value;
        return Math.Clamp(elapsed, 0, 1);
    }

    public static MetricLine Percent(string label, double used, DateTimeOffset? resetsAt, long periodMs) => new()
    {
        Label = label,
        Kind = MetricKind.Percent,
        Used = used,
        ResetsAt = resetsAt,
        PeriodMs = periodMs,
    };
}

public static class Periods
{
    public const long SessionMs = 5L * 60 * 60 * 1000;
    public const long DayMs = 24L * 60 * 60 * 1000;
    public const long WeekMs = 7 * DayMs;
}

public enum SnapshotState
{
    Ok,
    Loading,
    SignInExpired,
    RateLimited,
    Error,
}

public sealed class UsageSnapshot
{
    public string AccountId { get; set; } = "";
    public string? Plan { get; set; }
    public List<MetricLine> Lines { get; set; } = new();
    public DateTimeOffset? FetchedAt { get; set; }
    public SnapshotState State { get; set; } = SnapshotState.Loading;
    public string? Message { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
}

public sealed class SignInExpiredException(string message) : Exception(message);

public sealed class RateLimitedException(TimeSpan retryAfter) : Exception($"Rate limited. Retry in {retryAfter.TotalMinutes:0} min.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

public sealed class ProviderException(string message) : Exception(message);
