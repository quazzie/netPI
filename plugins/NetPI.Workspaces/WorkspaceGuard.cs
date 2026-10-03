using System.Text.Json;
using Microsoft.Extensions.Logging;

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
/// accident: another worker's checkout, or the primary one, in an isolated workspace — and, while git cannot answer,
/// anything it cannot verify: an unverifiable path is not a pass.
/// </para>
/// <para>
/// It fails closed as well: a call it could not judge at all (git threw, a path it cannot resolve) does not run, because
/// a hook that throws is a hook that did not judge. Only for a call that can change something — see <see cref="Judges"/>.
/// </para>
/// </summary>
internal sealed class WorkspaceGuard(IPluginContext ctx, IWorkspaceRepoProbe git) : IAgentHook
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
        // The arguments as the tools read them: a string-encoded root (even a double-encoded one) unwraps to the
        // object the tool will run with, so the guard judges the call that runs - a call it cannot read is a call it
        // would let through, which is how the bypass happened.
        var args = ToolArgs.Parse(call.Arguments);
        var name = call.Name;

        // A write tool's file, or the destination an ssh download writes on this machine (scp writes it where the call says).
        var mutation = MutatingTools.Contains(name);
        var download = mutation ? null : SshDownloadTarget(name, args);
        // What the guard has an opinion about at all: a write, a download that lands on this machine, or a shell
        // (a shell command can write anything). Everything else it never judges, so it never blocks.
        var judged = mutation || download is not null || ShellTools.Contains(name);
        try
        {
            var run = turn.Run;
            var binding = run.Workspace();
            if (binding is null || !binding.Isolated) return ValueTask.FromResult<ToolCallDecision?>(null);
            if (mutation || download is not null)
            {
                var target = mutation ? PathArg(args) : download;
                if (target is not null)
                {
                    var full = turn.Resolve(call, target);
                    var verdict = WorkspacePaths.CheckMutation(binding, full, git);
                    if (verdict is WorkspacePathVerdict.ForeignCheckout or WorkspacePathVerdict.Unverifiable)
                        return ValueTask.FromResult<ToolCallDecision?>(new ToolCallDecision
                        {
                            Block = true,
                            Reason = WorkspacePaths.Refusal(binding, full, git, verdict),
                        });
                }
                return ValueTask.FromResult<ToolCallDecision?>(null);
            }

            if (ShellTools.Contains(name))
            {
                var cwd = args.Str(CwdArgs)?.Trim();
                if (cwd is null) return ValueTask.FromResult<ToolCallDecision?>(null);   // the default cwd is the workspace
                var full = turn.Resolve(call, cwd);
                var verdict = WorkspacePaths.CheckMutation(binding, full, git);
                if (verdict is not (WorkspacePathVerdict.ForeignCheckout or WorkspacePathVerdict.Unverifiable)) return ValueTask.FromResult<ToolCallDecision?>(null);
                // A shell command may write anything, so this is a judgement call: refuse the obvious accident (running in
                // another worker's checkout) and say plainly that this is not a sandbox.
                return ValueTask.FromResult<ToolCallDecision?>(new ToolCallDecision
                {
                    Block = true,
                    Reason = WorkspacePaths.Refusal(binding, full, git, verdict) +
                        " (This guards where a command runs, not what it does: a shell command that names another path is not inspected.)",
                });
            }

            return ValueTask.FromResult<ToolCallDecision?>(null);
        }
        catch (Exception ex)
        {
            // Failing open would make the guard a way in: the hook pass swallows a throwing hook and judges nothing,
            // so the call the guard could not check is the call that runs. A call that cannot change anything was
            // never the guard's business, and a failure there blocks nothing.
            ctx.Logger.LogWarning(ex, "Workspace guard could not check {Tool}", name);
            return ValueTask.FromResult<ToolCallDecision?>(judged
                ? new ToolCallDecision { Block = true, Reason = $"the workspace guard could not check this call ({ex.Message}), so it did not run." }
                : null);
        }
    }

    /// <summary>The tool's own path argument, by its own names first and then the shared ones.</summary>
    internal static string? PathArg(ToolArgs args) => args.Str(PathArgs)?.Trim() is { Length: > 0 } s ? s : null;

    /// <summary>
    /// The local path an ssh download writes, routed as the ssh tool routes it: the action is <c>copy</c> (or scp, upload,
    /// download) and the direction is what the tool resolves: the action itself when it says upload/download and there is no
    /// <c>direction</c> argument (the dispatcher fills it in), otherwise <c>direction</c>, then <c>mode</c>.
    /// Null for any other call. (The guardrails plugin keeps its own copy of this rule for its protected paths: plugins do
    /// not share code.)
    /// </summary>
    internal static string? SshDownloadTarget(string tool, ToolArgs args)
    {
        if (!tool.Equals("ssh", StringComparison.OrdinalIgnoreCase)) return null;
        var action = args.Str("action", "verb", "command")?.Trim().ToLowerInvariant();
        if (action is null && args.Has("script")) action = "run";
        if (action is not ("copy" or "scp" or "upload" or "download")) return null;
        var spoken = args.Str("action")?.Trim().ToLowerInvariant();
        var direction = (args.Str("direction") is null && spoken is "upload" or "download" ? spoken : args.Str("direction", "mode"))?.Trim().ToLowerInvariant();
        return direction == "download" ? args.Str("to", "destination", "dest", "target")?.Trim() : null;
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
        var binding = turn.Run.Workspace();
        var cwd = binding?.Root ?? turn.Run.Cwd;
        var context = new ToolContext
        {
            SessionId = turn.Run.Session.Id, AgentId = turn.Run.Agent.Id, CallId = call.Id,
            Cwd = cwd, Project = turn.Run.Project,
            Services = turn.Run.Services, Events = turn.Run.Events,
        };
        context.Features.Set(binding);
        return context.ResolvePath(path);
    }
}