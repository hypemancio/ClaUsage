using System.Windows.Threading;
using ClaUsage.Providers;

namespace ClaUsage.Core;

/// <summary>
/// Owns accounts and their latest usage. Runs on the UI thread: every await resumes on the dispatcher,
/// so state needs no locking.
/// </summary>
public sealed class UsageService
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, UsageSnapshot> _snapshots = new();
    private readonly Dictionary<string, int> _failures = new();
    private readonly HashSet<string> _inFlight = new();
    private readonly Dictionary<string, int> _alertLevels = new();
    private CancellationTokenSource _cts = new();
    private readonly bool _demo;

    public AppSettings Settings { get; }
    public List<Account> Accounts { get; }

    /// <summary>Raised after any account or snapshot changes.</summary>
    public event Action? Changed;

    /// <summary>A limit crossed the warn or critical threshold: (title, text).</summary>
    public event Action<string, string>? Alert;

    public UsageService(bool demo = false)
    {
        _demo = demo;
        Settings = AppSettings.Load();
        Accounts = demo ? DemoData.Accounts() : AccountStore.Load();
        SortAccounts();
        foreach (var entry in demo ? new() : SnapshotCache.Load())
        {
            if (Accounts.Any(a => a.Id == entry.AccountId))
            {
                // A cached read is shown until the first live one lands; it is never newer than "loading".
                entry.Snapshot.State = SnapshotState.Loading;
                _snapshots[entry.AccountId] = entry.Snapshot;
            }
        }
        _timer.Tick += (_, _) => _ = RefreshDue();
    }

    public void Start()
    {
        _timer.Start();
        _ = RefreshAll(force: true);
    }

    public UsageSnapshot? Snapshot(string accountId) => _snapshots.GetValueOrDefault(accountId);

    public bool IsRefreshing => _inFlight.Count > 0;

    public DateTimeOffset? LastUpdated =>
        _snapshots.Values.Where(s => s.FetchedAt is not null).Select(s => s.FetchedAt).DefaultIfEmpty(null).Max();

    public Task RefreshAll(bool force) => Task.WhenAll(Accounts.ToList().Select(a => Refresh(a, force)));

    private Task RefreshDue()
    {
        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromMinutes(Math.Max(1, Settings.RefreshMinutes));
        var due = Accounts.Where(a =>
        {
            var snap = Snapshot(a.Id);
            if (snap?.RetryAt is { } retry) return now >= retry;
            return snap?.FetchedAt is not { } at || now - at >= interval;
        }).ToList();
        return Task.WhenAll(due.Select(a => Refresh(a, force: false)));
    }

    public async Task Refresh(Account account, bool force)
    {
        if (!_inFlight.Add(account.Id)) return;
        var previous = Snapshot(account.Id);
        var now = DateTimeOffset.UtcNow;
        try
        {
            // Inside a provider's 429 window nothing is sent, even on a manual refresh: the penalty
            // lives on the provider's side and every extra request extends it.
            if (previous?.State == SnapshotState.RateLimited && previous.RetryAt is { } retryAt && now < retryAt) return;
            if (previous?.State == SnapshotState.SignInExpired && !force) return;

            Changed?.Invoke();
            if (_demo)
            {
                await Task.Delay(400);
                var demo = DemoData.For(account, DateTimeOffset.UtcNow);
                demo.AccountId = account.Id;
                demo.FetchedAt = DateTimeOffset.UtcNow;
                demo.State = SnapshotState.Ok;
                _snapshots[account.Id] = demo;
                CheckAlerts(account, demo);
                return;
            }
            var provider = Providers.Providers.For(account.Kind);
            var ct = _cts.Token;

            if (account.ExpiresAt is { } exp && exp - now < TimeSpan.FromMinutes(2) && !string.IsNullOrEmpty(account.RefreshToken))
            {
                await provider.RefreshTokenAsync(account, ct);
                SaveAccounts();
            }

            var snapshot = await provider.FetchAsync(account, ct);
            if (snapshot is null)
            {
                await provider.RefreshTokenAsync(account, ct);
                SaveAccounts();
                snapshot = await provider.FetchAsync(account, ct)
                           ?? throw new SignInExpiredException("Sign-in expired. Sign in again.");
            }

            // Provider may have updated extras (plan cache).
            SaveAccounts();
            snapshot.AccountId = account.Id;
            snapshot.FetchedAt = DateTimeOffset.UtcNow;
            snapshot.State = SnapshotState.Ok;
            _snapshots[account.Id] = snapshot;
            _failures.Remove(account.Id);
            CheckAlerts(account, snapshot);
        }
        catch (SignInExpiredException e)
        {
            _snapshots[account.Id] = Degraded(previous, account, SnapshotState.SignInExpired, e.Message, null);
        }
        catch (RateLimitedException e)
        {
            var failures = _failures[account.Id] = _failures.GetValueOrDefault(account.Id) + 1;
            var wait = TimeSpan.FromTicks(Math.Max(e.RetryAfter.Ticks, Backoff(failures).Ticks));
            _snapshots[account.Id] = Degraded(previous, account, SnapshotState.RateLimited,
                "The provider is rate-limiting usage reads.", DateTimeOffset.UtcNow + wait);
        }
        catch (OperationCanceledException)
        {
            // App shutting down or account removed.
        }
        catch (Exception e)
        {
            var failures = _failures[account.Id] = _failures.GetValueOrDefault(account.Id) + 1;
            Log.Write($"{account.Kind} refresh failed: {e.Message}");
            _snapshots[account.Id] = Degraded(previous, account, SnapshotState.Error,
                e is ProviderException ? e.Message : "Usage read failed.", DateTimeOffset.UtcNow + Backoff(failures));
        }
        finally
        {
            _inFlight.Remove(account.Id);
            SaveCache();
            Changed?.Invoke();
        }
    }

    /// <summary>1, 2, 4, 8... minutes, capped at an hour.</summary>
    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, failures - 1))));

    /// <summary>Keeps the last good numbers visible under the error.</summary>
    private static UsageSnapshot Degraded(UsageSnapshot? previous, Account account, SnapshotState state, string message, DateTimeOffset? retryAt) => new()
    {
        AccountId = account.Id,
        Plan = previous?.Plan,
        Lines = previous?.Lines ?? new(),
        FetchedAt = previous?.FetchedAt,
        State = state,
        Message = message,
        RetryAt = retryAt,
    };

    private void CheckAlerts(Account account, UsageSnapshot snapshot)
    {
        foreach (var line in snapshot.Lines)
        {
            if (line.Fraction is not { } fraction) continue;
            var percent = fraction * 100;
            var level = percent >= Settings.CriticalPercent ? 2 : percent >= Settings.WarnPercent ? 1 : 0;
            // A new window (new reset time) starts over.
            var key = $"{account.Id}|{line.Label}|{line.ResetsAt?.ToUnixTimeSeconds() / 600}";
            var before = _alertLevels.GetValueOrDefault(key);
            _alertLevels[key] = level;
            if (level <= before || !Settings.Notifications || Settings.IsHidden(account.Id, line.Label)) continue;
            var reset = line.ResetsAt is { } r ? $" Resets {Format.ResetIn(r, DateTimeOffset.UtcNow)}." : "";
            Alert?.Invoke(
                $"{account.Kind.DisplayName()} {line.Label} at {percent:0}%",
                $"{account.DisplayName}: {(level == 2 ? "almost used up" : "running low")}.{reset}");
        }
    }

    /// <param name="replacing">When set, the new sign-in or key replaces this account's credential.</param>
    public Account AddOrUpdate(Account account, Account? replacing = null)
    {
        var existing = replacing is not null && Accounts.Contains(replacing) ? replacing
            : account.Identity is null ? null : Accounts.FirstOrDefault(a => a.Kind == account.Kind && a.Identity == account.Identity);
        if (existing is not null)
        {
            existing.AccessToken = account.AccessToken;
            existing.RefreshToken = account.RefreshToken;
            existing.ExpiresAt = account.ExpiresAt;
            existing.Identity = account.Identity;
            existing.Label = account.Label;
            foreach (var (k, v) in account.Extras) existing.Extras[k] = v;
            _snapshots.Remove(existing.Id);
            account = existing;
        }
        else
        {
            Accounts.Add(account);
            Settings.Order.Add(account.Id);
            Settings.Save();
        }
        SaveAccounts();
        Changed?.Invoke();
        _ = Refresh(account, force: true);
        return account;
    }

    public void Remove(Account account)
    {
        Accounts.Remove(account);
        _snapshots.Remove(account.Id);
        Settings.Order.Remove(account.Id);
        Settings.HiddenMetrics.Remove(account.Id);
        Settings.Save();
        SaveAccounts();
        SaveCache();
        Changed?.Invoke();
    }

    public void Rename(Account account, string? name)
    {
        account.CustomName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        SaveAccounts();
        SaveCache();
        Changed?.Invoke();
    }

    public void Move(Account account, int delta)
    {
        var index = Accounts.IndexOf(account);
        var target = Math.Clamp(index + delta, 0, Accounts.Count - 1);
        if (index < 0 || target == index) return;
        Accounts.RemoveAt(index);
        Accounts.Insert(target, account);
        Settings.Order = Accounts.Select(a => a.Id).ToList();
        Settings.Save();
        SaveAccounts();
        Changed?.Invoke();
    }

    public void NotifySettingsChanged()
    {
        Settings.Save();
        Changed?.Invoke();
    }

    /// <summary>The highest used fraction among visible percent and progress limits, for the tray icon.</summary>
    public (double Fraction, Account Account, MetricLine Line)? Highest()
    {
        (double, Account, MetricLine)? best = null;
        foreach (var account in Accounts)
        {
            if (Snapshot(account.Id) is not { } snap) continue;
            foreach (var line in snap.Lines)
            {
                if (line.Fraction is not { } f || Settings.IsHidden(account.Id, line.Label)) continue;
                if (best is null || f > best.Value.Item1) best = (f, account, line);
            }
        }
        return best;
    }

    public void Stop()
    {
        _timer.Stop();
        _cts.Cancel();
    }

    private void SortAccounts()
    {
        var order = Settings.Order;
        Accounts.Sort((a, b) =>
        {
            var ia = order.IndexOf(a.Id);
            var ib = order.IndexOf(b.Id);
            return (ia < 0 ? int.MaxValue : ia).CompareTo(ib < 0 ? int.MaxValue : ib);
        });
    }

    private void SaveAccounts()
    {
        if (_demo) return;
        try
        {
            AccountStore.Save(Accounts);
        }
        catch (Exception e)
        {
            Log.Write($"accounts save failed: {e.Message}");
        }
    }

    private void SaveCache()
    {
        if (!_demo) WriteCache();
    }

    private void WriteCache() => SnapshotCache.Save(Accounts
        .Where(a => _snapshots.ContainsKey(a.Id))
        .Select(a => new SnapshotCache.Entry
        {
            AccountId = a.Id,
            Provider = a.Kind.CliName(),
            Name = a.DisplayName,
            Snapshot = _snapshots[a.Id],
        }));
}

public static class Format
{
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "<1m";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalDays}d {span.Hours}h";
    }

    public static string ResetIn(DateTimeOffset resetsAt, DateTimeOffset now) =>
        resetsAt <= now ? "now" : $"in {Duration(resetsAt - now)}";

    public static string ResetClock(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var local = resetsAt.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Date == today.AddDays(1)) return $"tomorrow {local:HH:mm}";
        return local.ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        if (span.TotalMinutes < 1) return "just now";
        return $"{Duration(span)} ago";
    }

    public static string Money(double value, string? currency)
    {
        var symbol = currency?.ToUpperInvariant() switch
        {
            "USD" or null => "$",
            "EUR" => "€",
            "GBP" => "£",
            "JPY" => "¥",
            var other => other + " ",
        };
        return $"{symbol}{value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    public static string Value(MetricLine line) => line.Kind switch
    {
        MetricKind.Percent => $"{line.Used:0}%",
        MetricKind.Progress when line.IsCurrency => $"{Money(line.Used, line.Unit)} / {Money(line.Limit ?? 0, line.Unit)}",
        MetricKind.Progress => $"{line.Used:0} / {line.Limit:0} {line.Unit}".TrimEnd(),
        _ => line.Text ?? (line.IsCurrency ? Money(line.Used, line.Unit) : $"{line.Used:0}"),
    };
}
