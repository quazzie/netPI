using System.Text.RegularExpressions;

namespace NetPI.Host.Storage;

/// <summary>
/// What a plugin may call a collection or an index field, checked the same way by every built-in provider (a name ends up in a table
/// or column of an engine that cares, and what one provider accepts another must not reject). Field names are also unique ignoring
/// case: an engine may fold them.
/// </summary>
internal static partial class StorageNames
{
    /// <summary>
    /// The most values an <c>In</c>/<c>NotIn</c> list may hold, the same for every provider (the port documents it): a
    /// provider that binds every value as a parameter of one statement has a cap of its own, and a list that works on one
    /// provider must work on the other.
    /// </summary>
    public const int MaxListValues = 1000;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex Pattern();

    public static void CheckCollection(string name)
    {
        if (name is null || !Pattern().IsMatch(name))
            throw new ArgumentException($"'{name}' is not a collection name (letters, digits and underscores, starting with a letter)", nameof(name));
    }

    public static void CheckSpec(CollectionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in spec.Fields.Keys)
        {
            if (!Pattern().IsMatch(field)) throw new ArgumentException($"'{field}' is not an index field name", nameof(spec));
            if (!seen.Add(field)) throw new ArgumentException($"The index field '{field}' is declared twice (names are unique ignoring case)", nameof(spec));
        }
    }

    /// <summary>A key is any non-empty string.</summary>
    public static void CheckKey(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("A document key cannot be null or empty", nameof(key));
    }
}
