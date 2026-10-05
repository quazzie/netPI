using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Compaction;

/// <summary>
/// The summarizer's prompts, after pi's: a structured checkpoint the next model continues from; with an earlier summary,
/// strict rules to merge the new messages into it; for the start of a turn too long to keep, a separate prefix summary.
/// pi's "## Goal" is "## Task" here: a goal is what the user sets with /goal, which a summary must not look like.
/// </summary>
public static class SummaryPrompts
{
    public const string System =
        """
        You are a context summarization assistant. Your task is to read a conversation between a user and an AI coding
        agent, then produce a structured summary following the exact format specified. The summary replaces that part of
        the conversation in the agent's context, so another model will continue the work from it.
        Do NOT continue the conversation. Do NOT respond to any questions in the conversation. Never invent anything that is
        not in the conversation. Write in the language the user writes in. ONLY output the structured summary.
        """;

    public const string Format =
        """
        ## Task
        [What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]

        ## Constraints & Preferences
        - [Any constraints, preferences, or requirements mentioned by user]
        - [Or "(none)" if none were mentioned]

        ## Progress
        ### Done
        - [x] [Completed tasks/changes]

        ### In Progress
        - [ ] [Current work]

        ### Blocked
        - [Issues preventing progress, if any]

        ## Key Decisions
        - **[Decision]**: [Brief rationale]

        ## Next Steps
        1. [Ordered list of what should happen next]

        ## Critical Context
        - [Any data, examples, or references needed to continue]
        - [Or "(none)" if not applicable]
        """;

    public const string Initial =
        "The messages above are a conversation to summarize. Create a structured context checkpoint summary that another " +
        "LLM will use to continue the work.\n\nUse this EXACT format:\n\n" + Format +
        "\n\nKeep each section concise. Preserve exact file paths, function names, and error messages.";

    public const string Update =
        "The messages above are NEW conversation messages to incorporate into the existing summary provided in " +
        "<previous-summary> tags.\n\n" +
        """
        Update the existing structured summary with new information. RULES:
        - PRESERVE all existing information from the previous summary
        - ADD new progress, decisions, and context from the new messages
        - UPDATE the Progress section: move items from "In Progress" to "Done" when completed
        - UPDATE "Next Steps" based on what was accomplished
        - PRESERVE exact file paths, function names, and error messages
        - If something is no longer relevant, you may remove it

        Use this EXACT format:

        """ + "\n" + Format +
        "\n\nKeep each section concise. Preserve exact file paths, function names, and error messages.";

    public const string TurnPrefixFormat =
        """
        ## Original Request
        [What did the user ask for in this turn?]

        ## Early Progress
        - [Key decisions and work done in the prefix]

        ## Context for Suffix
        - [Information needed to understand the retained recent work]
        """;

    public const string TurnPrefix =
        "This is the PREFIX of a turn that was too large to keep. The SUFFIX (recent work) is retained.\n\n" +
        "Summarize the prefix to provide context for the retained suffix:\n\n" + TurnPrefixFormat +
        "\n\nBe concise. Focus on what's needed to understand the kept suffix.";

    public const string TurnPrefixUpdate =
        "The messages above continue the PREFIX of a turn that was too large to keep; its earlier part is summarized in " +
        "<previous-summary> tags. The SUFFIX (recent work) is retained. Update that summary with the new messages, " +
        "preserving what it says, in this format:\n\n" + TurnPrefixFormat +
        "\n\nBe concise. Focus on what's needed to understand the kept suffix.";

    public const string SplitTurnHeading = "\n\n---\n\n**Turn Context (split turn):**\n\n";

    /// <summary>The user message for one summarizer call: the conversation, the summary so far, the instructions.</summary>
    public static string Build(string conversation, string? previousSummary, bool turnPrefix, string? focus)
    {
        var sb = new StringBuilder("<conversation>\n").Append(conversation).Append("\n</conversation>\n\n");
        if (previousSummary is not null) sb.Append("<previous-summary>\n").Append(previousSummary.Trim()).Append("\n</previous-summary>\n\n");
        sb.Append(turnPrefix ? (previousSummary is null ? TurnPrefix : TurnPrefixUpdate) : (previousSummary is null ? Initial : Update));
        if (!turnPrefix && !string.IsNullOrWhiteSpace(focus)) sb.Append("\n\nAdditional focus: ").Append(focus.Trim());
        return sb.ToString();
    }
}

/// <summary>
/// The files read and modified, taken from the tool calls (not written by the model) and carried from summary to
/// summary: appended to each summary as &lt;read-files&gt; and &lt;modified-files&gt;, kept in its meta.
/// </summary>
public static class FileLists
{
    private static readonly string[] PathArgs = ["path", "file_path", "filePath", "file", "filename"];
    private static readonly Regex Blocks = new(@"\s*<(read-files|modified-files)>.*?</\1>", RegexOptions.Singleline | RegexOptions.Compiled);

    public static (List<string> Read, List<string> Modified) Collect(IEnumerable<ChatMessage> messages, JsonObject? previousMeta)
    {
        var read = new SortedSet<string>(StringComparer.Ordinal);
        var modified = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var n in previousMeta?["readFiles"] as JsonArray ?? []) if (Str(n) is { } p) read.Add(p);
        foreach (var n in previousMeta?["modifiedFiles"] as JsonArray ?? []) if (Str(n) is { } p) modified.Add(p);
        foreach (var m in messages)
        {
            if (m.Role != MessageRole.Assistant) continue;
            foreach (var call in m.ToolCalls)
            {
                // An edit of several files names them in files[].path.
                if (call.Name == "edit" && EditFiles(call.Arguments) is { Count: > 0 } several)
                {
                    foreach (var p in several) modified.Add(p);
                    continue;
                }
                var (path, host, action) = PathOf(call.Arguments);
                if (path is null) continue;
                // a tool with actions counts as <tool>_<action>: ssh + read is ssh_read, the older name still in older chats
                var name = action is not null ? call.Name + "_" + action : call.Name;
                switch (name)
                {
                    case "read": read.Add(path); break;
                    case "write" or "edit": modified.Add(path); break;
                    case "ssh_read": if (host is not null) read.Add($"{host}:{path}"); break;
                    case "ssh_write" or "ssh_edit": if (host is not null) modified.Add($"{host}:{path}"); break;
                }
            }
        }
        read.ExceptWith(modified);
        return ([.. read], [.. modified]);
    }

    public static string Format(IReadOnlyList<string> read, IReadOnlyList<string> modified)
    {
        var sb = new StringBuilder();
        if (read.Count > 0) sb.Append("\n\n<read-files>\n").Append(string.Join('\n', read)).Append("\n</read-files>");
        if (modified.Count > 0) sb.Append("\n\n<modified-files>\n").Append(string.Join('\n', modified)).Append("\n</modified-files>");
        return sb.ToString();
    }

    /// <summary>The summary without its file lists (they are added again from the meta, never by the model).</summary>
    public static string Strip(string summary) => Blocks.Replace(summary, "").Trim();

    private static (string? Path, string? Host, string? Action) PathOf(string arguments)
    {
        try
        {
            if (JsonNode.Parse(arguments) is not JsonObject o) return (null, null, null);
            string? path = null;
            foreach (var name in PathArgs)
                if (Str(o[name]) is { Length: > 0 } p) { path = p.Trim(); break; }
            return (path, Str(o["host"])?.Trim(), Str(o["action"])?.Trim().ToLowerInvariant());
        }
        catch (JsonException) { return (null, null, null); }
    }

    /// <summary>The paths of an edit of several files (<c>files: [{ path, edits }]</c>); empty for the one-file form.</summary>
    private static List<string> EditFiles(string arguments)
    {
        var paths = new List<string>();
        try
        {
            if (JsonNode.Parse(arguments) is not JsonObject o || (o["files"] ?? o["fileEdits"]) is not JsonArray files) return paths;
            foreach (var f in files)
            {
                if (f is not JsonObject entry) continue;
                foreach (var name in PathArgs)
                    if (Str(entry[name]) is { Length: > 0 } p) { paths.Add(p.Trim()); break; }
            }
        }
        catch (JsonException) { }
        return paths;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
