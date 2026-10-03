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
        context.Services.Register(new SettingsSection
        {
            Id = "agentsMd", Title = "AGENTS.md", Group = "Context", Order = 20,
            Settings =
            [
                SettingInfo.List("agentsMd.fileNames", "Instruction file names", ["AGENTS.md", "CLAUDE.md"], "The first match per folder, from the root down to the working folder."),
                SettingInfo.List("agentsMd.extraFiles", "Always include", [], "Paths of more instruction files."),
                new SettingInfo { Key = "agentsMd.guidance", Type = "text", Label = "How agents use AGENTS.md", Default = System.Text.Json.Nodes.JsonValue.Create(InstructionsSection.Default), Help = "The \"# Instruction files\" section of the system prompt; empty leaves it out.", Applies = "new sessions" },
            ],
        });
        var loader = new AgentsMdLoader(context);
        var notices = new InstructionNotices(context, loader);
        context.Services.Register<IAgentHook>(notices);
        context.Services.Register<IPromptSection>(new InstructionsSection(context.Settings));
        context.Events.Subscribe(EventTypes.SessionProject, e =>
        {
            if (e.As<JsonObject>()?["sessionId"]?.GetValue<string>() is { Length: > 0 } id) notices.OnProjectChanged(id);
        });
        context.Rpc.Register("agentsmd.list", (req, _) =>
        {
            string cwd;
            var bound = false;
            if (req.Str("projectId") is { Length: > 0 } projectId)
                cwd = (context.Sessions.GetProject(projectId) ?? throw new RpcException("not_found", $"No project {projectId}")).Path;
            else
            {
                var sessionId = req.Required("sessionId");
                var session = context.Sessions.Require(sessionId);
                cwd = AgentsMdLoader.WorkspaceRoot(context, session);
                bound = context.Services.Get<IWorkspaceResolver>()?.ResolveLenient(session) is { Isolated: true };
            }
            var arr = new JsonArray();
            foreach (var f in loader.Discover(cwd, stopAtRepoRoot: bound))
                arr.Add(new JsonObject { ["path"] = f.Path, ["bytes"] = f.Bytes, ["scope"] = f.Scope });
            return Task.FromResult<object?>(arr);
        }, "Instruction files that apply to a session or a project folder: { sessionId } | { projectId } → [{ path, bytes, scope }]");
        return Task.CompletedTask;
    }
}

/// <summary>
/// How to use and keep instruction files (prompt section, order 450): they arrive as notices, stay lean with pointers to
/// deeper docs, and are where durable learnings go. <c>agentsMd.guidance</c> replaces the text; an empty string drops it.
/// </summary>
internal sealed class InstructionsSection(ISettings settings) : IPromptSection
{
    public const string Default =
        "AGENTS.md and CLAUDE.md files reach you as notices. They are the lean entry point for agents: the essentials, plus " +
        "pointers to deeper docs. When your task touches something they point to, read that doc first.\n" +
        "Keep them lean. When you learn something the next agent would otherwise have to rediscover (a non-obvious command, " +
        "a pitfall, a convention), add one line to the most specific AGENTS.md, or put the details in the doc it points to " +
        "and add a pointer there. Correct outdated lines instead of adding new ones next to them, and leave out what the code " +
        "or git history already shows. Ask before creating an AGENTS.md where there is none.";

    public string Id => "agentsmd";
    public int Order => 450;

    public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct)
    {
        var text = settings.Get<string>("agentsMd.guidance") ?? Default;
        return ValueTask.FromResult<string?>(string.IsNullOrWhiteSpace(text) ? null : "# Instruction files\n" + text.Trim());
    }
}

internal sealed record InstructionFile(string Path, string Scope, long Bytes);

internal sealed class AgentsMdLoader(IPluginContext ctx)
{
    /// <summary>A session's working directory, resolved through the workspace service when one is loaded.</summary>
    internal static string WorkspaceRoot(IPluginContext ctx, SessionInfo session) => InstructionNotices.CwdOf(ctx, session);


    public const int MaxBytes = 32 * 1024;
    public static readonly string[] DefaultFileNames = ["AGENTS.md", "CLAUDE.md"];

    private sealed record CacheEntry(DateTime MtimeUtc, long Length, string Content, string Hash);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(PathComparer);

    internal static StringComparer PathComparer => WorkspacePaths.Comparer;

    /// <summary>Instruction files in prompt order: global, root → cwd, extra.</summary>
    public List<InstructionFile> Discover(string cwd) => Discover(cwd, stopAtRepoRoot: false);

    /// <summary>
    /// Instruction files in prompt order: global, root → cwd, extra.
    /// <para>
    /// <paramref name="stopAtRepoRoot"/> stops the walk at the working directory's own repository boundary. Without it a
    /// worktree nested inside the main checkout (or a checkout of a repository that has one) also picks up the main
    /// checkout's <c>AGENTS.md</c>, because that file is an ancestor directory of it — and its rules are about a tree the
    /// worker is not working in. Instructions above the checkout (the workspace's own <c>..</c> chain, the user's global
    /// file) still apply.
    /// </para>
    /// </summary>
    public List<InstructionFile> Discover(string cwd, bool stopAtRepoRoot)
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

        Add(Path.Combine(ctx.Paths.Home, "AGENTS.md"), "global");

        var names = ctx.Settings.GetStrings("agentsMd.fileNames", DefaultFileNames);
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
                // The working directory's own repository is the boundary: its .git here means everything above belongs to
                // a checkout this session is not in.
                if (stopAtRepoRoot && IsRepoRoot(dir.FullName)) break;
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "AGENTS.md discovery failed for {Cwd}", cwd);
        }
        chain.Reverse(); // root → leaf
        foreach (var p in chain) Add(p, "project");

        foreach (var extra in ctx.Settings.GetStrings("agentsMd.extraFiles", []))
        {
            var path = extra.StartsWith("~/", StringComparison.Ordinal) || extra.StartsWith("~\\", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), extra[2..])
                : Path.IsPathRooted(extra) ? extra : Path.Combine(cwd, extra);
            Add(path, "extra");
        }
        return result;
    }

    /// <summary>Whether a directory is the root of a git checkout: it holds a <c>.git</c> directory or file (a worktree's is a file).</summary>
    internal static bool IsRepoRoot(string dir)
    {
        try { return Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>File content capped at <see cref="MaxBytes"/> (with a note), cached by path + mtime + size.</summary>
    public string? Read(string path) => ReadHashed(path).Content;

    /// <summary>
    /// The file's content and the hash of it, both from the same cache entry. The hash is what the hook compares on
    /// every model call, so it is kept next to the content instead of being recomputed from it each time: the file
    /// does not change, but the turn still has to prove it, and SHA-256 over every instruction file is not free.
    /// </summary>
    public (string? Content, string? Hash) ReadHashed(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return (null, null);
            if (_cache.TryGetValue(path, out var hit) && hit.MtimeUtc == fi.LastWriteTimeUtc && hit.Length == fi.Length)
                return (hit.Content, hit.Hash);

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
            var hash = InstructionNotices.Hash(content.Trim());
            _cache[path] = new CacheEntry(fi.LastWriteTimeUtc, fi.Length, content, hash);
            return (content, hash);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Cannot read {Path}", path);
            return (null, null);
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
    /// <summary>A session's working directory: its workspace root when it is bound to one, else its project path.</summary>
    internal static string CwdOf(IPluginContext ctx, SessionInfo session)
    {
        var resolver = ctx.Services.Get<IWorkspaceResolver>();
        return SessionWorkspace.Of(session) is not null && resolver is not null ? resolver.CwdOf(session) : ctx.Sessions.GetCwd(session);
    }


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
        // A session in a workspace of its own stops the instruction walk at that checkout's root: the main checkout's
        // AGENTS.md is an ancestor file, not an instruction about this tree.
        var ownCheckout = turn.Run.Workspace() is { Isolated: true };
        if (!Pending(turn.Messages, turn.Run.Cwd, ownCheckout).Any) return;
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
            var binding = ctx.Services.Get<IWorkspaceResolver>()?.ResolveLenient(session);
            var delta = Pending(ctx.Sessions.GetContextMessages(sessionId), CwdOf(ctx, session), binding is { Isolated: true });
            if (!delta.Any) return false;
            var notice = ChatMessage.NoticeText(Text(delta), Kind);
            notice.Meta!["files"] = new JsonArray([.. delta.Changed.Select(c =>
                (JsonNode)new JsonObject { ["path"] = c.File.Path, ["hash"] = c.Hash, ["scope"] = c.File.Scope })]);
            if (delta.Removed.Count > 0) notice.Meta["removed"] = new JsonArray([.. delta.Removed.Select(p => (JsonNode)JsonValue.Create(p))]);
            notice.Meta["setup"] = true;  // the chat's setting: at the first turn the model reads it before the first message
            ctx.Sessions.AppendMessage(sessionId, notice);
            return true;
        }
    }

    private Delta Pending(IReadOnlyList<ChatMessage> context, string cwd, bool ownCheckout)
    {
        var known = Known(context);
        var current = new List<Current>();
        foreach (var f in loader.Discover(cwd, stopAtRepoRoot: ownCheckout))
        {
            var (content, hash) = loader.ReadHashed(f.Path);
            content = content?.Trim();
            if (string.IsNullOrEmpty(content) || hash is null) continue;
            current.Add(new Current(f, content, hash));
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

    internal static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();
}
