using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Providers.Kit;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// The owner's Claude subscription usage (session window, weekly windows, extra-usage credits), fetched from the
/// same endpoint Claude Code's <c>/usage</c> command uses: <c>GET https://api.anthropic.com/api/oauth/usage</c> with
/// the OAuth access token the CLI stores in <c>~/.claude/.credentials.json</c> (or <c>$CLAUDE_CONFIG_DIR</c>).
/// The access token expires, so when it is expired (or the API rejects it) it is rotated through the CLI's own
/// token endpoint and the rotated credentials are written back to the file — the same flow Claude Code itself
/// uses. The endpoint is aggressively rate-limited, so readings are cached, a stale cache is served on failure,
/// and a failed refresh is backed off: the refresh budget is shared with the CLI, and spending it carelessly
/// would break the CLI's own refresh.
/// </summary>
public sealed class ClaudeUsageMonitor
{
    internal const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    internal const string TokenUrl = "https://platform.claude.com/v1/oauth/token";

    /// <summary>Claude Code's public OAuth client id (hardcoded in the CLI binary).</summary>
    internal const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// <summary>After a failed refresh, do not spend another refresh token for this long.</summary>
    internal static readonly TimeSpan RefreshBackoff = TimeSpan.FromHours(1);

    internal sealed record HttpResult(int Status, string Body);

    /// <summary>One HTTP exchange; replaced by a mock in tests (like the provider's <c>CliRunner</c>).</summary>
    internal delegate ValueTask<HttpResult> UsageHttp(string method, string url, string? bearer, string? json, CancellationToken ct);

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Func<JsonObject?> _config;
    private readonly ILogger _log;
    private readonly UsageHttp _http;
    private readonly object _gate = new();

    private JsonObject? _cache;
    private DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _refreshFailedAtUtc = DateTimeOffset.MinValue;

    public ClaudeUsageMonitor(Func<JsonObject?> config, ILogger? logger = null)
        : this(config, logger, DefaultHttp) { }

    internal ClaudeUsageMonitor(Func<JsonObject?> config, ILogger? logger, UsageHttp http)
    {
        _config = config;
        _log = logger ?? NullLogger.Instance;
        _http = http;
    }

    /// <summary>Fires after each successful fetch (the plugin publishes it on the bus as <c>usage.claude</c>).</summary>
    public Action<JsonObject>? OnFetched { get; set; }

    private static async ValueTask<HttpResult> DefaultHttp(string method, string url, string? bearer, string? json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (bearer is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (json is not null) req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var res = await SharedHttp.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new HttpResult((int)res.StatusCode, body);
    }

    /// <summary>Where the CLI keeps its login: <c>$CLAUDE_CONFIG_DIR</c> when set, else <c>~/.claude</c>.</summary>
    internal static string CredentialsPath()
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var root = string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : dir;
        return Path.Combine(root, ".credentials.json");
    }

    internal sealed record ClaudeCreds(string? AccessToken, string? RefreshToken, long? ExpiresAtMs, string? Plan, string? Tier);

    private ClaudeCreds? ReadCreds()
    {
        try
        {
            var path = CredentialsPath();
            if (!File.Exists(path)) return null;
            var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var c = o?.Obj("claudeAiOauth");
            return c is null ? null : new ClaudeCreds(
                c.Str("accessToken"),
                c.Str("refreshToken"),
                c["expiresAt"] is JsonValue ev && ev.TryGetValue<long>(out var ms) ? ms : null,
                c.Str("subscriptionType"),
                c.Str("rateLimitTier"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "claude-usage: could not read the Claude CLI credentials");
            return null;
        }
    }

    /// <summary>
    /// The current snapshot: <c>{ plan, tier, limits:[{key,label,percent,resetsAt,active,severity}], spend, breakdown,
    /// fetchedAt, stale }</c>, plus <c>error</c> when no usable reading exists. A fresh fetch runs when
    /// <paramref name="force"/> is set, the monitor is on, and the cache is older than the configured refresh
    /// interval; on failure the stale cache (flagged <c>stale</c>) is served.
    /// </summary>
    public async Task<JsonObject> GetAsync(bool force, CancellationToken ct)
    {
        var o = ClaudeCliOptions.Parse(_config());
        var ttl = TimeSpan.FromMinutes(o.UsageRefreshMinutes);
        lock (_gate)
        {
            if (!force && o.Usage && _cache is { } fresh && DateTimeOffset.UtcNow - _cachedAtUtc < ttl)
                return fresh;
        }
        if (!o.Usage)
            return new JsonObject { ["limits"] = new JsonArray(), ["error"] = "The usage monitor is off (providers.claudeCli.usage)." };
        try
        {
            var snap = await FetchAsync(ct).ConfigureAwait(false);
            lock (_gate) { _cache = snap; _cachedAtUtc = DateTimeOffset.UtcNow; }
            OnFetched?.Invoke(snap);
            return snap;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "claude-usage: fetch failed");
            lock (_gate)
            {
                if (_cache is { } c)
                {
                    var stale = (JsonObject)c.DeepClone()!;
                    stale["stale"] = true;
                    stale["error"] = J.Truncate(ex.Message, 200);
                    return stale;
                }
            }
            return new JsonObject { ["limits"] = new JsonArray(), ["error"] = J.Truncate(ex.Message, 200) };
        }
    }

    /// <summary>The background refresh loop the plugin runs until the plugin stops: fetch each interval (re-read from
    /// settings, so a changed interval takes effect on the next cycle).</summary>
    public async Task WatchAsync(CancellationToken ct)
    {
        try { await GetAsync(false, ct).ConfigureAwait(false); } catch { /* first fetch may fail; the loop keeps trying */ }
        while (!ct.IsCancellationRequested)
        {
            var minutes = ClaudeCliOptions.Parse(_config()).UsageRefreshMinutes;
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { await GetAsync(false, ct).ConfigureAwait(false); } catch { /* GetAsync logs; the next cycle retries */ }
        }
    }

    private async Task<JsonObject> FetchAsync(CancellationToken ct)
    {
        var creds = ReadCreds() ?? throw new InvalidOperationException(
            $"No Claude CLI login at {CredentialsPath()}. Run `claude auth login` once: the usage monitor reads the CLI's subscription login.");
        if (creds.AccessToken is not { Length: > 0 })
            throw new InvalidOperationException("The Claude CLI login has no access token; run `claude auth login` again.");

        var access = creds.AccessToken;
        if (creds.ExpiresAtMs is { } exp && exp <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000)
            access = await RefreshTokenAsync(creds, ct).ConfigureAwait(false) ?? access; // a failed refresh keeps the stored token

        var res = await _http("GET", UsageUrl, access, null, ct).ConfigureAwait(false);
        if (res.Status is 401 or 403 && access == creds.AccessToken)
        {
            // the stored token was rejected before its stated expiry: one fresh attempt
            var fresh = await RefreshTokenAsync(creds, ct).ConfigureAwait(false);
            if (fresh is not null) res = await _http("GET", UsageUrl, fresh, null, ct).ConfigureAwait(false);
        }
        if (res.Status != 200)
            throw new InvalidOperationException($"The Claude usage endpoint answered HTTP {res.Status} (it is rate-limited; the last good reading is kept).");
        var raw = JsonNode.Parse(res.Body) as JsonObject
            ?? throw new InvalidOperationException("The Claude usage endpoint returned no JSON.");
        return Normalize(raw, creds.Plan, creds.Tier);
    }

    /// <summary>
    /// Rotates the access token through the CLI's token endpoint and writes the rotated credentials back (the CLI's
    /// own flow). Returns the new access token, or null when a refresh is impossible or failed — the caller then
    /// keeps using the stored token. A failure is backed off for <see cref="RefreshBackoff"/> so a dead refresh
    /// token is not retried against a budget the CLI itself needs.
    /// </summary>
    private async Task<string?> RefreshTokenAsync(ClaudeCreds creds, CancellationToken ct)
    {
        if (creds.RefreshToken is not { Length: > 0 }) return null;
        if (DateTimeOffset.UtcNow < _refreshFailedAtUtc + RefreshBackoff) return null;
        var path = CredentialsPath();
        // A failed write after a successful refresh would leave the file with a dead refresh token and brick the
        // CLI's own refresh, so the writability is checked before the refresh token is spent.
        if (!CanWrite(path))
        {
            _log.LogDebug("claude-usage: {Path} is not writable; not spending the refresh token", path);
            return null;
        }
        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = creds.RefreshToken,
            ["client_id"] = ClientId,
        }.ToJsonString();
        var res = await _http("POST", TokenUrl, null, body, ct).ConfigureAwait(false);
        if (res.Status is not (200 or 201))
        {
            _refreshFailedAtUtc = DateTimeOffset.UtcNow;
            _log.LogWarning("claude-usage: token refresh failed (HTTP {Status}); not retrying for an hour", res.Status);
            return null;
        }
        string? newAccess = null, newRefresh = null;
        long? newExpiryMs = null;
        try
        {
            using var doc = JsonDocument.Parse(res.Body);
            if (doc.RootElement.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String)
                newAccess = at.GetString();
            if (doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number)
                newExpiryMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(ei.GetDouble() * 1000);
            // The server omits refresh_token when it keeps the old one; then the stored one is still good.
            if (doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
                newRefresh = rt.GetString();
        }
        catch (JsonException) { /* fall through: nothing usable in the answer */ }
        if (newAccess is null)
        {
            _refreshFailedAtUtc = DateTimeOffset.UtcNow;
            _log.LogWarning("claude-usage: the token endpoint answered HTTP {Status} without an access token", res.Status);
            return null;
        }
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidOperationException("the credentials file is not JSON");
            var c = o["claudeAiOauth"] as JsonObject ?? new JsonObject();
            c["accessToken"] = newAccess;
            if (newRefresh is not null) c["refreshToken"] = newRefresh;
            if (newExpiryMs is not null) c["expiresAt"] = newExpiryMs.Value;
            o["claudeAiOauth"] = c;
            WriteCredsAtomic(path, o);
        }
        catch (Exception ex)
        {
            // The server has already rotated: the stored refresh token is now dead. Say so loudly — the only
            // recovery is `claude auth login`.
            _log.LogError(ex,
                "claude-usage: refreshed the token but could not write {Path} — the stored refresh token is now invalid; run `claude auth login`", path);
        }
        return newAccess;
    }

    private static bool CanWrite(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir)) return false;
            if (File.Exists(path)) return (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0;
            var probe = Path.Combine(dir, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Same-directory temp file + move: a crash never leaves a half-written credentials file in place.</summary>
    internal static void WriteCredsAtomic(string path, JsonObject doc)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, $".credentials-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, doc.ToJsonString());
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// The endpoint's response — the structured <c>limits</c> array when present, else the flat
    /// <c>five_hour</c>/<c>seven_day</c> keys — to the snapshot shape the UI renders. Tolerant of the response
    /// shape changing: an unknown shape yields an empty limit list, not a failure.
    /// </summary>
    internal static JsonObject Normalize(JsonObject raw, string? plan, string? tier)
    {
        var limits = new JsonArray();
        if (raw.Arr("limits") is { Count: > 0 } structured)
        {
            foreach (var n in structured)
            {
                if (n is not JsonObject l) continue;
                var scopeName = l.Obj("scope")?.Obj("model")?.Str("display_name");
                var (key, label) = (l.Str("kind"), scopeName) switch
                {
                    ("session", _) => ("session", "Session (5-hour window)"),
                    ("weekly_all", _) => ("weekly_all", "Week (all models)"),
                    ("weekly_scoped", { Length: > 0 } name) => ($"weekly_{name.ToLowerInvariant()}", $"Week ({name})"),
                    ("weekly_scoped", _) => ("weekly_scoped", "Week (model window)"),
                    _ => (null, null),
                };
                if (key is null) continue;
                limits.Add(new JsonObject
                {
                    ["key"] = key,
                    ["label"] = label,
                    ["percent"] = l["percent"]?.DeepClone() ?? JsonValue.Create(0),
                    ["resetsAt"] = l.Str("resets_at"),
                    ["active"] = l.Bool("is_active", false),
                    ["severity"] = l.Str("severity"),
                });
            }
        }
        else
        {
            void Flat(string key, string label, string field)
            {
                var b = raw.Obj(field);
                if (b is null) return;
                limits.Add(new JsonObject
                {
                    ["key"] = key,
                    ["label"] = label,
                    ["percent"] = b["utilization"]?.DeepClone() ?? JsonValue.Create(0),
                    ["resetsAt"] = b.Str("resets_at"),
                    ["active"] = true,
                });
            }
            Flat("session", "Session (5-hour window)", "five_hour");
            Flat("weekly_all", "Week (all models)", "seven_day");
            Flat("weekly_sonnet", "Week (Sonnet)", "seven_day_sonnet");
            Flat("weekly_opus", "Week (Opus)", "seven_day_opus");
        }

        JsonObject? spend = null;
        if (raw.Obj("spend") is { } s)
        {
            var used = s.Obj("used");
            var minor = used?.Int("amount_minor") ?? 0;
            var exp = used?.Int("exponent") ?? 2;
            spend = new JsonObject
            {
                ["enabled"] = s.Bool("enabled", false),
                ["percent"] = s["percent"]?.DeepClone() ?? JsonValue.Create(0),
                ["usedUsd"] = minor / Math.Pow(10, Math.Clamp(exp, 0, 6)),
                ["limitUsd"] = s["limit"]?.DeepClone(),
            };
        }
        else if (raw.Obj("extra_usage") is { } e)
        {
            spend = new JsonObject
            {
                ["enabled"] = e.Bool("is_enabled", false),
                ["percent"] = e["utilization"]?.DeepClone(),
                ["usedUsd"] = e["used_credits"]?.DeepClone(),
                ["limitUsd"] = e["monthly_limit"]?.DeepClone(),
            };
        }

        var snapshot = new JsonObject
        {
            ["plan"] = plan,
            ["tier"] = tier,
            ["limits"] = limits,
            ["spend"] = spend,
            ["fetchedAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["stale"] = false,
        };
        if (raw.Obj("seven_day_breakdown")?.Arr("rows") is { Count: > 0 } rows)
        {
            var bd = new JsonArray();
            foreach (var r in rows)
            {
                if (r is not JsonObject ro) continue;
                bd.Add(new JsonObject
                {
                    ["key"] = ro.Str("key"),
                    ["label"] = ro.Str("display_name") ?? ro.Str("key") ?? "—",
                    ["percent"] = ro["percent"]?.DeepClone() ?? JsonValue.Create(0),
                });
            }
            snapshot["breakdown"] = bd;
        }
        return snapshot;
    }
}
