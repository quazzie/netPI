using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Runtime;

/// <summary>One agent run: turns of (steering → context → model call → tools) until the agent stops.</summary>
internal sealed class AgentRunner(AgentRuntime rt, AgentState state, RunState run)
{
    public const string SkippedBySteering =
        "Skipped: a new message arrived before this tool call ran. Read it first, then re-issue this call if it is still needed.";
    public const string NotExecutedStop = "Not executed: the run was stopped.";
    public const string NotExecutedAbort = "Aborted: the run was cancelled before this tool call completed.";

    private AgentRunContext? _rc;
    private long _lastContextTokens = -1;

    private IPluginContext Ctx => rt.Ctx;
    private AgentInfo Info => state.Info;
    private string SessionId => state.Info.SessionId;
    private string AgentId => state.Info.Id;

    public async Task RunAsync()
    {
        var ct = run.Cts.Token;
        var outcome = "completed";
        Exception? error = null;
        try
        {
            await LoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = "aborted";
        }
        catch (RunFailedException ex)
        {
            outcome = "failed";
            error = ex;
        }
        catch (Exception ex)
        {
            outcome = "failed";
            error = ex;
            Ctx.Logger.LogError(ex, "Agent run failed ({Agent}, session {Session})", AgentId, SessionId);
            rt.AppendNotice(state, $"The agent run failed: {ex.Message}", "error");
        }
        finally
        {
            try
            {
                await rt.EndRunAsync(state, run, _rc, outcome, error).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Ctx.Logger.LogError(ex, "Ending the agent run failed ({Agent})", AgentId);
            }
        }
    }

    // ---------------------------------------------------------------- loop

    private async Task LoopAsync(CancellationToken ct)
    {
        var maxTurns = Math.Max(1, rt.IntSetting("agent.maxTurns", 200));
        var turnIndex = 0;
        var retries = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (turnIndex >= maxTurns)
            {
                var msg = $"Stopped: this run reached the limit of {maxTurns} model calls (setting agent.maxTurns).";
                rt.AppendNotice(state, msg, "error");
                throw new RunFailedException(msg);
            }

            // 1. session, model, project, cwd, slot: re-read every turn (they can change mid-run)
            var resolved = await ResolveTurnAsync(turnIndex, ct).ConfigureAwait(false);
            var (rc, session, model, project, cwd, workspace) = resolved;

            // 2. steering input → transcript
            rc.SetAdmissionLease(run.Lease);
            DrainSteering();

            // 3. turn context + hooks
            var promptRevision = SessionPrompt.Revision(session);
            var tools = rt.ToolsFor(Info, session);
            var defs = ToolSelection.Visible(tools);
            var prompt = await BuildPromptAsync(session, project, cwd, model, defs, ct).ConfigureAwait(false);
            if (Ctx.Sessions.GetSession(SessionId) is { } afterPrompt && SessionPrompt.Revision(afterPrompt) != promptRevision) continue;
            AgentTurnContext turn = null!;
            turn = new AgentTurnContext
            {
                Run = rc,
                TurnIndex = turnIndex,
                SystemPrompt = prompt,
                Messages = [.. Ctx.Sessions.GetContextMessages(SessionId)],
                Tools = defs,
                LastContextTokens = Math.Max(0, _lastContextTokens),
                ReloadMessagesAsync = () =>
                {
                    turn.Messages = [.. Ctx.Sessions.GetContextMessages(SessionId)];
                    return Task.CompletedTask;
                },
            };
            foreach (var hook in rt.Hooks())
                await SafeAsync(Ctx, () => hook.OnBeforeModelCallAsync(turn), "OnBeforeModelCall", ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // A hook may have moved the session's workspace: a parked switch is drained at this boundary (the
            // previous batch's tools are done), and so is a switch made from the UI mid-turn. If the root changed, this
            // iteration's root and prompt were built against the checkout the session just left, and the model call
            // they were built for would still work there — the call the switch promised would run one later, with its
            // whole tool batch and its guard. Restart the iteration: the fresh pass re-reads the session, re-resolves
            // the root, rebuilds the prompt against it, and the very next model call is the one that was promised the
            // new workspace. Nothing was spent: no model call happened yet in this pass.
            var afterHooks = Ctx.Sessions.GetSession(SessionId) ?? session;
            var switchedWorkspace = ResolveWorkspace(afterHooks);
            var switchedCwd = switchedWorkspace?.Root ?? Ctx.Sessions.GetCwd(afterHooks);
            if (!string.Equals(switchedWorkspace?.WorkspaceId, workspace?.WorkspaceId, StringComparison.Ordinal)
                || !string.Equals(switchedCwd, cwd, StringComparison.Ordinal))
                continue;

            // 4. model call
            var beforeCall = Ctx.Sessions.GetSession(SessionId);
            if (beforeCall is null || SessionPrompt.Revision(beforeCall) != promptRevision) continue;
            var call = await CallModelWithRecoveryAsync(turn, model, session, beforeCall, promptRevision, retries, ct).ConfigureAwait(false);
            if (call.Assistant is not { } assistant)
            {
                retries++;
                continue; // re-run the turn (rebuilds the context)
            }
            retries = 0;
            if (call.ContextTokens > 0) _lastContextTokens = call.ContextTokens;
            turnIndex++;
            rc.TurnCount++;

            // 5. after-call hooks: first non-null decision wins
            var decided = await RunAfterHooksAsync(turn, assistant, ct).ConfigureAwait(false);
            assistant = decided.Assistant;
            var after = decided.Decision;

            var calls = assistant.ToolCalls.ToList();
            if (after?.Action == TurnAction.Stop)
            {
                var batch = new ToolBatch(rt, state, rc, HasSteering);
                foreach (var c in calls) batch.PersistResult(c, NotExecutedStop, isError: true, publishEnd: false, skipped: "stopped");
                break;
            }

            // 6. tools
            if (calls.Count > 0) await new ToolBatch(rt, state, rc, HasSteering).RunAsync(assistant, turn, calls, tools, ct).ConfigureAwait(false);

            if (after is { Action: TurnAction.Inject } && !string.IsNullOrWhiteSpace(after.Text))
            {
                rt.AppendNotice(state, after.Text, string.IsNullOrWhiteSpace(after.NoticeKind) ? "nudge" : after.NoticeKind);
                continue;
            }
            if (calls.Count > 0) continue;

            // 7. no tool calls: pending steering or one queued follow-up keeps the run going
            if (TakeNextInput()) continue;
            break;
        }
    }

    /// <summary>What one pass of the loop resolved for this turn: the run's context, the session as it is now, its
    /// model, where the turn works, and the workspace the tools' guard must see.</summary>
    private readonly record struct TurnResolution(AgentRunContext Run, SessionInfo Session, ModelInfo Model, ProjectInfo? Project, string Cwd, WorkspaceBinding? Workspace);

    /// <summary>The assistant a model call produced and the context size it reported, or no assistant when a
    /// model-error hook asked for the turn to be retried.</summary>
    private readonly record struct ModelCallOutcome(ChatMessage? Assistant, long ContextTokens);

    private async Task<TurnResolution> ResolveTurnAsync(int turnIndex, CancellationToken ct)
    {
        var session = Ctx.Sessions.GetSession(SessionId) ?? throw new RunFailedException("The session no longer exists.");
        if (turnIndex == 0 && SessionAgent.IsAny(session) && run.Lease is not { IsReleased: false })
        {
            // the chat may use any agent: the first one with a free instance takes the run and its model becomes the chat's
            try
            {
                var (slot, _) = await rt.AcquireAnySlotAsync(state, 0, ct).ConfigureAwait(false);
                run.Lease = slot;
            }
            catch (CallRefusedException ex)
            {
                rt.AppendNotice(state, ex.Message, "error");
                throw new RunFailedException(ex.Message, ex);
            }
            session = Ctx.Sessions.GetSession(SessionId) ?? throw new RunFailedException("The session no longer exists.");
        }
        var modelRef = await SessionModel.ResolveRefAsync(session, Ctx.Models, Ctx.Settings,
            Ctx.Services.Get<IAgentScheduler>(), ct).ConfigureAwait(false);
        ModelInfo? model = null;
        if (!string.IsNullOrWhiteSpace(modelRef))
        {
            try { model = await Ctx.Models.FindAsync(modelRef, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Ctx.Logger.LogWarning(ex, "Model lookup failed for {Model}", modelRef); }
        }
        if (model is null)
        {
            var msg = modelRef is null
                ? "No model is configured. Pick a model for this session or set a default model in the settings."
                : $"Model '{modelRef}' is not available. Pick another model for this session.";
            rt.AppendNotice(state, msg, "error");
            throw new RunFailedException(msg);
        }
        var project = session.ProjectId is null ? null : Ctx.Sessions.GetProject(session.ProjectId);
        // One resolver, asked once per turn: the run's workspace and its root. Every consumer of the run (the tools'
        // Cwd, the guard, the notices, the UI) reads these two, so they cannot disagree about where this turn works.
        var workspace = ResolveWorkspace(session);
        var cwd = workspace?.Root ?? Ctx.Sessions.GetCwd(session);
        rt.Update(state, i => i.Model = model.Ref);

        var context = await SyncRunContextAsync(session, model, project, cwd, workspace, ct).ConfigureAwait(false);

        // a slot on the agent
        try
        {
            await EnsureSlotAsync(model, ct).ConfigureAwait(false);
        }
        catch (CallRefusedException ex)
        {
            rt.AppendNotice(state, ex.Message, "error");
            throw new RunFailedException(ex.Message, ex);
        }
        return new TurnResolution(context.Run, context.Session, model, project, cwd, workspace);
    }

    /// <summary>The run's context, built on the first turn (and run-start hooks) and kept in step with the session on
    /// every later one. Returns that context and the session as the hooks left it.</summary>
    private async Task<(AgentRunContext Run, SessionInfo Session)> SyncRunContextAsync(SessionInfo session, ModelInfo model, ProjectInfo? project,
        string cwd, WorkspaceBinding? workspace, CancellationToken ct)
    {
        if (_rc is null)
        {
            _rc = new AgentRunContext
            {
                Agent = Info,
                Session = session,
                Project = project,
                Cwd = cwd,
                Model = model,
                ReasoningEffort = session.Reasoning,
                Services = Ctx.Services,
                Sessions = Ctx.Sessions,
                Models = Ctx.Models,
                Events = Ctx.Events,
                CancellationToken = ct,
            };
            _rc.SetWorkspace(workspace);
            _lastContextTokens = session.ContextTokens;
            foreach (var hook in rt.Hooks())
                await SafeAsync(Ctx, () => hook.OnRunStartAsync(_rc), "OnRunStart", ct).ConfigureAwait(false);
            // a hook may have set up the session (the profiles plugin applies a new chat's profile here)
            session = Ctx.Sessions.GetSession(SessionId) ?? session;
            _rc.Session = session;
        }
        else
        {
            _rc.Session = session;
            _rc.Project = project;
            _rc.SetWorkspace(workspace);
            _rc.Cwd = cwd;
            _rc.Model = model;
            _rc.ReasoningEffort = session.Reasoning;
        }
        return (_rc, session);
    }

    /// <summary>The call itself, and what its failure means: a model-error hook that asks for a retry hands back no
    /// assistant (the loop runs the turn again), anything else fails the run.</summary>
    private async Task<ModelCallOutcome> CallModelWithRecoveryAsync(AgentTurnContext turn, ModelInfo model, SessionInfo session,
        SessionInfo beforeCall, long promptRevision, int retries, CancellationToken ct)
    {
        try
        {
            // Record the sent prompt only when it is not already recorded for this revision: re-recording would
            // rewrite the session row and broadcast session.updated with the prompt in its meta on every model call
            // (idea-l1o09d). The prompt a revision was sent with is stored once, when it first is. Recorded also
            // covers the built-in prompt builder, which writes the meta without the history entry RecordSent keeps.
            if (!SessionPrompt.Recorded(beforeCall, turn.SystemPrompt))
            {
                var afterSeq = Ctx.Sessions.GetMessages(SessionId, null, 1).LastOrDefault()?.Seq ?? 0;
                Ctx.Sessions.UpdateSession(SessionId, s => SessionPrompt.RecordSent(s, turn.SystemPrompt, promptRevision, afterSeq));
            }
            var (assistant, contextTokens) = await new ModelCall(rt, state, run).RunAsync(turn, model, session, ct).ConfigureAwait(false);
            return new ModelCallOutcome(assistant, contextTokens);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CallRefusedException ex)
        {
            // a middleware refused the call (the ledger stopping a paid one, say): the notice carries the reason's kind, and the UI offers "let this chat go over" when allowed
            rt.AppendNotice(state, ex.Message, ex.Kind, new JsonObject { ["canOverride"] = ex.CanOverride });
            throw new RunFailedException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            // expected provider errors: one line without a stack trace; anything else with the full exception
            if (ex is ModelException or HttpRequestException or IOException)
                Ctx.Logger.LogWarning("Model call failed ({Model}, session {Session}): {Error}", model.Ref, SessionId, ex.Message);
            else
                Ctx.Logger.LogWarning(ex, "Model call failed ({Model}, session {Session})", model.Ref, SessionId);
            ModelErrorDecision? decision = null;
            foreach (var hook in rt.Hooks())
            {
                decision = await SafeAsync(Ctx, () => hook.OnModelErrorAsync(turn, ex), "OnModelError", ct).ConfigureAwait(false);
                if (decision is not null) break;
            }
            if (decision?.Retry == true && retries < 2) return new ModelCallOutcome(null, 0);
            var msg = ModelErrorText(ex);
            rt.AppendNotice(state, msg, "error");
            throw new RunFailedException(msg, ex);
        }
    }

    /// <summary>The after-call hooks (the first decision wins) and the observers that see every call, whoever decided
    /// it. Returns the assistant as it stands after a replacement, and that decision.</summary>
    private async Task<(ChatMessage Assistant, TurnDecision? Decision)> RunAfterHooksAsync(AgentTurnContext turn, ChatMessage assistant, CancellationToken ct)
    {
        turn.LatestAssistant = assistant;
        TurnDecision? after = null;
        foreach (var hook in rt.Hooks())
        {
            var a = assistant;
            after = await SafeAsync(Ctx, () => hook.OnAfterModelCallAsync(turn, a), "OnAfterModelCall", ct).ConfigureAwait(false);
            if (after is not null) break;
        }
        if (after is { Action: TurnAction.Replace, Replacement: { } replacement })
            assistant = new ModelCall(rt, state, run).ReplaceAssistant(assistant, replacement);
        turn.LatestAssistant = assistant;

        // metering and diagnostics see every call, whoever decided it (a hook that only counts would sit behind
        // the first decision and miss those calls: the goal's token budget is the case that bit us)
        foreach (var observer in rt.CallObservers())
            await SafeAsync(Ctx, () => observer.OnAfterModelCallAsync(turn, assistant), "OnAfterModelCall (observer)", ct).ConfigureAwait(false);
        return (assistant, after);
    }

    // One line for a person: DisplayMessage, not Message, so the provider's ids and the saved failed request stay in
    // the log and diag where they belong (idea-qz1a5z).
    private static string ModelErrorText(Exception ex) => ex switch
    {
        ModelException me => $"Model error{(me.StatusCode is { } c ? $" ({c})" : "")}: {me.DisplayMessage}",
        _ => $"Model call failed: {ex.Message}",
    };

    // ---------------------------------------------------------------- hooks

    // a plugin hook that throws must not fail the run: the error is logged and the hook is skipped (static: the tool batch runs hooks too)
    internal static async ValueTask SafeAsync(IPluginContext ctx, Func<ValueTask> call, string what, CancellationToken ct)
    {
        try { await call().ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Agent hook {Hook} failed", what); }
    }

    internal static async ValueTask<T?> SafeAsync<T>(IPluginContext ctx, Func<ValueTask<T?>> call, string what, CancellationToken ct) where T : class
    {
        try { return await call().ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Agent hook {Hook} failed", what);
            return null;
        }
    }

    // ---------------------------------------------------------------- inputs

    private void DrainSteering()
    {
        List<UserInput> items;
        lock (state.Gate)
        {
            if (state.Steering.Count == 0)
            {
                // A cancelled signal with nothing queued is a steer that was taken back: nothing is waiting to be steered to.
                if (state.SteerSignal.IsCancellationRequested) state.SteerSignal = new CancellationTokenSource();
                return;
            }
            items = [.. state.Steering];
            state.Steering.Clear();
            run.Delivered = true;
            Info.QueuedMessages = state.FollowUps.Count;
            if (state.SteerSignal.IsCancellationRequested) state.SteerSignal = new CancellationTokenSource();
        }
        foreach (var input in items)
        {
            // A drained agent-result notice is the parent's copy of the report — unless a wait already returned
            // the report (ResultConsumed): then the notice is dropped, not persisted as a second copy.
            if (input.NoticeKind == "agent-result" && input.Source?.StartsWith("agent:", StringComparison.Ordinal) == true
                && !rt.ClaimNoticeForParent(input.Source!["agent:".Length..]))
                continue;
            rt.PersistInput(state, input, "steer");
        }
        rt.PublishQueue(state);
        rt.PublishStatus(state);
    }

    private bool HasSteering()
    {
        lock (state.Gate) return state.Steering.Count > 0;
    }

    /// <summary>Pending steering → continue. Otherwise deliver ONE queued follow-up and continue. Else stop.</summary>
    private bool TakeNextInput()
    {
        UserInput? next;
        lock (state.Gate)
        {
            if (state.Steering.Count > 0) return true;
            if (state.FollowUps.Count == 0) return false;
            next = state.FollowUps[0];
            state.FollowUps.RemoveAt(0);
            run.Delivered = true;
            Info.QueuedMessages = state.Steering.Count + state.FollowUps.Count;
        }
        rt.PersistInput(state, next, "queue");
        rt.PublishQueue(state);
        rt.PublishStatus(state);
        return true;
    }

    // ---------------------------------------------------------------- slot

    private async Task EnsureSlotAsync(ModelInfo model, CancellationToken ct)
    {
        var scheduler = Ctx.Services.Get<IAgentScheduler>();
        if (scheduler is null)
        {
            if (run.Lease is { IsReleased: false } && run.Model?.Ref == model.Ref) return;
            run.Lease?.Dispose();
            run.Lease = await rt.AcquireSlotAsync(state, run, model, 0, ct).ConfigureAwait(false);
            run.Model = model;
            if (Info.Status != AgentStatus.Running) rt.SetStatus(state, AgentStatus.Running, null, keepActivity: true);
            return;
        }
        // a run stays on the agent it holds for as long as that agent runs the model (a run does not hop between agents)
        if (run.Lease is { IsReleased: false } held && string.Equals(scheduler.ModelOf(held.Key), model.Ref, StringComparison.OrdinalIgnoreCase))
        {
            run.Model = model;
            return;
        }
        var agents = scheduler.Candidates(model, SessionAgent.Running(Ctx.Sessions.GetSession(SessionId)));
        var pool = agents.Count == 0 ? scheduler.Resolve(model) : scheduler.Resolve(model, agents[0]);
        if (run.Lease is { IsReleased: false } lease && (agents.Contains(lease.Key, StringComparer.OrdinalIgnoreCase) || string.Equals(lease.Key, pool, StringComparison.OrdinalIgnoreCase)))
        {
            run.Model = model;
            return;
        }
        if (run.Lease is { } old)
        {
            run.Lease = null;
            old.Dispose();
        }
        run.Lease = await rt.AcquireSlotAsync(state, run, model, 0, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- prompt

    private async Task<string> BuildPromptAsync(SessionInfo session, ProjectInfo? project, string cwd, ModelInfo model,
        IReadOnlyList<ToolDefinition> defs, CancellationToken ct)
    {
        var capturedRevision = SessionPrompt.Revision(session);
        var pc = new PromptContext
        {
            Session = session,
            Project = project,
            Cwd = cwd,
            Model = model,
            Tools = defs,
            Agent = rt.Snapshot(state),
            Instructions = state.Instructions,
        };
        var builder = Ctx.Services.Get<ISystemPromptBuilder>();
        if (builder is not null)
        {
            try
            {
                var prompt = await builder.BuildAsync(pc, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(prompt)) return prompt;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Ctx.Logger.LogWarning(ex, "System prompt builder failed; using the built-in prompt"); }
        }
        return await FallbackPromptBuilder.BuildAsync(Ctx, pc, capturedRevision, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The session's workspace for this turn, or null when it is not bound to one (the pre-workspace behavior: the
    /// project's path). A bound workspace that cannot be used is an error for the run, not a fall back to the project
    /// checkout — the model has to be told, because otherwise its next write lands in the tree it was told not to use.
    /// </summary>
    private WorkspaceBinding? ResolveWorkspace(SessionInfo session)
    {
        var resolver = Ctx.Services.Get<IWorkspaceResolver>();
        if (resolver is null) return null;
        try
        {
            return resolver.Resolve(session);
        }
        catch (WorkspaceUnavailableException ex)
        {
            rt.AppendNotice(state, ex.Message, "error");
            throw new RunFailedException(ex.Message, ex);
        }
    }
}
