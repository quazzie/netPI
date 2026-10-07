using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// Asks Anthropic for the subscription's usage with the token the Claude CLI is logged in with (the owner's decision, 2026-10-07:
/// pressing the context ring reads the live figure instead of waiting for the next call). The token is read from the CLI's
/// credentials file for the one request: not kept, not logged, not renewed here (the CLI renews it when it runs).
/// </summary>
internal static class ClaudeUsageApi
{
    private const string Url = "https://api.anthropic.com/api/oauth/usage";
    private const string Expired = "The Claude CLI's login has expired: run it once (any chat on a Claude model) to renew it, then press the ring again.";

    /// <summary>The CLI's credentials file: <c>$CLAUDE_CONFIG_DIR</c> or <c>~/.claude</c>.</summary>
    internal static string CredentialsPath() => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        ".credentials.json");

    internal static async Task<JsonObject> FetchAsync(HttpClient http, string credentialsPath, CancellationToken ct)
    {
        JsonObject? oauth;
        try { oauth = (JsonNode.Parse(await File.ReadAllTextAsync(credentialsPath, ct).ConfigureAwait(false)) as JsonObject)?.Obj("claudeAiOauth"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // macOS keeps the CLI's login in the Keychain, not in a file: say so instead of "not signed in"
            throw new RpcException("unavailable", OperatingSystem.IsMacOS() && ex is FileNotFoundException or DirectoryNotFoundException
                ? "On macOS the Claude CLI keeps its login in the Keychain, which NetPI does not read: the reading comes from the CLI's own output after each call."
                : "The Claude CLI's login was not found: sign in with `claude auth login`.");
        }
        var token = oauth?.Str("accessToken");
        if (string.IsNullOrEmpty(token)) throw new RpcException("unavailable", "The Claude CLI is not signed in with a subscription: `claude auth login`.");
        if (oauth!["expiresAt"] is JsonValue e && e.TryGetValue<long>(out var ms) && ms <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new RpcException("unavailable", Expired);

        using var req = new HttpRequestMessage(HttpMethod.Get, Url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized) throw new RpcException("unavailable", Expired);
        if (!res.IsSuccessStatusCode) throw new RpcException("unavailable", $"Anthropic's usage answered {(int)res.StatusCode}.");
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
        return (body is null ? null : ClaudeUsageTracker.NormalizeApi(body)) ?? throw new RpcException("unavailable", "Anthropic's usage answer had no usage windows.");
    }
}
