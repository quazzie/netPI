using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Workspaces;

/// <summary>
/// The ownership guard: one place that decides whether a tool call may write where it is aiming, so the file tools, the
/// shell and any future native tool cannot each form their own opinion.
/// <para>
/// It works from git evidence, not from spelling: a path is "another checkout" when it is outside the session's
/// workspace <em>and</em> its <c>--git-common-dir</c> is the same repository's. A stale absolute path into the primary
/// checkout, a relative <c>../</c> that climbs out, a differently cased spelling and a junction that points there all
/// reach the same answer, because all of them resolve first.
/// </para>
/// <para>
/// Two things it is not: it does not parse shell commands (a shell can write anywhere the user can), and it does not
/// refuse merely being outside the workspace — writing a log to the temp folder is ordinary work. It refuses the specific
/// accident: another worker's checkout, or the primary one, in an isolated workspace.
/// </para>
/// </summary>
internal sealed class WorkspaceGuard(GitProbe git) : IAgentHook
{
    /// <summary>The path-like argument each mutating tool uses (the file tools' own list, then the usual names).</summary>
    internal static readonly string[] PathArgs = ["path", "file_path", "filePath", "file", "filename", "fileName", "target"];

    /// <summary>Tools that write files. A tool not on this list is never blocked: reading another checkout is fine.</summary>
    internal static readonly HashSet<string> MutatingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write", "edit", "write_file", "edit_file", "notebook_edit", "patch",
    };

    /// <summary>Shell tools: their cwd is checked, but the command is not parsed (nothing here is an OS sandbox).</summary>
    internal static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase) { "bash", "pwsh", "shell" };

    /// <summary>The names the shell tools read their working directory by (Tools.Shell ShellService), in the order it tries them.</summary>
    internal static readonly string[] CwdArgs = ["cwd", "workdir", "working_directory", "workingDirectory", "directory", "dir"];

    /// <summary>Before the guardrails' own path rules, so a refusal names the workspace rather than a bare path.</summary>
    public int Order => 100;

    public ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call)
    {
        var run = turn.Run;
        var binding = run.Workspace;
        if (binding is null || !binding.Isolated) return ValueTask.FromResult<ToolCallDecision?>(null);
        var args = Arguments(call.Arguments);
        if (args is null) return ValueTask.FromResult<ToolCallDecision?>(null);
        var name = call.Name;

        // A write tool's file, or the destination an ssh download writes on this machine (scp writes it where the call says).
        var mutation = MutatingTools.Contains(name);
        var download = mutation ? null : SshDownloadTarget(name, args);
        if (mutation || download is not null)
        {
            var target = mutation ? PathArg(args) : download;
            if (target is not null)
            {
                var full = turn.Resolve(call, target);
                var verdict = WorkspacePaths.CheckMutation(binding, full, git);
                if (verdict == WorkspacePathVerdict.ForeignCheckout)
                    return ValueTask.FromResult<ToolCallDecision?>(new ToolCallDecision
                    {
                        Block = true,
                        Reason = WorkspacePaths.Refusal(binding, full, git),
                    });
            }
            return ValueTask.FromResult<ToolCallDecision?>(null);
        }

        if (ShellTools.Contains(name))
        {
            var cwd = Str(args, CwdArgs);
            if (cwd is null) return ValueTask.FromResult<ToolCallDecision?>(null);   // the default cwd is the workspace
            var full = turn.Resolve(call, cwd);
            var verdict = WorkspacePaths.CheckMutation(binding, full, git);
            if (verdict != WorkspacePathVerdict.ForeignCheckout) return ValueTask.FromResult<ToolCallDecision?>(null);
            // A shell command may write anything, so this is a judgement call: refuse the obvious accident (running in
            // another worker's checkout) and say plainly that this is not a sandbox.
            return ValueTask.FromResult<ToolCallDecision?>(new ToolCallDecision
            {
                Block = true,
                Reason = WorkspacePaths.Refusal(binding, full, git) +
                    " (This guards where a command runs, not what it does: a shell command that names another path is not inspected.)",
            });
        }

        return ValueTask.FromResult<ToolCallDecision?>(null);
    }

    /// <summary>The tool's own path argument, by its own names first and then the shared ones.</summary>
    internal static string? PathArg(JsonObject args) => Str(args, PathArgs);

    /// <summary>
    /// An argument as the tools read it: names match ignoring case, <c>_</c>, <c>-</c> and spaces, and the first of
    /// <paramref name="names"/> that is present wins (a blank value is no value, as the tool then refuses it). The guard has
    /// to judge the path the tool will use, so <c>{"Path": ...}</c> and <c>{"file-path": ...}</c> count like <c>path</c>.
    /// </summary>
    internal static string? Str(JsonObject args, params string[] names)
    {
        var props = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in args) props.TryAdd(Key(key), value);
        foreach (var name in names)
            if (props.TryGetValue(Key(name), out var node) && node is not null)
                return Text(node)?.Trim() is { Length: > 0 } s ? s : null;
        return null;
    }

    private static string Key(string name) => name.Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();

    private static string? Text(JsonNode node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join("\n", a.Select(e => e is JsonValue ev && ev.TryGetValue<string>(out var es) ? es : e?.ToJsonString())),
        _ => node.ToJsonString(),
    };

    /// <summary>
    /// The arguments object. Some models send it as a JSON <em>string</em>; the tools unwrap that, so the guard must too, or
    /// a call it cannot read is a call it lets through.
    /// </summary>
    internal static JsonObject? Arguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonValue v && v.TryGetValue<string>(out var inner)) node = JsonNode.Parse(inner);
            return node as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The local path an ssh download writes, routed as the ssh tool routes it: the action is <c>copy</c> (or scp, upload,
    /// download) and the direction is the <c>direction</c> argument, or the action itself when that says upload/download.
    /// Null for any other call. (The guardrails plugin keeps its own copy of this rule for its protected paths: plugins do
    /// not share code.)
    /// </summary>
    internal static string? SshDownloadTarget(string tool, JsonObject args)
    {
        if (!tool.Equals("ssh", StringComparison.OrdinalIgnoreCase)) return null;
        var action = Str(args, "action", "verb", "command")?.ToLowerInvariant();
        if (action is null && Str(args, "script") is not null) action = "run";
        if (action is not ("copy" or "scp" or "upload" or "download")) return null;
        var direction = Str(args, "direction", "mode")?.ToLowerInvariant();
        if (direction is null && Str(args, "action")?.ToLowerInvariant() is "upload" or "download") direction = Str(args, "action")!.ToLowerInvariant();
        return direction == "download" ? Str(args, "to", "destination", "dest", "target") : null;
    }

    /// <summary>Resolve a path argument the way the tool will: relative to the call's own cwd.</summary>
    internal static string Resolve(ToolContext context, string path) => context.ResolvePath(path);
}

/// <summary>Resolving for the guard: the call's cwd and the workspace probe, so it needs no plugin state.
/// </summary>
internal static class WorkspaceGuardExtensions
{
    /// <summary>Resolve a path argument against the turn's tool context (the run's cwd is the workspace root).</summary>
    public static string Resolve(this AgentTurnContext turn, ToolCallPart call, string path)
    {
        var binding = turn.Run.Workspace;
        var cwd = binding?.Root ?? turn.Run.Cwd;
        var context = new ToolContext
        {
            SessionId = turn.Run.Session.Id, AgentId = turn.Run.Agent.Id, CallId = call.Id,
            Cwd = cwd, Project = turn.Run.Project, Workspace = binding,
            Services = turn.Run.Services, Events = turn.Run.Events,
        };
        return context.ResolvePath(path);
    }
}