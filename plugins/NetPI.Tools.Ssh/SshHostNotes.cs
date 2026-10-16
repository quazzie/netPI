using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Ssh;

/// <summary>
/// Notes the user keeps about a host (what runs there, what not to touch): setting <c>ssh.hostNotes</c>,
/// <c>{ "folders": ["&lt;dir&gt;"], "files": { "&lt;alias&gt;": "&lt;file&gt;" } }</c>. A chat's first ssh call to a host (any action) gets the
/// note as a section of its result; it is not repeated while that result is still in the chat's context, and comes back after
/// compaction dropped it. The file is <c>files[alias]</c>, else <c>&lt;folder&gt;/&lt;alias&gt;.md</c> in the first folder that has one.
/// </summary>
internal sealed class SshHostNotes(IPluginContext ctx)
{
    internal const int MaxBytes = 12 * 1024;
    /// <summary>Calls of one turn run together: a second call starting this soon after the first took the note is that turn's, whose result is not in the context yet.</summary>
    internal static TimeSpan SameTurn { get; set; } = TimeSpan.FromSeconds(10);
    private static readonly Regex SafeName = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, DateTime> _taken = new(StringComparer.OrdinalIgnoreCase);

    internal static string Marker(string alias) => $"[Host notes for {alias}:";

    /// <summary>The note's file for a host, or null.</summary>
    internal string? FileFor(string alias)
    {
        if (ctx.Settings.GetNode("ssh.hostNotes") is not JsonObject cfg) return null;
        if (cfg["files"] is JsonObject files)
            foreach (var (name, value) in files)
                if (name.Equals(alias, StringComparison.OrdinalIgnoreCase) && value is JsonValue v && v.TryGetValue<string>(out var file) && !string.IsNullOrWhiteSpace(file))
                {
                    var full = Expand(file);
                    if (File.Exists(full)) return full;
                }
        if (cfg["folders"] is JsonArray folders && SafeName.IsMatch(alias))
            foreach (var f in folders)
                if (f is JsonValue v && v.TryGetValue<string>(out var folder) && !string.IsNullOrWhiteSpace(folder))
                {
                    var candidate = Path.Combine(Expand(folder), alias + ".md");
                    if (File.Exists(candidate)) return candidate;
                }
        return null;
    }

    private static string Expand(string path)
    {
        path = path.Trim();
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        return Path.GetFullPath(path);
    }

    /// <summary>The section to add to this call's result, or null when the host has no note or the chat has it already.</summary>
    internal string? Take(string sessionId, string alias)
    {
        if (FileFor(alias) is not { } file) return null;
        var key = sessionId + "|" + alias;
        try
        {
            var marker = Marker(alias);
            var held = ctx.Sessions?.GetContextMessages(sessionId).Any(m => m.ToolResults.Any(r => r.Content.Contains(marker, StringComparison.Ordinal))) ?? false;
            if (held) return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { /* an unknown session has no context: treat as none */ }
        // Check and take in one step: concurrent calls of a turn must not both attach it
        var now = DateTime.UtcNow;
        var first = true;
        _taken.AddOrUpdate(key, now, (_, previous) => { first = now - previous >= SameTurn; return first ? now : previous; });
        return first ? Render(alias, file) : null;
    }

    internal static string Render(string alias, string file)
    {
        string text;
        long length;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = fs.Length;
            var buffer = new byte[(int)Math.Min(length, MaxBytes)];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            text = Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\uFFFD').Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"{Marker(alias)} {file}]\n(the note could not be read: {ex.Message})"; }
        var sb = new StringBuilder($"{Marker(alias)} {file}]\n{text}");
        if (length > MaxBytes) sb.Append($"\n[Truncated: the note is {length / 1024} KB; only the first {MaxBytes / 1024} KB are shown. Full file: {file}]");
        return sb.ToString();
    }
}
