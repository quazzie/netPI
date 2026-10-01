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

        if (MutatingTools.Contains(name))
        {
            var target = PathArg(args);
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
            var cwd = Str(args, "cwd", "workdir", "working_directory", "workingDirectory", "directory", "dir");
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
    internal static string? PathArg(JsonObject args)
    {
        foreach (var key in PathArgs)
            if (Str(args, key) is { Length: > 0 } v) return v;
        return null;
    }

    internal static string? Str(JsonObject args, params string[] names)
    {
        foreach (var name in names)
            if (args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        return null;
    }

    internal static JsonObject? Arguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
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