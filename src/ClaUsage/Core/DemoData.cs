using System.Text.Json.Nodes;
using ClaUsage.Providers;

namespace ClaUsage.Core;

/// <summary>
/// Sample provider responses, shaped like the real ones, for <c>--demo</c> (UI without signing in) and
/// <c>--selftest</c> (mapper check). Nothing here touches the network or the saved accounts.
/// </summary>
public static class DemoData
{
    public static List<Account> Accounts() => new()
    {
        new Account { Id = "demo-claude", Kind = ProviderKind.Claude, Label = "you@example.com" },
        new Account { Id = "demo-codex", Kind = ProviderKind.Codex, Label = "you@example.com" },
        new Account { Id = "demo-openrouter", Kind = ProviderKind.OpenRouter, Label = "OpenRouter Account" },
    };

    public static UsageSnapshot For(Account account, DateTimeOffset now) => account.Kind switch
    {
        ProviderKind.Claude => ClaudeProvider.MapUsage(ClaudeUsage(now), ClaudeProvider.FormatPlan("claude_max", "default_claude_max_5x")),
        ProviderKind.Codex => CodexProvider.MapUsage(CodexUsage(now), _ => null, now),
        ProviderKind.OpenRouter => OpenRouterProvider.Map(OpenRouterKey()["data"]!, OpenRouterCredits(), OpenRouterKeys(),
            OpenRouterProvider.MapActivity(OpenRouterActivity(now), now), now),
        _ => new UsageSnapshot(),
    };

    public static JsonNode ClaudeUsage(DateTimeOffset now) => JsonNode.Parse($$"""
    {
      "five_hour": { "utilization": 78.0, "resets_at": "{{Iso(now.AddMinutes(95))}}" },
      "seven_day": { "utilization": 41.0, "resets_at": "{{Iso(now.AddDays(3.2))}}" },
      "seven_day_sonnet": { "utilization": 12.0, "resets_at": "{{Iso(now.AddDays(3.2))}}" },
      "seven_day_opus": null,
      "limits": [
        { "kind": "weekly_scoped", "percent": 64, "resets_at": "{{Iso(now.AddDays(3.2))}}", "scope": { "model": { "display_name": "Fable" } } }
      ],
      "extra_usage": { "is_enabled": true, "currency": "USD", "decimal_places": 2, "used_credits": 1234, "monthly_limit": 5000 }
    }
    """)!;

    public static JsonNode CodexUsage(DateTimeOffset now) => JsonNode.Parse($$"""
    {
      "plan_type": "plus",
      "rate_limit": {
        "primary_window": { "used_percent": 22, "limit_window_seconds": 18000, "reset_after_seconds": 9000 },
        "secondary_window": { "used_percent": 96, "limit_window_seconds": 604800, "reset_at": {{now.AddDays(1.5).ToUnixTimeSeconds()}} }
      },
      "additional_rate_limits": [
        { "limit_name": "GPT-5-Codex-Spark", "rate_limit": {
          "primary_window": { "used_percent": 5, "limit_window_seconds": 18000, "reset_after_seconds": 12000 } } }
      ],
      "credits": { "has_credits": true, "balance": "137" },
      "rate_limit_reset_credits": { "available_count": 1 }
    }
    """)!;

    /// <summary>A Management key with a monthly budget and the free-model daily quota.</summary>
    public static JsonNode OpenRouterKey() => JsonNode.Parse("""
    {
      "data": {
        "label": "mgmt", "is_management_key": true, "is_free_tier": false,
        "limit": null, "limit_remaining": null, "limit_reset": null,
        "usage_daily": 0, "usage_weekly": 0, "usage_monthly": 0,
        "effective_budget": { "limit_usd": 100, "spend_usd": 63.5, "reset_interval": "monthly" },
        "free_model_daily_requests": { "limit": 1000, "used": 120 }
      }
    }
    """)!;

    public static JsonNode OpenRouterCredits() => JsonNode.Parse("""
        { "data": { "total_credits": 150, "total_usage": 87.25 } }
        """)!;

    public static List<JsonNode?> OpenRouterKeys() => new()
    {
        JsonNode.Parse("""{ "usage_daily": 1.5, "usage_weekly": 9.25, "usage_monthly": 40 }"""),
        JsonNode.Parse("""{ "usage_daily": 0.5, "usage_weekly": 2.75, "usage_monthly": 23.5 }"""),
    };

    public static JsonNode OpenRouterActivity(DateTimeOffset now)
    {
        var today = now.UtcDateTime.Date;
        string Day(int back) => today.AddDays(-back).ToString("yyyy-MM-dd");
        return JsonNode.Parse($$"""
        { "data": [
          { "date": "{{Day(0)}}", "model": "a", "usage": 99 },
          { "date": "{{Day(1)}}", "model": "a", "usage": 3.0 },
          { "date": "{{Day(1)}}", "model": "b", "usage": 1.25 },
          { "date": "{{Day(10)}}", "model": "a", "usage": 20 },
          { "date": "{{Day(31)}}", "model": "a", "usage": 500 }
        ] }
        """)!;
    }

    private static string Iso(DateTimeOffset d) => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>Runs both mappers on the samples and returns a readable report, failing loudly on surprises.</summary>
    public static (bool Ok, string Report) SelfTest()
    {
        var now = DateTimeOffset.UtcNow;
        var sb = new System.Text.StringBuilder();
        var ok = true;
        void Expect(bool condition, string what)
        {
            sb.AppendLine($"  [{(condition ? "ok" : "FAIL")}] {what}");
            ok &= condition;
        }

        var claude = ClaudeProvider.MapUsage(ClaudeUsage(now), ClaudeProvider.FormatPlan("claude_max", "default_claude_max_5x"));
        sb.AppendLine($"Claude plan={claude.Plan}");
        foreach (var l in claude.Lines) sb.AppendLine($"  {l.Label}: {Format.Value(l)} resets={l.ResetsAt:u} pace={l.PaceFraction(now):0.00}");
        Expect(claude.Plan == "Max", "plan Max");
        Expect(claude.Lines.Select(l => l.Label).SequenceEqual(new[] { "Session", "Weekly", "Sonnet", "Fable", "Extra usage" }), "Claude rows in order, null Opus skipped");
        Expect(claude.Lines[0].Used == 78 && claude.Lines[0].ResetsAt is { } r && Math.Abs((r - now.AddMinutes(95)).TotalSeconds) < 2, "Session 78% resets in 95m");
        Expect(Format.Value(claude.Lines[4]) == "$12.34 / $50.00", "extra usage in dollars");
        Expect(ClaudeProvider.FormatPlan("claude_pro", null) == "Pro", "Pro plan");
        Expect(ClaudeProvider.FormatPlan("claude_max", "default_claude_max_20x") == "Ultra", "Max 20x reads Ultra");
        Expect(ClaudeProvider.FormatPlan("claude_team", "tier_5x") == "Team 5x", "Team 5x");

        var codex = CodexProvider.MapUsage(CodexUsage(now), _ => null, now);
        sb.AppendLine($"Codex plan={codex.Plan}");
        foreach (var l in codex.Lines) sb.AppendLine($"  {l.Label}: {Format.Value(l)} resets={l.ResetsAt:u} period={l.PeriodMs}");
        Expect(codex.Plan == "Plus", "plan Plus");
        Expect(codex.Lines.Select(l => l.Label).SequenceEqual(new[] { "Session", "Weekly", "Spark", "Rate limit resets", "Credits" }), "Codex rows in order");
        Expect(codex.Lines[1].Used == 96 && codex.Lines[1].PeriodMs == Periods.WeekMs, "Weekly 96% with week period");
        Expect(codex.Lines[4].Text == "$5.48 · 137 credits", "credits at 4 cents");

        // A sole weekly limit moved into the primary slot must land on Weekly, not Session.
        var moved = CodexProvider.MapUsage(JsonNode.Parse("""
            { "rate_limit": { "primary_window": { "used_percent": 50, "limit_window_seconds": 604800, "reset_after_seconds": 100 } } }
            """)!, _ => null, now);
        Expect(moved.Lines.Count == 1 && moved.Lines[0].Label == "Weekly", "sole weekly in primary slot maps to Weekly");

        var ms = JsonRead.Date(JsonValue.Create(1767225600000d));
        Expect(ms == DateTimeOffset.FromUnixTimeSeconds(1767225600), "unix ms date");
        Expect(OAuthFlow.ParsePastedCode("abc#st", "st") == "abc", "pasted code#state");
        Expect(OAuthFlow.ParsePastedCode("http://localhost:1/callback?code=xyz&state=st", "st") == "xyz", "pasted callback URL");
        var router = For(new Account { Kind = ProviderKind.OpenRouter }, now);
        sb.AppendLine($"OpenRouter plan={router.Plan}");
        foreach (var l in router.Lines) sb.AppendLine($"  {l.Label}: {Format.Value(l)} resets={l.ResetsAt:u}");
        string RouterValue(string label) => router.Lines.FirstOrDefault(l => l.Label == label) is { } line ? Format.Value(line) : "missing";
        Expect(router.Plan == "Pay-as-you-go", "plan Pay-as-you-go");
        Expect(RouterValue("Balance") == "$62.75", "balance = credits - usage");
        Expect(RouterValue("Today") == "$2.00" && RouterValue("This week") == "$12.00" && RouterValue("This month") == "$63.50", "spend summed over keys");
        Expect(RouterValue("Yesterday") == "$4.25", "yesterday from activity, both models");
        Expect(RouterValue("Prior 30 days") == "$24.25", "prior 30 days excludes today and day 31");
        Expect(RouterValue("Budget") == "$63.50 / $100.00", "monthly budget");
        Expect(RouterValue("Free requests") == "120 / 1000 requests", "free requests");
        var (weekEnd, _) = OpenRouterProvider.UtcWindowEnd("weekly", new DateTimeOffset(2026, 11, 11, 15, 0, 0, TimeSpan.Zero));
        Expect(weekEnd == new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), "week resets Monday 00:00 UTC");
        var standard = OpenRouterProvider.Map(JsonNode.Parse("""
            { "is_management_key": false, "limit": 20, "limit_remaining": 5, "limit_reset": "daily",
              "usage_daily": 15, "usage_weekly": 30, "usage_monthly": 45 }
            """)!, null, null, null, now);
        Expect(standard.Lines.Select(l => l.Label).SequenceEqual(new[] { "Today", "This week", "This month", "Key limit" })
               && Format.Value(standard.Lines[3]) == "$15.00 / $20.00", "standard key: own spend and key limit");

        // Loopback OAuth callback: a real HTTP round trip on 127.0.0.1 and on "localhost".
        try
        {
            using var server = LoopbackServer.Start("st8");
            var port = new Uri(server.RedirectUri).Port;
            var wrong = Http.Client.GetAsync($"http://127.0.0.1:{port}/callback?code=bad&state=nope").GetAwaiter().GetResult();
            Expect((int)wrong.StatusCode == 400 && !server.Code.IsCompleted, "callback with wrong state rejected");
            var right = Http.Client.GetAsync($"http://localhost:{port}/callback?code=good&state=st8").GetAwaiter().GetResult();
            Expect(right.IsSuccessStatusCode && server.Code.Wait(2000) && server.Code.Result == "good", "callback delivers the code");
        }
        catch (Exception e)
        {
            Expect(false, $"loopback server: {e.Message}");
        }
        return (ok, sb.ToString());
    }
}
