namespace NetPI;

/// <summary>
/// Where a credential hides inside a value that is not named as one: a URL's userinfo, its query values, or a path
/// segment shaped like a generated token (ha-mcp's secret path, a webhook id). A read-only view masks these parts and
/// keeps the rest readable (the host, the port, the plain path); the one place a value is edited keeps it whole
/// (idea-3q0uzn, after idea-csuckr). Plugins do not share code, so the shape lives in the contracts.
/// </summary>
public static class SecretText
{
    /// <summary>What a masked value reads as: its length, never a part of it.</summary>
    public static string Marker(int length) => $"<secret, {length} chars>";

    private static readonly char[] Separators = ['-', '_', '~', '.', '+', '='];

    /// <summary>
    /// A path segment or a bare word that looks generated: 16 or more characters of letters, digits and <c>- _ ~ . + =</c>,
    /// of which at least 12 are in random-looking runs (<see cref="RandomLooking"/>). Words, numbers and the slugs made of
    /// them are not (<c>server-filesystem</c>, <c>claude-sonnet-4-5-20250929</c>, <c>homeassistant</c>, <c>v1</c>); a
    /// hex or base64 token, a UUID and ha-mcp's <c>private_…</c> path are. When unsure it masks: a read-only view that
    /// hides a version string costs a glance, one that prints a token costs the token.
    /// </summary>
    public static bool TokenShaped(string? value)
    {
        if (value is null || value.Length < 16) return false;
        foreach (var c in value)
            if (!char.IsAsciiLetterOrDigit(c) && Array.IndexOf(Separators, c) < 0) return false;
        var random = 0;
        foreach (var part in value.Split(Separators))
            if (RandomLooking(part)) random += part.Length;
        return random >= 12;
    }

    /// <summary>A run of four or more that mixes letters and digits (<c>a8Kd93</c>, <c>550e8400</c>), or flips case at least
    /// once every three characters (<c>ZkqWpLmT</c>; <c>HomeAssistant</c> and <c>McpServerIntegration</c> do not).</summary>
    private static bool RandomLooking(string part)
    {
        if (part.Length < 4) return false;
        bool letter = false, digit = false;
        var flips = 0;
        for (var i = 0; i < part.Length; i++)
        {
            var c = part[i];
            if (char.IsAsciiDigit(c)) digit = true;
            else letter = true;
            if (i > 0 && char.IsAsciiLetter(c) && char.IsAsciiLetter(part[i - 1]) && char.IsAsciiLetterUpper(c) != char.IsAsciiLetterUpper(part[i - 1]))
                flips++;
        }
        return letter && digit || flips * 3 >= part.Length;
    }

    /// <summary>
    /// The URL with its secrets masked: userinfo, every query value, and each token-shaped path segment become a
    /// <see cref="Marker"/>. Null when the value is not an absolute URL or has nothing to mask, so a caller can tell
    /// "masked" from "plain" and leave a plain URL as it was written.
    /// </summary>
    public static string? MaskUrl(string? value)
    {
        if (string.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.IsFile || uri.IsUnc) return null;
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return null;
        var start = schemeEnd + 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], start);
        if (authorityEnd < 0) authorityEnd = value.Length;
        var authority = value[start..authorityEnd];
        var masked = false;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = Marker(at) + authority[at..];
            masked = true;
        }
        var rest = value[authorityEnd..];
        var fragmentAt = rest.IndexOf('#');
        var fragment = fragmentAt >= 0 ? rest[fragmentAt..] : "";
        if (fragmentAt >= 0) rest = rest[..fragmentAt];
        var queryAt = rest.IndexOf('?');
        var path = queryAt >= 0 ? rest[..queryAt] : rest;
        var query = queryAt >= 0 ? rest[(queryAt + 1)..] : null;
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
            if (TokenShaped(Uri.UnescapeDataString(segments[i])))
            {
                segments[i] = Marker(segments[i].Length);
                masked = true;
            }
        string? maskedQuery = null;
        if (query is not null)
        {
            var pairs = query.Split('&');
            for (var i = 0; i < pairs.Length; i++)
            {
                var eq = pairs[i].IndexOf('=');
                if (eq < 0 || eq == pairs[i].Length - 1) continue;
                pairs[i] = pairs[i][..(eq + 1)] + Marker(pairs[i].Length - eq - 1);
                masked = true;
            }
            maskedQuery = string.Join('&', pairs);
        }
        if (fragment.Length > 1 && TokenShaped(fragment[1..]))
        {
            fragment = "#" + Marker(fragment.Length - 1);
            masked = true;
        }
        if (!masked) return null;
        return value[..start] + authority + string.Join('/', segments) + (maskedQuery is null ? "" : "?" + maskedQuery) + fragment;
    }

    /// <summary>
    /// The secret parts <see cref="MaskUrl"/> would hide, as they appear in the URL (and unescaped): an error message that
    /// quotes the URL, or only its path, is masked by replacing these.
    /// </summary>
    public static IReadOnlyList<string> UrlSecrets(string? value)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.IsFile || uri.IsUnc) return found;
        if (uri.UserInfo.Length > 0) found.Add(uri.UserInfo);
        foreach (var segment in uri.AbsolutePath.Split('/'))
            if (TokenShaped(Uri.UnescapeDataString(segment))) found.Add(segment);
        if (uri.Query.Length > 1)
            foreach (var pair in uri.Query[1..].Split('&'))
                if (pair.IndexOf('=') is var eq and >= 0 && eq < pair.Length - 1) found.Add(pair[(eq + 1)..]);
        var more = found.Select(Uri.UnescapeDataString).Where(s => !found.Contains(s)).ToList();
        found.AddRange(more);
        found.RemoveAll(s => s.Length < 4);
        return found.Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToList();
    }

    /// <summary>The text with each of <paramref name="secrets"/> replaced by its <see cref="Marker"/>.</summary>
    public static string? Without(string? text, IEnumerable<string> secrets)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var secret in secrets)
            if (secret.Length > 0) text = text.Replace(secret, Marker(secret.Length), StringComparison.Ordinal);
        return text;
    }
}
