using System.Globalization;
using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>
/// The settings reads the plugins share: one reader each, so a fallback, a "120" for an int setting and an
/// <c>env:NAME</c> key mean the same in every plugin. The host's <see cref="ISettings.Get{T}"/> stays the strict
/// typed read; these add the lenient ones the plugins used to copy.
/// </summary>
public static class SettingsExtensions
{
    /// <summary>A settings read that never throws (the store already turns a wrong type into its default): missing or
    /// unreadable is <paramref name="fallback"/>.</summary>
    public static T GetOr<T>(this ISettings? settings, string path, T fallback = default)
    {
        if (settings is null) return fallback;
        var value = settings.Get(path, fallback);
        return value is null ? fallback : value;
    }

    /// <summary>An int setting: a number (a fraction is truncated), or its string form; else <paramref name="fallback"/>.</summary>
    public static int GetInt(this ISettings? settings, string path, int fallback)
    {
        if (settings is null) return fallback;
        try
        {
            if (settings.GetNode(path) is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<double>(out var d)) return (int)d;
                if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
            }
        }
        catch { }
        return fallback;
    }

    /// <summary>A bool setting: a boolean, or its string form ("true"/"false"/"1"/"0"); else <paramref name="fallback"/>.</summary>
    public static bool GetBool(this ISettings? settings, string path, bool fallback)
    {
        if (settings is null) return fallback;
        try
        {
            if (settings.GetNode(path) is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed)) return parsed;
            }
        }
        catch { }
        return fallback;
    }

    /// <summary>A list of strings: an array (blank or non-string entries dropped), or one string;
    /// else <paramref name="fallback"/>.</summary>
    public static List<string> GetStrings(this ISettings? settings, string path, IReadOnlyList<string>? fallback = null)
    {
        fallback ??= [];
        if (settings is null) return [.. fallback];
        try
        {
            switch (settings.GetNode(path))
            {
                case JsonArray a:
                    return a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                        .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
                case JsonValue v when v.TryGetValue<string>(out var one) && !string.IsNullOrWhiteSpace(one):
                    return [one.Trim()];
            }
        }
        catch { }
        return [.. fallback];
    }

    /// <summary>A secret setting: a literal, or <c>env:NAME</c> / <c>$NAME</c> (an environment variable);
    /// else <paramref name="fallback"/>.</summary>
    public static string? GetSecret(this ISettings? settings, string path, string? fallback = null)
        => ResolveSecret(settings is null ? fallback : settings.Get(path, fallback));

    /// <summary>A value or <c>env:NAME</c> / <c>$NAME</c> (an environment variable); a bare <c>$</c> is a literal.</summary>
    public static string? ResolveSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        string? env = value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ? value[4..]
            : value.StartsWith('$') && value.Length > 1 ? value[1..] : null;
        if (env is null) return value;
        var resolved = Environment.GetEnvironmentVariable(env.Trim());
        return string.IsNullOrWhiteSpace(resolved) ? null : resolved.Trim();
    }
}
