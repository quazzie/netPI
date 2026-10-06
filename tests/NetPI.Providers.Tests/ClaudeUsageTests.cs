extern alias claudecli;

using System.Text.Json.Nodes;
using CL = claudecli::NetPI.Providers.ClaudeCli;

namespace NetPI.Providers.Tests;

/// <summary>
/// The Claude subscription usage monitor: the normalized snapshot (both response shapes), the token refresh
/// (rotated credentials written back), the refresh backoff, the stale-cache fallback and the guards that keep
/// a dead refresh token from being spent.
/// </summary>
internal static class ClaudeUsageTests
{
    private sealed record Call(string Method, string Url, string? Bearer, string? Json);

    private const string Structured = """
    {
      "limits": [
        {"kind":"session","group":"session","percent":30,"severity":"normal","resets_at":"2026-10-06T12:30:00Z","scope":null,"is_active":true},
        {"kind":"weekly_all","group":"weekly","percent":8,"severity":"normal","resets_at":"2026-10-09T03:00:00Z","scope":null,"is_active":false},
        {"kind":"weekly_scoped","group":"weekly","percent":0,"severity":"normal","resets_at":"2026-10-09T03:00:00Z","scope":{"model":{"id":null,"display_name":"Fable"},"surface":null},"is_active":false}
      ],
      "spend": {"used":{"amount_minor":0,"currency":"USD","exponent":2},"limit":null,"percent":0,"severity":"normal","enabled":false},
      "seven_day_breakdown": {"rows":[{"key":"claude_code","display_name":"Claude Code","percent":100},{"key":"chat","display_name":"Chats","percent":0}]}
    }
    """;

    private const string Flat = """
    {
      "five_hour": {"utilization": 35.5, "resets_at": "2026-02-06T22:00:00Z"},
      "seven_day": {"utilization": 14, "resets_at": "2026-02-12T20:00:00Z"},
      "seven_day_sonnet": {"utilization": 39, "resets_at": "2026-02-09T14:00:00Z"},
      "seven_day_opus": null,
      "extra_usage": {"is_enabled": true, "monthly_limit": 1000, "used_credits": 250.5, "utilization": 25.05}
    }
    """;

    // ---------------------------------------------------------------- helpers

    private static JsonObject Cfg() => new() { ["usage"] = true, ["usageRefreshMinutes"] = 5 };

    private static (CL.ClaudeUsageMonitor.UsageHttp Http, List<Call> Calls, Queue<(int, string)> Responses) HttpMock()
    {
        var calls = new List<Call>();
        var responses = new Queue<(int, string)>();
        CL.ClaudeUsageMonitor.UsageHttp http = (string method, string url, string? bearer, string? json, CancellationToken ct) =>
        {
            calls.Add(new Call(method, url, bearer, json));
            var (status, body) = responses.Count > 0 ? responses.Dequeue() : (200, "{}");
            return ValueTask.FromResult(new CL.ClaudeUsageMonitor.HttpResult(status, body));
        };
        return (http, calls, responses);
    }

    /// <summary>Point the monitor's credential search at a temp folder (restored on exit).</summary>
    private static IDisposable CredsDir(out string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"netpi-claude-usage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var prev = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", dir);
        path = Path.Combine(dir, ".credentials.json");
        return new Restore(prev);
    }

    private sealed class Restore(string? prev) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", prev);
    }

    private static void WriteCreds(string path, string access, string refresh, long expiresAtMs)
    {
        File.WriteAllText(path, new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = access,
                ["refreshToken"] = refresh,
                ["expiresAt"] = expiresAtMs,
                ["subscriptionType"] = "max",
                ["rateLimitTier"] = "default_claude_max_20x",
                ["scopes"] = new JsonArray("user:profile", "user:inference"),
            },
        }.ToJsonString());
    }

    // ---------------------------------------------------------------- tests

    public static async Task RunAsync(TestRunner t)
    {
        await t.Run("usage: the structured limits array normalizes (session, week, model-scoped, spend, breakdown)", async () =>
        {
            var snap = CL.ClaudeUsageMonitor.Normalize((JsonObject)JsonNode.Parse(Structured)!, "max", "default_claude_max_20x");
            var limits = (JsonArray)snap["limits"]!;
            t.Eq(3, limits!.Count, "limit count");
            t.Eq("session", limits![0]!["key"]!.GetValue<string>(), "first key");
            t.Eq("weekly_all", limits[1]!["key"]!.GetValue<string>(), "second key");
            t.Eq("weekly_fable", limits[2]!["key"]!.GetValue<string>(), "scoped key from the model's display name");
            t.Eq("Week (Fable)", limits[2]!["label"]!.GetValue<string>(), "scoped label");
            t.Eq(30, limits[0]!["percent"]!.GetValue<int>(), "session percent");
            t.Check(snap["spend"]!["enabled"]!.GetValue<bool>() == false, "spend disabled");
            t.Eq(0.0, snap["spend"]!["usedUsd"]!.GetValue<double>(), "used credits in dollars");
            t.Eq(2, (snap["breakdown"] as JsonArray)?.Count ?? -1, "breakdown rows");
            t.Check(snap["stale"]!.GetValue<bool>() == false, "not stale");
        });

        await t.Run("usage: the flat five_hour/seven_day keys are the fallback, null buckets skipped", async () =>
        {
            var snap = CL.ClaudeUsageMonitor.Normalize((JsonObject)JsonNode.Parse(Flat)!, "max", null);
            var limits = (JsonArray)snap["limits"]!;
            t.Eq(3, limits!.Count, "null seven_day_opus is skipped");
            t.Eq("session", limits![0]!["key"]!.GetValue<string>(), "five_hour is the session window");
            t.Eq(35.5, limits[0]!["percent"]!.GetValue<double>(), "utilization is the percent");
            t.Eq("weekly_sonnet", limits[2]!["key"]!.GetValue<string>(), "sonnet window");
            t.Check(snap["spend"]!["enabled"]!.GetValue<bool>(), "legacy extra_usage is enabled");
            t.Eq(250.5, snap["spend"]!["usedUsd"]!.GetValue<double>(), "used credits");
            t.Eq(1000, snap["spend"]!["limitUsd"]!.GetValue<double>(), "monthly limit");
        });

        await t.Run("usage: a fresh fetch is cached within the refresh interval", async () =>
        {
            using var dir = CredsDir(out var path);
            WriteCreds(path, "tok-a", "ref-a", DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
            var (http, calls, responses) = HttpMock();
            responses.Enqueue((200, Structured));
            var m = new CL.ClaudeUsageMonitor(Cfg, null, http);
            var first = await m.GetAsync(false, CancellationToken.None);
            var second = await m.GetAsync(false, CancellationToken.None);
            t.Eq(1, calls.Count, "the second read is served from the cache");
            t.Check(ReferenceEquals(first, second), "same snapshot instance");
        });

        await t.Run("usage: an expired token is refreshed, the rotated credentials are written back, and the new token is used", async () =>
        {
            using var dir = CredsDir(out var path);
            WriteCreds(path, "tok-old", "ref-old", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
            var (http, calls, responses) = HttpMock();
            responses.Enqueue((200, """{"access_token":"tok-new","refresh_token":"ref-new","expires_in":28800}"""));
            responses.Enqueue((200, Structured));
            var m = new CL.ClaudeUsageMonitor(Cfg, null, http);
            var snap = await m.GetAsync(true, CancellationToken.None);

            t.Eq(2, calls.Count, "one token POST, one usage GET");
            t.Eq("POST", calls[0].Method, "the token endpoint is called");
            t.Check(calls[0].Json!.Contains("ref-old") && calls[0].Json!.Contains("refresh_token"), "the stored refresh token is sent");
            t.Eq("tok-new", calls[1].Bearer, "the usage GET uses the new access token");
            t.Eq(3, ((JsonArray)snap["limits"]!).Count, "the snapshot parsed");
            var file = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
            t.Eq("tok-new", file["claudeAiOauth"]!["accessToken"]!.GetValue<string>(), "access token rotated in the file");
            t.Eq("ref-new", file["claudeAiOauth"]!["refreshToken"]!.GetValue<string>(), "refresh token rotated in the file");
            t.Check(file["claudeAiOauth"]!["expiresAt"]!.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "the new expiry is stored");
            t.Eq("max", file["claudeAiOauth"]!["subscriptionType"]!.GetValue<string>(), "the rest of the login is kept");
        });

        await t.Run("usage: a failed refresh is backed off — a dead refresh token is not retried", async () =>
        {
            using var dir = CredsDir(out var path);
            WriteCreds(path, "tok-old", "ref-dead", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
            var (http, calls, responses) = HttpMock();
            responses.Enqueue((429, "{}")); // the refresh grant is rate-limited
            responses.Enqueue((401, "{}")); // the stored access token is rejected
            var m = new CL.ClaudeUsageMonitor(Cfg, null, http);
            var first = await m.GetAsync(true, CancellationToken.None);
            t.Check(first["error"] != null, "no usable reading");

            // a forced second read: the refresh must not be spent again within the backoff window
            responses.Enqueue((401, "{}"));
            var postsBefore = calls.Count(c => c.Method == "POST");
            await m.GetAsync(true, CancellationToken.None);
            t.Eq(postsBefore, calls.Count(c => c.Method == "POST"), "no second refresh attempt inside the backoff");
        });

        await t.Run("usage: a 429 from the usage endpoint serves the stale cache, flagged", async () =>
        {
            using var dir = CredsDir(out var path);
            WriteCreds(path, "tok-a", "ref-a", DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
            var (http, calls, responses) = HttpMock();
            responses.Enqueue((200, Structured));
            var m = new CL.ClaudeUsageMonitor(Cfg, null, http);
            await m.GetAsync(true, CancellationToken.None);

            responses.Enqueue((429, "{}"));
            var stale = await m.GetAsync(true, CancellationToken.None);
            t.Check(stale["stale"]!.GetValue<bool>(), "flagged stale");
            t.Check(stale["error"] != null, "the reason is carried");
            t.Eq(3, ((JsonArray)stale["limits"]!).Count, "the last good limits are kept");
        });

        await t.Run("usage: a read-only credentials file is not spent — the refresh is skipped", async () =>
        {
            using var dir = CredsDir(out var path);
            WriteCreds(path, "tok-old", "ref-a", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            try
            {
                var (http, calls, responses) = HttpMock();
                responses.Enqueue((401, "{}"));
                var m = new CL.ClaudeUsageMonitor(Cfg, null, http);
                var snap = await m.GetAsync(true, CancellationToken.None);
                t.Eq(0, calls.Count(c => c.Method == "POST"), "no refresh POST: the file cannot be written back");
                t.Check(snap["error"] != null, "the failure is reported, not hidden");
            }
            finally
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
        });

        await t.Run("usage: without a CLI login the error says to run `claude auth login`", async () =>
        {
            using var dir = CredsDir(out var path);
            var (http, calls, _) = HttpMock();
            var m = new CL.ClaudeUsageMonitor(Cfg, null, http);
            var snap = await m.GetAsync(true, CancellationToken.None);
            t.Eq(0, calls.Count, "no network call without credentials");
            t.Check(snap["error"]!.ToString()!.Contains("claude auth login"), "the remedy is in the error");
        });

        await t.Run("usage: a disabled monitor does not fetch, and says so", async () =>
        {
            using var dir = CredsDir(out var path);
            WriteCreds(path, "tok-a", "ref-a", DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
            var (http, calls, responses) = HttpMock();
            var m = new CL.ClaudeUsageMonitor(() => new JsonObject { ["usage"] = false, ["usageRefreshMinutes"] = 5 }, null, http);
            var snap = await m.GetAsync(true, CancellationToken.None);
            t.Eq(0, calls.Count, "no fetch while off");
            t.Check(snap["error"]!.ToString()!.Contains("off"), "the reason is said");
        });
    }
}
