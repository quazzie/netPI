using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.AgentsMd;

/// <summary>
/// Instruction files for the model, delivered as "instructions" notices (never in the system prompt, which is frozen per
/// session): the global <c>~/.netpi/AGENTS.md</c>, then the first existing file of <c>agentsMd.fileNames</c> (default
/// <c>["AGENTS.md", "CLAUDE.md"]</c>) in every directory from the filesystem root down to the session's working
/// directory, then <c>agentsMd.extraFiles</c>. Each file is capped at 32KB.
/// <para>RPC: <c>agentsmd.list { sessionId } | { projectId }</c> → <c>[{ path, bytes, scope }]</c> (scope: global | project | extra).</para>
/// </summary>
[NetPiPlugin("netpi.agentsmd", Name = "AGENTS.md", Description = "Global and project AGENTS.md / CLAUDE.md instructions, announced as notices when they apply or change", Order = 41)]
public sealed class AgentsMdPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var loader = new AgentsMdLoader(context);
        var notices = new InstructionNotices(context, loader);
        context.Services.Register<IAgentHook>(notices);
        context.Events.Subscribe(EventTypes.SessionProject, e =>
        {
            if (e.As<JsonObject>()?["sessionId"]?.GetValue<string>() is { Length: > 0 } id) notices.OnProjectChanged(id);
        });
        context.Rpc.Register("agentsmd.list", (req, _) =>
        {
            string cwd;
            if (req.Str("projectId") is { Length: > 0 } projectId)
                cwd = (context.Sessions.GetProject(projectId) ?? throw new RpcException("not_found", $"No project {projectId}")).Path;
            else
            {
                var sessionId = req.Required("sessionId");
                var session = context.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"No session {sessionId}");
                cwd = context.Sessions.GetCwd(session);
            }
            var arr = new JsonArray();
            foreach (var f in loader.Discover(cwd))
                arr.Add(new JsonObject { ["path"] = f.Path, ["bytes"] = f.Bytes, ["scope"] = f.Scope });
            return Task.FromResult<object?>(arr);
        }, "Instruction files that apply to a session or a project folder: { sessionId } | { projectId } → [{ path, bytes, scope }]");
        return Task.CompletedTask;
    }
}

internal sealed record InstructionFile(string Path, string Scope, long Bytes);

internal sealed class AgentsMdLoader(IPluginContext ctx)
{
    public const int MaxBytes = 32 * 1024;
    public static readonly string[] DefaultFileNames = ["AGENTS.md", "CLAUDE.md"];

    private sealed record CacheEntry(DateTime MtimeUtc, long Length, string Content);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(PathComparer);

    internal static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private List<string> Setting(string path, IReadOnlyList<string> fallback)
    {
        try
        {
            var node = ctx.Settings.GetNode(path);
            if (node is JsonArray a)
                return a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
            if (node is JsonValue jv && jv.TryGetValue<string>(out var one) && !string.IsNullOrWhiteSpace(one)) return [one.Trim()];
        }
        catch { }
        return [.. fallback];
    }

    /// <summary>Instruction files in prompt order: global, root → cwd, extra.</summary>
    public List<InstructionFile> Discover(string cwd)
    {
        var result = new List<InstructionFile>();
        var seen = new HashSet<string>(PathComparer);

        void Add(string path, string scope)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var fi = new FileInfo(full);
                if (!fi.Exists || !seen.Add(full)) return;
                result.Add(new InstructionFile(full, scope, fi.Length));
            }
            catch { /* unreadable path */ }
        }

        Add(ctx.Paths.GlobalAgentsMd, "global");

        var names = Setting("agentsMd.fileNames", DefaultFileNames);
        var chain = new List<string>();
        try
        {
            for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
            {
                foreach (var name in names)
                {
                    var candidate = Path.Combine(dir.FullName, name);
                    if (File.Exists(candidate)) { chain.Add(candidate); break; }
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "AGENTS.md discovery failed for {Cwd}", cwd);
        }
        chain.Reverse(); // root → leaf
        foreach (var p in chain) Add(p, "project");

        foreach (var extra in Setting("agentsMd.extraFiles", []))
        {
            var path = extra.StartsWith("~/", StringComparison.Ordinal) || extra.StartsWith("~\\", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), extra[2..])
                : Path.IsPathRooted(extra) ? extra : Path.Combine(cwd, extra);
            Add(path, "extra");
        }
        return result;
    }

    /// <summary>File content capped at <see cref="MaxBytes"/> (with a note), cached by path + mtime + size.</summary>
    public string? Read(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            if (_cache.TryGetValue(path, out var hit) && hit.MtimeUtc == fi.LastWriteTimeUtc && hit.Length == fi.Length) return hit.Content;

            string content;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var len = (int)Math.Min(fs.Length, MaxBytes);
                var buf = new byte[len];
                var read = 0;
                while (read < len)
                {
                    var n = fs.Read(buf, read, len - read);
                    if (n == 0) break;
                    read += n;
                }
                content = Encoding.UTF8.GetString(buf, 0, read);
                if (content.Length > 0 && content[0] == '﻿') content = content[1..];
                content = content.Replace("\r\n", "\n");
                if (fi.Length > MaxBytes)
                {
                    content = content.TrimEnd('�');
                    content += $"\n\n[Truncated: this file is {fi.Length / 1024} KB; only the first {MaxBytes / 1024} KB are included. Read the file for the rest.]";
                }
            }
            _cache[path] = new CacheEntry(fi.LastWriteTimeUtc, fi.Length, content);
            return content;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Cannot read {Path}", path);
            return null;
        }
    }
}

/// <summary>
/// Announces the instruction files in "instructions" notices, which are only ever appended (the conversation's cached
/// prefix survives): all files when a session first calls the model, afterwards only what changed — an edited or new
/// file with its content, a file that no longer applies by name (e.g. after a project switch, event
/// <c>session.project</c>). What the model has is read from the notices still in the context, so a file whose notice was
/// compacted away is announced again. Notice meta: <c>files: [{ path, hash, scope }]</c> (content included) and
/// <c>removed: [path]</c>.
/// </summary>
internal sealed class InstructionNotices(IPluginContext ctx, AgentsMdLoader loader) : IAgentHook
{
    public const string Kind = "instructions";

    private sealed record Current(InstructionFile File, string Content, string Hash);
    private sealed record Delta(List<Current> Changed, List<string> Removed, bool First)
    {
        public bool Any => Changed.Count > 0 || Removed.Count > 0;
    }

    private readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.Ordinal);

    /// <summary>After compaction (-100) and the working-directory notice (500).</summary>
    public int Order => 510;

    public async ValueTask OnBeforeModelCallAsync(AgentTurnContext turn)
    {
        if (!Pending(turn.Messages, turn.Run.Cwd).Any) return;
        if (Announce(turn.Run.Session.Id)) await turn.ReloadMessagesAsync().ConfigureAwait(false);
    }

    /// <summary>A project switch: announce the new project's instructions right away.</summary>
    public void OnProjectChanged(string sessionId)
    {
        try { Announce(sessionId); }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Instructions notice for {Session} failed", sessionId); }
    }

    /// <summary>Re-reads the session under a per-session lock (the event and the hook can race) and appends a notice if needed.</summary>
    internal bool Announce(string sessionId)
    {
        lock (_gates.GetOrAdd(sessionId, _ => new object()))
        {
            var session = ctx.Sessions.GetSession(sessionId);
            if (session is null) return false;
            var delta = Pending(ctx.Sessions.GetContextMessages(sessionId), ctx.Sessions.GetCwd(session));
            if (!delta.Any) return false;
            var notice = ChatMessage.NoticeText(Text(delta), Kind);
            notice.Meta!["files"] = new JsonArray([.. delta.Changed.Select(c =>
                (JsonNode)new JsonObject { ["path"] = c.File.Path, ["hash"] = c.Hash, ["scope"] = c.File.Scope })]);
            if (delta.Removed.Count > 0) notice.Meta["removed"] = new JsonArray([.. delta.Removed.Select(p => (JsonNode)JsonValue.Create(p))]);
            ctx.Sessions.AppendMessage(sessionId, notice);
            return true;
        }
    }

    private Delta Pending(IReadOnlyList<ChatMessage> context, string cwd)
    {
        var known = Known(context);
        var current = new List<Current>();
        foreach (var f in loader.Discover(cwd))
        {
            var content = loader.Read(f.Path)?.Trim();
            if (string.IsNullOrEmpty(content)) continue;
            current.Add(new Current(f, content, Hash(content)));
        }
        var changed = current.Where(c => !known.TryGetValue(c.File.Path, out var h) || h != c.Hash).ToList();
        var removed = known.Keys.Where(k => !current.Any(c => AgentsMdLoader.PathComparer.Equals(c.File.Path, k))).ToList();
        return new Delta(changed, removed, known.Count == 0);
    }

    /// <summary>Path → hash of what the model has: the instruction notices still in the context, in order.</summary>
    private static Dictionary<string, string> Known(IReadOnlyList<ChatMessage> context)
    {
        var known = new Dictionary<string, string>(AgentsMdLoader.PathComparer);
        foreach (var m in context)
        {
            if (m.Role != MessageRole.Notice || m.MetaString("kind") != Kind) continue;
            if (m.Meta?["files"] is JsonArray files)
                foreach (var f in files.OfType<JsonObject>())
                    if (f["path"]?.GetValue<string>() is { } path && f["hash"]?.GetValue<string>() is { } hash) known[path] = hash;
            if (m.Meta?["removed"] is JsonArray removed)
                foreach (var r in removed)
                    if (r?.GetValue<string>() is { } path) known.Remove(path);
        }
        return known;
    }

    private static string Text(Delta d)
    {
        var sb = new StringBuilder(d.First
            ? "Instruction files that apply here: global first, then from the filesystem root down to the working directory. Follow them; where they disagree, the more specific (deeper) file wins.\n"
            : "The instruction files changed. Follow the current versions below.\n");
        foreach (var c in d.Changed)
        {
            sb.Append("\n## ").Append(c.File.Path);
            if (c.File.Scope == "global") sb.Append(" (global)");
            sb.Append("\n\n").Append(c.Content).Append('\n');
        }
        if (d.Removed.Count > 0) sb.Append("\nNo longer apply: ").Append(string.Join(", ", d.Removed)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();
}
