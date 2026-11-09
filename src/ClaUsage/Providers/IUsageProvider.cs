using ClaUsage.Core;

namespace ClaUsage.Providers;

public interface IUsageProvider
{
    ProviderKind Kind { get; }

    /// <summary>Runs the provider's sign-in and returns a new account (not yet saved).</summary>
    Task<Account> LoginAsync(ILoginUi ui, CancellationToken ct);

    /// <summary>Renews the access token in place. Throws <see cref="SignInExpiredException"/> when refused.</summary>
    Task RefreshTokenAsync(Account account, CancellationToken ct);

    /// <summary>
    /// Reads usage with the current access token. Returns null when the token was refused (401/403),
    /// so the caller can renew it once and retry.
    /// </summary>
    Task<UsageSnapshot?> FetchAsync(Account account, CancellationToken ct);
}

public static class Providers
{
    public static readonly IReadOnlyList<IUsageProvider> All = new IUsageProvider[]
    {
        new ClaudeProvider(),
        new CodexProvider(),
        new OpenRouterProvider(),
    };

    public static IUsageProvider For(ProviderKind kind) => All.First(p => p.Kind == kind);
}
