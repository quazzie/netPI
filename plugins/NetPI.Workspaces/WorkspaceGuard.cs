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
    /// <summary>Tools that write files. A tool not on this list is never blocked: reading another checkout is fine.</summary>
    internal static readonly HashSet<string> MutatingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write", "edit", "write_file", "edit_file", "notebook_edit", "patch",
    };

    /// <summary>Shell tools: their cwd is checked, but the command is not parsed (nothing here is an OS sandbox).</summary>
    internal static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase) { "bash", "pwsh", "shell" };

    /// <summary>Before the guardrails' own path rules, so a refusal names the workspace rather than a bare path.</summary>
    public int Order => 100;

    public async ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call)
    {
        // The arguments as the tools read them: a string-encoded root (even a double-encoded one) unwraps to the
        // object the tool will run with, so the guard judges the call that runs - a call it cannot read is a call it
        // would let through, which is how the bypass happened. Which argument is the path is the shared vocabulary
        // (ToolPathArgs): the tools read the same names in the same order.
        var args = ToolArgs.Parse(call.Arguments);
        var name = call.Name;
        var ct = turn.Run.CancellationToken;

        // A write tool's file, or the destination an ssh download writes on this machine (scp writes it where the call says).
        var mutation = MutatingTools.Contains(name);
        var download = mutation ? null : ToolPathArgs.SshDownloadTarget(name, args);
        // What the guard has an opinion about at all: a write, a download that lands on this machine, or a shell
        // (a shell command can write anything). Everything else it never judges, so it never blocks.
        var judged = mutation || download is not null || ShellTools.Contains(name);
        try
        {
            var run = turn.Run;
            var binding = run.Workspace();
            if (binding is null || !binding.Isolated) return null;
            if (mutation || download is not null)
            {
                // Every file the call writes: an edit of several files is all or nothing, so one file in another
                // checkout refuses the whole call.
                foreach (var target in mutation ? ToolPathArgs.WriteTargets(name, args) : [download!])
                {
                    var full = turn.Resolve(call, target);
                    var verdict = await WorkspacePaths.CheckMutationAsync(binding, full, git, ct).ConfigureAwait(false);
                    if (verdict is WorkspacePathVerdict.ForeignCheckout or WorkspacePathVerdict.Unverifiable)
                        return new ToolCallDecision
                        {
                            Block = true,
                            Reason = await WorkspacePaths.RefusalAsync(binding, full, git, verdict, ct).ConfigureAwait(false),
                        };
                }
                return null;
            }

            if (ShellTools.Contains(name))
            {
                var cwd = ToolPathArgs.CwdOf(args);
                if (cwd is null) return null;   // the default cwd is the workspace
                var full = turn.Resolve(call, cwd);
                var verdict = await WorkspacePaths.CheckMutationAsync(binding, full, git, ct).ConfigureAwait(false);
                if (verdict is not (WorkspacePathVerdict.ForeignCheckout or WorkspacePathVerdict.Unverifiable)) return null;
                // A shell command may write anything, so this is a judgement call: refuse the obvious accident (running in
                // another worker's checkout) and say plainly that this is not a sandbox.
                return new ToolCallDecision
                {
                    Block = true,
                    Reason = await WorkspacePaths.RefusalAsync(binding, full, git, verdict, ct).ConfigureAwait(false) +
                        " (This guards where a command runs, not what it does: a shell command that names another path is not inspected.)",
                };
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Failing open would make the guard a way in: the hook pass swallows a throwing hook and judges nothing,
            // so the call the guard could not check is the call that runs. A call that cannot change anything was
            // never the guard's business, and a failure there blocks nothing.
            ctx.Logger.LogWarning(ex, "Workspace guard could not check {Tool}", name);
            return judged
                ? new ToolCallDecision { Block = true, Reason = $"the workspace guard could not check this call ({ex.Message}), so it did not run." }
                : null;
        }
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