using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Runtime;

/// <summary>
/// What a spawn decides before the child exists: the model it runs on, the reasoning, its instructions, the tools it may
/// use and the checkout it works in — the session metadata and everything else <see cref="IAgentRuntime.SpawnAsync"/>
/// then creates the child from. It reads the spawn request, the parent (its <see cref="AgentInfo"/> snapshot and its
/// session) and the services, and holds no state of its own, so a plan can be taken (and refused) without a running
/// agent. A refusal here — an unknown agent, a workspace that cannot be provisioned — happens before the child's
/// session exists, so a spawn that cannot work leaves nothing behind.
/// </summary>
internal sealed class SubagentPlanner(IPluginContext ctx)
{
    /// <param name="parent">The parent agent's snapshot (null when there is no parent agent).</param>
    /// <param name="parentSession">The session the parent agent runs in, for what a child inherits from it.</param>
    /// <param name="childId">The id the child is created with (its instructions name it, so it must exist first).</param>
    /// <param name="childName">The name the child is created with.</param>
    public async Task<SpawnPlan> PlanAsync(SpawnRequest request, AgentInfo? parent, SessionInfo? parentSession,
        string childId, string childName, CancellationToken ct)
    {
        // an agent the user set up (by its id): the subagent runs on it
        var anyAgent = string.Equals(request.Agent?.Trim(), SessionAgent.Any, StringComparison.OrdinalIgnoreCase);
        var agent = anyAgent ? null : SetUpAgent(request.Agent ?? request.Model);
        string? modelRef;
        if (anyAgent)
        {
            if (!(ctx.Services.Get<IAgentScheduler>()?.Snapshot().Any(p => p.Configured && p.Available) ?? false))
                throw new InvalidOperationException("No agent can take work now, so \"any\" has nowhere to go. Choose an agent (agent_choices).");
            modelRef = null; // the model follows the agent that takes the run
        }
        else if (agent is not null)
        {
            if (!agent.Available)
                throw new InvalidOperationException(agent.Disabled
                    ? $"The agent \"{agent.Key}\" is switched off by the user. Choose another agent (agent_choices)."
                    : $"The agent \"{agent.Key}\" can't take work now: {agent.Unavailable}. Choose another agent (agent_choices).");
            modelRef = agent.Model;
        }
        else if (!string.IsNullOrWhiteSpace(request.Agent))
            throw new ArgumentException($"Unknown agent '{request.Agent}'. agent_choices lists the agents.");
        else
            modelRef = await ResolveModelAsync(request.Model, parentSession, ct).ConfigureAwait(false);
        var parentRef = parentSession is null ? null : await SessionModel.ResolveRefAsync(parentSession, ctx.Models,
            ctx.Settings, ctx.Services.Get<IAgentScheduler>(), ct).ConfigureAwait(false);
        var parentModel = parentRef is null ? null : await ctx.Models.FindAsync(parentRef, ct).ConfigureAwait(false);
        var reasoning = request.Reasoning ?? (string.Equals(modelRef, parentModel?.Ref, StringComparison.OrdinalIgnoreCase) ? parentSession?.Reasoning : null);

        // The child's checkout, decided BEFORE the session exists: an explicitly requested workspace must exist and be
        // usable, and a writing worker gets its worktree provisioned here, so a failure means no child at all rather
        // than a runnable child sitting in the parent's checkout. Without the workspace plugin nothing changes.
        var workspace = await ProvisionWorkspaceAsync(request, parentSession, childId, childName, ct).ConfigureAwait(false);

        // The owner chooses a subagent's tools: the ones it names (tools it does not have itself included: a limited
        // orchestrator can dispatch an agent with other tools), or by default its own (its allowlist, and the tools
        // switched off for its session stay off).
        List<string>? allow = request.Tools is { Count: > 0 } t ? [.. t.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())] : null;
        var off = allow is null ? SessionTools.Off(parentSession) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allow is null && parent?.ToolAllowlist is { } parentAllow) allow = [.. parentAllow];
        var registered = ctx.Tools.All.Select(t => t.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var canMessage = (allow is null || ToolLists.Names(allow, "agent", registered)) && !ToolLists.Names(off, "agent", registered);
        var instructions = Instructions(childId, childName, parent, request.Instructions, canMessage);

        var meta = new JsonObject
        {
            ["agentId"] = childId,
            ["parentAgentId"] = parent?.Id,
            ["agentInstructions"] = instructions,
        };
        if (off.Count > 0) meta[SessionTools.MetaKey] = new JsonArray([.. off.Order(StringComparer.Ordinal).Select(n => (JsonNode?)n)]);
        if (anyAgent) meta[SessionAgent.MetaKey] = SessionAgent.Any;
        else if (agent is not null) meta[SessionAgent.MetaKey] = agent.Key;
        // The child's checkout is attached to its session like any other binding: the workspace and the folder it runs in.
        if (workspace is not null)
        {
            meta[SessionWorkspace.MetaKey] = workspace.WorkspaceId;
            meta[SessionCwd.MetaKey] = workspace.Root;
        }
        else if (InheritedWorkspace(parentSession) is { } inherited)
        {
            meta[SessionWorkspace.MetaKey] = inherited;
            if (SessionCwd.Of(parentSession) is { } parentCwd) meta[SessionCwd.MetaKey] = parentCwd;
        }
        return new SpawnPlan
        {
            ModelRef = modelRef,
            Reasoning = reasoning,
            Instructions = instructions,
            ToolAllowlist = allow,
            Workspace = workspace,
            Meta = meta,
        };
    }

    /// <summary>
    /// The child's checkout. Null when no workspace plugin is loaded (the child shares its parent's project, as it always
    /// did); otherwise the provisioner answers, and a refusal is an error that stops the spawn <em>before</em> the child's
    /// session exists, so a failed provisioning cannot leave a runnable child in the parent's checkout.
    /// </summary>
    private async Task<WorkspaceBinding?> ProvisionWorkspaceAsync(SpawnRequest request, SessionInfo? parentSession, string childId, string childName, CancellationToken ct)
    {
        var provisioner = ctx.Services.Get<IWorkspaceProvisioner>();
        if (provisioner is null) return null;
        WorkspaceOutcome outcome;
        try
        {
            outcome = await provisioner.ForChildAsync(request, parentSession, childId, childName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (WorkspaceUnavailableException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
        if (outcome.Error is not null) throw new InvalidOperationException(outcome.Error);
        return outcome.Binding;
    }

    /// <summary>
    /// The parent's workspace, when it can be inherited: the child's own session is a new worker, so it starts where its
    /// parent works (a reader sees the same tree) unless it was given one. A binding whose workspace record has gone is
    /// not inherited, so the child falls back to the project path instead of inheriting a broken one.
    /// </summary>
    private string? InheritedWorkspace(SessionInfo? parentSession)
    {
        if (SessionWorkspace.Of(parentSession) is not { } id) return null;
        return ctx.Services.Get<IWorkspaceStore>()?.GetWorkspace(id) is null ? null : id;
    }

    private static string Instructions(string id, string name, AgentInfo? parent, string? extra, bool canMessage)
    {
        var boss = parent is null ? "the user" : $"\"{parent.Name}\"";
        var sb = new StringBuilder();
        sb.Append($"You are \"{name}\" (agent id {id}), a subagent working for ");
        sb.Append(parent is null ? "the user" : $"\"{parent.Name}\" (agent id {parent.Id})").Append(".\n");
        sb.Append("- Your task is in the first user message. Work autonomously with your tools. You cannot ask the user questions: if something is unclear, make a reasonable assumption and mention it in your report.\n");
        sb.Append("- Stay within the scope of the task.\n");
        sb.Append($"- Finish with a concise final report: what you did, the results or answer, files you changed, and anything left open. Your last message is returned verbatim to {boss} as your result, so make it self-contained.\n");
        if (parent is not null && canMessage)
            sb.Append($"- To tell {boss} something before you finish (for example that you are blocked), use `agent` with action send and to=\"parent\".\n");
        if (!string.IsNullOrWhiteSpace(extra)) sb.Append('\n').Append(extra.Trim()).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>An agent the user set up (<c>agents.&lt;id&gt;</c>) by its id, with its state.</summary>
    private AgentSlots? SetUpAgent(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null
            : ctx.Services.Get<IAgentScheduler>()?.Snapshot().FirstOrDefault(p => p.Configured && string.Equals(p.Key, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The model the child runs on: the one asked for (a model or an agent's key, an agent first so a named agent wins),
    /// else the model the parent resolves to (the chat's, the agent's or the parent's own).
    /// </summary>
    private async Task<string?> ResolveModelAsync(string? requested, SessionInfo? parentSession, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            var inherited = await SessionModel.ResolveRefAsync(parentSession, ctx.Models, ctx.Settings,
                ctx.Services.Get<IAgentScheduler>(), ct).ConfigureAwait(false);
            if (inherited is null) return null;
            return (await ctx.Models.FindAsync(inherited, ct).ConfigureAwait(false))?.Ref ?? inherited;
        }
        requested = requested.Trim();
        ModelInfo? found = null;
        try { found = await ctx.Models.FindAsync(requested, ct).ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        if (found is not null) return found.Ref;

        var pool = ctx.Services.Get<IAgentScheduler>()?.Snapshot()
            .FirstOrDefault(p => string.Equals(p.Key, requested, StringComparison.OrdinalIgnoreCase));
        if (pool is { Models.Count: > 0 })
        {
            var parentRef = await SessionModel.ResolveRefAsync(parentSession, ctx.Models, ctx.Settings,
                ctx.Services.Get<IAgentScheduler>(), ct).ConfigureAwait(false);
            if (parentRef is not null && pool.Models.Contains(parentRef, StringComparer.OrdinalIgnoreCase)) return parentRef;
            var cached = ctx.Models.Cached;
            var best = pool.Models
                .Select(r => cached.FirstOrDefault(m => string.Equals(m.Ref, r, StringComparison.OrdinalIgnoreCase)))
                .Where(m => m is not null)
                .OrderBy(m => m!.Status == "loaded" ? 0 : m.Status is "offline" or "stopped" ? 2 : 1)
                .FirstOrDefault();
            return best?.Ref ?? pool.Models[0];
        }
        throw new ArgumentException($"Unknown agent or model '{requested}'. agent_choices lists the agents.");
    }
}

/// <summary>
/// A planned child: the decisions <see cref="SubagentPlanner"/> took, which <see cref="IAgentRuntime.SpawnAsync"/> creates
/// the child's session, agent and first run from.
/// </summary>
internal sealed record SpawnPlan
{
    /// <summary>The model the child runs on (null when an agent takes the run and its model follows).</summary>
    public required string? ModelRef { get; init; }
    /// <summary>The reasoning effort, inherited from the parent when it runs on the same model.</summary>
    public required string? Reasoning { get; init; }
    /// <summary>The subagent role instructions (the context plugin renders them into the child's system prompt).</summary>
    public required string Instructions { get; init; }
    /// <summary>The tools it may use (null: the parent's allowlist, or everything it has itself).</summary>
    public required List<string>? ToolAllowlist { get; init; }
    /// <summary>The checkout the workspace plugin provisioned for it, when there is one.</summary>
    public required WorkspaceBinding? Workspace { get; init; }
    /// <summary>The session metadata the child is created with: its agent id, instructions, agent, tools off, workspace.</summary>
    public required JsonObject Meta { get; init; }
}