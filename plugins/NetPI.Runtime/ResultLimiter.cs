namespace NetPI.Runtime;

/// <summary>
/// The cap on what one tool result costs the model: a result longer than <c>agent.maxToolResultChars</c> (20000) is
/// written whole to a file (by <paramref name="save"/>) and the model gets its start and end with the path, to read
/// the rest in parts or search it instead of losing it.
/// </summary>
internal static class ResultLimiter
{
    public static string Limit(string content, int max, Func<string, string?> save)
    {
        if (max <= 0 || content.Length <= max) return content;
        var file = save(content);
        var where = file is null
            ? "It could not be saved; narrow the request (offset/limit, a more specific pattern, head/tail) to see it."
            : $"The whole result is in {file}: read the part you need (read with offset/limit) or search it (grep) instead of running the call again.";
        return TextLimit.HeadTail(content, max, 2, (omitted, total) =>
            $"\n\n[... {omitted:N0} of {total:N0} characters not shown (limit {max:N0}). {where} ...]\n\n");
    }
}