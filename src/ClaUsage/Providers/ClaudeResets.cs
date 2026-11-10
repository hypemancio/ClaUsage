using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaUsage.Core;

namespace ClaUsage.Providers;

/// <summary>
/// Claude's one-off rate-limit resets ("cedar_ember" grants): Anthropic sometimes gives an account resets
/// that refill its session and weekly limits on demand. Undocumented and shaped like Claude Code's own
/// requests; mapping follows MIT-licensed open source (see THIRD_PARTY_NOTICES.txt).
/// </summary>
public sealed partial class ClaudeProvider
{
    public const string ResetsLabel = "Rate limit resets";

    /// <summary>The variant Claude Code sends to see resets on offer. Polled hard, Anthropic answers 429.</summary>
    private const string GrantsUrl = "https://api.anthropic.com/api/oauth/usage?cedar_ember=1&skip_spend=1";
    private static readonly TimeSpan GrantsInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan GrantsRetryAfterFailure = TimeSpan.FromHours(1);

    public sealed record Grant(string Id, int ResetsLeft, DateTimeOffset? EndsAt, List<string> Clears);

    public sealed record GrantStatus(bool Eligible, List<Grant> Grants, string? NextGrantId)
    {
        public string? ClaimableGrantId =>
            Eligible && NextGrantId is { } id && Grants.Any(g => g.Id == id && g.ResetsLeft > 0) ? id : null;

        public string ConfirmDetail
        {
            get
            {
                var grant = Grants.FirstOrDefault(g => g.Id == ClaimableGrantId);
                var names = grant?.Clears.Select(c => c switch { "five_hour" => "session", "seven_day" => "weekly", _ => null })
                    .Where(n => n is not null).ToList() ?? new();
                return names.Count > 0
                    ? $"Refills your {string.Join(" and ", names)} limits now. This can't be undone."
                    : "Refills your limits now. This can't be undone.";
            }
        }

        public MetricLine Line(DateTimeOffset now)
        {
            var count = 0;
            var expiries = new List<DateTimeOffset>();
            if (Eligible)
            {
                foreach (var grant in Grants.Where(g => g.ResetsLeft >= 1))
                {
                    if (grant.EndsAt is { } end && end <= now) continue;
                    count += grant.ResetsLeft;
                    if (grant.EndsAt is { } e) expiries.AddRange(Enumerable.Repeat(e, grant.ResetsLeft));
                }
            }
            expiries.Sort();
            return new MetricLine
            {
                Label = ResetsLabel,
                Kind = MetricKind.Value,
                Used = count,
                Text = $"{count} available",
                Expiries = expiries.Count > 0 ? expiries : null,
                ActionId = count > 0 ? ClaimableGrantId : null,
                ActionDetail = count > 0 ? ConfirmDetail : null,
            };
        }
    }

    private sealed class GrantsState
    {
        public DateTimeOffset? CheckedAt;
        public GrantStatus? Status;
        /// <summary>A claim whose answer never arrived: retried under the same request id, so it can't spend twice.</summary>
        public (string GrantId, string RequestId)? Unsettled;
    }

    private static readonly Dictionary<string, GrantsState> Grants = new();

    private static GrantsState StateFor(Account account)
    {
        if (!Grants.TryGetValue(account.Id, out var state)) Grants[account.Id] = state = new GrantsState();
        return state;
    }

    /// <summary>The resets row from the last check, if any; appended to every usage read.</summary>
    /// <summary>The resets row, left out while none is available.</summary>
    public static MetricLine? ResetsLine(Account account, DateTimeOffset now) =>
        Grants.TryGetValue(account.Id, out var state) && state.Status?.Line(now) is { Used: > 0 } line ? line : null;

    public static bool GrantsDue(Account account, DateTimeOffset now) =>
        StateFor(account).CheckedAt is not { } at || now - at >= GrantsInterval;

    /// <summary>Reads the resets on offer. A failure only costs the row, never the bars.</summary>
    public async Task<bool> ReadGrantsAsync(Account account, CancellationToken ct)
    {
        var state = StateFor(account);
        var before = state.Status;
        try
        {
            var response = await Http.Get(GrantsUrl, UsageHeaders(account.AccessToken), ct);
            if (response.Code == 429) throw new RateLimitedException(response.RetryAfter() ?? TimeSpan.FromMinutes(30));
            if (Http.IsAuthFailure(response)) throw new SignInExpiredException("Sign-in expired. Sign in again.");
            if (!response.Ok) throw new ProviderException($"HTTP {response.Code}");
            state.Status = ParseGrants(response.Json()?["cedar_ember"]);
            state.CheckedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Write($"claude reset grants lookup failed, retrying in an hour: {e.Message}");
            state.CheckedAt = DateTimeOffset.UtcNow - GrantsInterval + GrantsRetryAfterFailure;
        }
        return !Equals(before, state.Status);
    }

    public static GrantStatus? ParseGrants(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var grants = new List<Grant>();
        foreach (var entry in obj["grants"] as JsonArray ?? new JsonArray())
        {
            var id = JsonRead.Str(entry?["id"]);
            if (string.IsNullOrEmpty(id) || JsonRead.Num(entry?["resets_left"]) is not { } left)
            {
                Log.Write("claude reset grants block has a malformed grant; ignoring the block");
                return null;
            }
            var clears = (entry?["clears"] as JsonArray)?.Select(JsonRead.Str).OfType<string>().ToList() ?? new();
            grants.Add(new Grant(id, Math.Max(0, (int)Math.Floor(left)), JsonRead.Date(entry?["ends_at"]), clears));
        }
        var next = JsonRead.Str(obj["next_grant_id"]);
        return new GrantStatus(JsonRead.Bool(obj["eligible"]) == true, grants, string.IsNullOrEmpty(next) ? null : next);
    }

    public enum ClaimOutcome { Success, NothingToReset, NoCredit, Unconfirmed, UnavailableNow, NotPermitted, SignInExpired, Refused, Failed }

    public sealed record ClaimResult(ClaimOutcome Outcome, DateTimeOffset? Until = null)
    {
        public string Message => Outcome switch
        {
            ClaimOutcome.Success => "Your limits are refilled. The panel updates in a moment.",
            ClaimOutcome.NothingToReset => "Nothing to reset: you aren't limited right now. Your reset is still available.",
            ClaimOutcome.NoCredit => "No reset is available anymore.",
            ClaimOutcome.Unconfirmed => "Anthropic didn't confirm the reset yet. Check your limits in a few minutes.",
            ClaimOutcome.UnavailableNow => Until is { } u
                ? $"Resets can't be used right now. Try again {Format.ResetIn(u, DateTimeOffset.UtcNow)}."
                : "Resets can't be used right now. Try again later.",
            ClaimOutcome.NotPermitted => "Anthropic doesn't allow this sign-in to use resets. Use it from Claude Code instead.",
            ClaimOutcome.SignInExpired => "Sign-in expired. Sign in again, then retry.",
            ClaimOutcome.Refused => "Anthropic refused the reset.",
            _ => "The reset request failed. Nothing was spent unless Anthropic confirms it later.",
        };
    }

    /// <summary>Spends the next reset. The caller has already asked the user to confirm.</summary>
    public async Task<ClaimResult> ClaimResetAsync(Account account, CancellationToken ct)
    {
        var state = StateFor(account);
        if (state.Status is null) await ReadGrantsAsync(account, ct);
        if (state.Status?.ClaimableGrantId is not { } grantId) return new ClaimResult(ClaimOutcome.NoCredit);

        var requestId = state.Unsettled is { } u && u.GrantId == grantId ? u.RequestId : Guid.NewGuid().ToString("N");
        if (!Regex.IsMatch(grantId, "^[a-z0-9_-]{1,40}$") || !Regex.IsMatch(requestId, "^[A-Za-z0-9_-]{1,64}$"))
        {
            Log.Write("claude reset claim: refusing a malformed grant id or request id");
            return new ClaimResult(ClaimOutcome.Refused);
        }

        var org = await OrganizationUuid(account, ct);
        if (org is null) return new ClaimResult(ClaimOutcome.Failed);

        Http.Response response;
        try
        {
            response = await Http.PostJson($"https://api.anthropic.com/api/organizations/{org}/reset_rate_limits",
                new Dictionary<string, string> { ["program"] = "cedar_ember", ["grant_id"] = grantId, ["request_id"] = requestId },
                ct, UsageHeaders(account.AccessToken));
        }
        catch (ProviderException e)
        {
            Log.Write($"claude reset claim: request failed: {e.Message}");
            state.Unsettled = (grantId, requestId);
            return new ClaimResult(ClaimOutcome.Failed);
        }

        var body = response.Ok ? response.Json() : null;
        if (body?["result"] is not null) state.Unsettled = null;
        else if (response.Ok || response.Code >= 500) state.Unsettled = (grantId, requestId);

        var result = MapClaim(response, body);
        Log.Write($"claude reset claim: {result.Outcome} (HTTP {response.Code})");
        if (result.Outcome is ClaimOutcome.Success or ClaimOutcome.Unconfirmed or ClaimOutcome.NoCredit)
        {
            // Show the new count right away; the next hourly check confirms it.
            if (result.Outcome == ClaimOutcome.Success && state.Status is { } status)
            {
                var left = JsonRead.Num(body?["resets_left"]) is { } l ? (int)Math.Floor(l) : (int?)null;
                state.Status = status with
                {
                    Grants = status.Grants.Select(g => g.Id == grantId ? g with { ResetsLeft = Math.Max(0, left ?? g.ResetsLeft - 1) } : g).ToList(),
                };
            }
            state.CheckedAt = DateTimeOffset.UtcNow - GrantsInterval + TimeSpan.FromMinutes(15);
        }
        return result;
    }

    public static ClaimResult MapClaim(Http.Response response, JsonNode? body)
    {
        // A token whose scopes don't cover the endpoint gets 403 "scope requirement": the sign-in's grant, not an expiry.
        if (response.Code == 403 && response.Body.Contains("scope requirement")) return new ClaimResult(ClaimOutcome.NotPermitted);
        if (Http.IsAuthFailure(response)) return new ClaimResult(ClaimOutcome.SignInExpired);
        return MapClaimBody(response.Ok ? body : null);
    }

    public static ClaimResult MapClaimBody(JsonNode? body)
    {
        if (JsonRead.Str(body?["result"]) is not { } result) return new ClaimResult(ClaimOutcome.Refused);
        var until = JsonRead.Date(body?["cooldown_until"]);
        ClaimResult? Code(string? code) => code switch
        {
            "reset" or "already_used" => new ClaimResult(ClaimOutcome.Success),
            "not_limited" => new ClaimResult(ClaimOutcome.NothingToReset),
            "ineligible" or "no_grant" or "unknown_grant" or "not_next_grant" => new ClaimResult(ClaimOutcome.NoCredit),
            "reset_unconfirmed" or "stamp_indeterminate" => new ClaimResult(ClaimOutcome.Unconfirmed),
            "cooldown" or "paused" or "low_priority_active" => new ClaimResult(ClaimOutcome.UnavailableNow, until),
            _ => null,
        };
        if (result == "unavailable") return Code(JsonRead.Str(body?["reason"])) ?? new ClaimResult(ClaimOutcome.Refused);
        return Code(result) ?? new ClaimResult(ClaimOutcome.Refused);
    }

    /// <summary>From the sign-in's identity ("account/org"), else from the profile.</summary>
    private static async Task<string?> OrganizationUuid(Account account, CancellationToken ct)
    {
        if (account.Extras.TryGetValue("orgUuid", out var stored) && Guid.TryParse(stored, out _)) return stored;
        var fromIdentity = account.Identity?.Split('/') is { Length: 2 } parts ? parts[1] : null;
        if (Guid.TryParse(fromIdentity, out _)) return fromIdentity;
        var profile = await Http.Get(ProfileUrl, ProfileHeaders(account.AccessToken), ct);
        var uuid = profile.Ok ? JsonRead.Str(profile.Json()?["organization"]?["uuid"]) : null;
        if (Guid.TryParse(uuid, out _))
        {
            account.Extras["orgUuid"] = uuid!;
            return uuid;
        }
        Log.Write($"claude reset claim: no organization (profile HTTP {profile.Code})");
        return null;
    }
}
