using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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

            // 1. session, model, project, cwd: re-read every turn (they can change mid-run)
            var session = Ctx.Sessions.GetSession(SessionId) ?? throw new RunFailedException("The session no longer exists.");
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

            if (_rc is null)
            {
                _rc = new AgentRunContext
                {
                    Agent = Info,
                    Session = session,
                    Project = project,
                    Workspace = workspace,
                    Cwd = cwd,
                    Model = model,
                    ReasoningEffort = session.Reasoning,
                    Services = Ctx.Services,
                    Sessions = Ctx.Sessions,
                    Models = Ctx.Models,
                    Events = Ctx.Events,
                    CancellationToken = ct,
                };
                _lastContextTokens = session.ContextTokens;
                foreach (var hook in rt.Hooks())
                    await SafeAsync(() => hook.OnRunStartAsync(_rc), "OnRunStart", ct).ConfigureAwait(false);
                // a hook may have set up the session (the profiles plugin applies a new chat's profile here)
                session = Ctx.Sessions.GetSession(SessionId) ?? session;
                _rc.Session = session;
            }
            else
            {
                _rc.Session = session;
                _rc.Project = project;
                _rc.Workspace = workspace;
                _rc.Cwd = cwd;
                _rc.Model = model;
                _rc.ReasoningEffort = session.Reasoning;
            }

            // 2. a slot on the agent
            try
            {
                await EnsureSlotAsync(model, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is BudgetExceededException or AgentUnavailableException)
            {
                rt.AppendNotice(state, ex.Message, "error");
                throw new RunFailedException(ex.Message, ex);
            }

            // 3. steering input → transcript
            _rc.AdmissionLease = run.Lease;
            DrainSteering();

            // 4. turn context + hooks
            var promptRevision = SessionPrompt.Revision(session);
            var tools = ActiveTools(session);
            var defs = ToolSelection.Visible(tools);
            var prompt = await BuildPromptAsync(session, project, cwd, model, defs, ct).ConfigureAwait(false);
            if (Ctx.Sessions.GetSession(SessionId) is { } afterPrompt && SessionPrompt.Revision(afterPrompt) != promptRevision) continue;
            AgentTurnContext turn = null!;
            turn = new AgentTurnContext
            {
                Run = _rc,
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
                await SafeAsync(() => hook.OnBeforeModelCallAsync(turn), "OnBeforeModelCall", ct).ConfigureAwait(false);
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

            // 5. model call
            if (Ctx.Sessions.GetSession(SessionId) is { } beforeCall && SessionPrompt.Revision(beforeCall) != promptRevision) continue;
            ChatMessage assistant;
            try
            {
                var afterSeq = Ctx.Sessions.GetMessages(SessionId, null, 1).LastOrDefault()?.Seq ?? 0;
                Ctx.Sessions.UpdateSession(SessionId, s => SessionPrompt.RecordSent(s, turn.SystemPrompt, promptRevision, afterSeq));
                assistant = await CallModelAsync(turn, model, session, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (BudgetExceededException ex)
            {
                // the budget stopped a paid call (the ledger middleware): the UI offers "let this chat go over" when allowed
                rt.AppendNotice(state, ex.Message, "budget", new JsonObject { ["canOverride"] = ex.CanOverride });
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
                    decision = await SafeAsync(() => hook.OnModelErrorAsync(turn, ex), "OnModelError", ct).ConfigureAwait(false);
                    if (decision is not null) break;
                }
                if (decision?.Retry == true && retries < 2)
                {
                    retries++;
                    continue; // re-run the turn (rebuilds the context)
                }
                var msg = ModelErrorText(ex);
                rt.AppendNotice(state, msg, "error");
                throw new RunFailedException(msg, ex);
            }
            retries = 0;
            turnIndex++;
            _rc.TurnCount++;

            // 6. after-call hooks: first non-null decision wins
            turn.LatestAssistant = assistant;
            TurnDecision? after = null;
            foreach (var hook in rt.Hooks())
            {
                var a = assistant;
                after = await SafeAsync(() => hook.OnAfterModelCallAsync(turn, a), "OnAfterModelCall", ct).ConfigureAwait(false);
                if (after is not null) break;
            }
            if (after is { Action: TurnAction.Replace, Replacement: { } replacement })
                assistant = ReplaceAssistant(assistant, replacement);
            turn.LatestAssistant = assistant;

            // metering and diagnostics see every call, whoever decided it (a hook that only counts would sit behind
            // the first decision and miss those calls: the goal's token budget is the case that bit us)
            foreach (var observer in rt.CallObservers())
                await SafeAsync(() => observer.OnAfterModelCallAsync(turn, assistant), "OnAfterModelCall (observer)", ct).ConfigureAwait(false);

            var calls = assistant.ToolCalls.ToList();
            if (after?.Action == TurnAction.Stop)
            {
                foreach (var c in calls) PersistResult(c, NotExecutedStop, isError: true, publishEnd: false, skipped: "stopped");
                break;
            }

            // 7. tools
            if (calls.Count > 0) await ExecuteToolsAsync(assistant, turn, calls, tools, ct).ConfigureAwait(false);

            if (after is { Action: TurnAction.Inject } && !string.IsNullOrWhiteSpace(after.Text))
            {
                rt.AppendNotice(state, after.Text, string.IsNullOrWhiteSpace(after.NoticeKind) ? "nudge" : after.NoticeKind);
                continue;
            }
            if (calls.Count > 0) continue;

            // 8. no tool calls: pending steering or one queued follow-up keeps the run going
            if (TakeNextInput()) continue;
            break;
        }
    }

    // One line for a person: DisplayMessage, not Message, so the provider's ids and the saved failed request stay in
    // the log and diag where they belong (idea-qz1a5z).
    private static string ModelErrorText(Exception ex) => ex switch
    {
        ModelException me => $"Model error{(me.StatusCode is { } c ? $" ({c})" : "")}: {me.DisplayMessage}",
        _ => $"Model call failed: {ex.Message}",
    };

    // ---------------------------------------------------------------- hooks

    private async ValueTask SafeAsync(Func<ValueTask> call, string what, CancellationToken ct)
    {
        try { await call().ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Ctx.Logger.LogWarning(ex, "Agent hook {Hook} failed", what); }
    }

    private async ValueTask<T?> SafeAsync<T>(Func<ValueTask<T?>> call, string what, CancellationToken ct) where T : class
    {
        try { return await call().ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Ctx.Logger.LogWarning(ex, "Agent hook {Hook} failed", what);
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
        foreach (var input in items) rt.PersistInput(state, input, "steer");
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
        var pool = scheduler.Resolve(model, rt.AgentFor(SessionId, model, scheduler));
        if (run.Lease is { IsReleased: false } lease && string.Equals(lease.Key, pool, StringComparison.OrdinalIgnoreCase))
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

    // ---------------------------------------------------------------- tools and prompt

    private List<IAgentTool> ActiveTools(SessionInfo session) => rt.ToolsFor(Info, session);

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
        var environment = $"Working directory: {cwd}\nProject: {project?.Name ?? "none"}\nModel: {model.Ref}";
        if (session.Meta?["runtimeEnvironment"]?.GetValue<string>() != environment)
        {
            Ctx.Sessions.AppendMessage(session.Id, ChatMessage.NoticeText(environment, "environment"));
            Ctx.Sessions.UpdateSession(session.Id, s => { s.Meta ??= new JsonObject(); s.Meta["runtimeEnvironment"] = environment; });
        }
        if (SessionPrompt.Fallback(session) is { } frozen) return frozen;
        var sections = Ctx.Services.GetAll<IPromptSection>().OrderBy(s => s.Order).ToList();
        var parts = new List<string>();
        var identity = SessionIdentity.Of(session);
        if (identity is not null) parts.Add(identity);
        var renderedSections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            if (identity is not null && section.Id == "identity") continue;
            try
            {
                var text = await section.RenderAsync(pc, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text)) { parts.Add(text.Trim()); renderedSections.Add(section.Id); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Ctx.Logger.LogWarning(ex, "Prompt section {Section} failed", section.Id); }
        }
        if (identity is null && !renderedSections.Contains("identity")) parts.Insert(0,
            Ctx.Settings.Get("context.customPrompt", "") is { Length: > 0 } custom ? custom : FallbackPrompt(pc));
        if (!renderedSections.Contains("tools"))
        {
            var guidelines = defs.SelectMany(t => t.PromptGuidelines ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.Ordinal);
            parts.Add(string.Join("\n", guidelines.Select(g => "- " + g)));
        }
        if (!renderedSections.Contains("subagent") && !string.IsNullOrWhiteSpace(pc.Instructions)) parts.Add("# Your role\n" + pc.Instructions.Trim());
        var rendered = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        Ctx.Sessions.UpdateSession(session.Id, s =>
        {
            if (SessionPrompt.Revision(s) != capturedRevision) return;
            s.Meta ??= new JsonObject();
            s.Meta[SessionPrompt.FallbackKey] = rendered;
            s.Meta[SessionPrompt.FallbackRevisionKey] = capturedRevision;
        });
        return rendered;
    }

    internal static string FallbackPrompt(PromptContext pc)
    {
        var sb = new StringBuilder();
        // Only used without the context plugin (which freezes the prompt and announces the working directory in notices):
        // no date or time, but the working directory has to be here.
        sb.Append("You are a coding agent running in NetPI, an agent harness on the user's machine. ");
        sb.Append("Be concise. Act, don't just describe: use your tools to do the work and check the result.\n\n");
        sb.Append("OS: ").Append(RuntimeInformation.OSDescription).Append('\n');
        sb.Append("Working directory: ").Append(pc.Cwd).Append('\n');
        sb.Append("Project: ").Append(pc.Project is { } p ? $"{p.Name} ({p.Path})" : "none").Append('\n');
        sb.Append("Model: ").Append(pc.Model.Ref);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- model call

    /// <summary>Accumulates streamed parts so an aborted call can persist what was produced so far.</summary>
    private sealed class PartialMessage
    {
        private readonly List<(MessagePart Part, StringBuilder Text)> _slots = [];

        public void Add<TPart>(string text) where TPart : MessagePart, new()
        {
            if (_slots.Count > 0 && _slots[^1].Part is TPart) _slots[^1].Text.Append(text);
            else _slots.Add((new TPart(), new StringBuilder(text)));
        }

        public void ToolCall(string id, string name) => _slots.Add((new ToolCallPart { Id = id, Name = name }, new StringBuilder()));
        public void Clear() => _slots.Clear();

        /// <summary>Text and thinking produced so far (no incomplete tool calls).</summary>
        public List<MessagePart> Build()
        {
            var parts = new List<MessagePart>();
            foreach (var (part, text) in _slots)
            {
                if (text.Length == 0) continue;
                switch (part)
                {
                    case TextPart: parts.Add(new TextPart { Text = text.ToString() }); break;
                    case ThinkingPart: parts.Add(new ThinkingPart { Text = text.ToString() }); break;
                }
            }
            return parts;
        }
    }

    private async Task<ChatMessage> CallModelAsync(AgentTurnContext turn, ModelInfo model, SessionInfo session, CancellationToken ct)
    {
        var request = new ModelRequest
        {
            CaptureDecisionContext = Ctx.Settings.Get("loops.contextChecks", false) || Ctx.Settings.Get("todo.checkCommits", false),
            CorrelationId = Guid.NewGuid().ToString("N"),
            Model = model,
            SystemPrompt = turn.SystemPrompt,
            // the first turn's notices before the first user message (ContextOrder); hooks see the stored order
            Messages = [.. ContextOrder.FirstTurnNoticesFirst(turn.Messages)],
            Tools = turn.Tools,
            ReasoningEffort = session.Reasoning,
            MaxOutputTokens = model.MaxOutputTokens ?? rt.IntSetting("agent.defaultMaxOutputTokens", 16384),
            SessionId = SessionId,
            AgentId = AgentId,
            Purpose = "agent",
        };
        turn.SentRequest = request;
        var physical = Ctx.Services.Get<IResourceLeases>();
        var leaseId = run.Lease?.LeaseId;
        if (leaseId is not null) physical?.Update(leaseId, new ResourceLeaseUpdate { BeginCall = true, CorrelationId = request.CorrelationId, Purpose = request.Purpose });
        using var cancellation = ct.Register(() =>
        {
            if (leaseId is not null) physical?.Update(leaseId, new ResourceLeaseUpdate { CancellationRequested = true });
        });
        using var acknowledgement = new ProviderAcknowledgement(physical, leaseId);
        var partial = new PartialMessage();
        var sw = Stopwatch.StartNew();
        long? thinkStart = null, thinkEnd = null;
        long? firstToken = null; // the call's first thinking, text or tool call (as diag.calls measures it)
        Usage? lastUsage = null;
        ChatMessage? final = null;

        rt.Emit(EventTypes.StreamStart, new JsonObject { ["sessionId"] = SessionId, ["agentId"] = AgentId, ["model"] = model.Ref }, SessionId);
        rt.SetActivity(state, "waiting for model");
        using var emitter = new StreamEmitter(Ctx.Events, SessionId);

        void EndThinking()
        {
            if (thinkStart is not null && thinkEnd is null) thinkEnd = sw.ElapsedMilliseconds;
        }

        try
        {
            await foreach (var ev in Ctx.Models.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case ThinkingDelta d:
                        firstToken ??= sw.ElapsedMilliseconds;
                        thinkStart ??= sw.ElapsedMilliseconds;
                        partial.Add<ThinkingPart>(d.Text);
                        emitter.Delta("thinking", d.Text);
                        rt.SetActivity(state, "thinking");
                        break;
                    case TextDelta d:
                        firstToken ??= sw.ElapsedMilliseconds;
                        EndThinking();
                        partial.Add<TextPart>(d.Text);
                        emitter.Delta("text", d.Text);
                        rt.SetActivity(state, "writing");
                        break;
                    case ToolCallStarted t:
                        firstToken ??= sw.ElapsedMilliseconds;
                        EndThinking();
                        emitter.Flush();
                        partial.ToolCall(t.Id, t.Name);
                        rt.Emit(EventTypes.StreamTool, new JsonObject { ["sessionId"] = SessionId, ["callId"] = t.Id, ["name"] = t.Name }, SessionId);
                        rt.SetActivity(state, $"preparing {t.Name}");
                        break;
                    case ToolCallArgsDelta:
                        break;
                    case UsageUpdate u:
                        lastUsage = u.Usage;
                        break;
                    case StreamReset r:
                        emitter.Discard();
                        partial.Clear();
                        thinkStart = thinkEnd = null;
                        lastUsage = null;
                        rt.Emit(EventTypes.StreamReset, new JsonObject { ["sessionId"] = SessionId, ["reason"] = r.Reason }, SessionId);
                        break;
                    case StreamNotice n:
                        emitter.Flush();
                        rt.Emit(EventTypes.AgentNotice, new JsonObject { ["sessionId"] = SessionId, ["level"] = n.Level, ["text"] = n.Text }, SessionId);
                        break;
                    case StreamCompleted c:
                        EndThinking();
                        final ??= c.Message;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            emitter.Dispose();
            rt.Emit(EventTypes.StreamEnd, new JsonObject { ["sessionId"] = SessionId }, SessionId);
            EndThinking();
            var parts = partial.Build();
            if (parts.Count > 0)
            {
                if (thinkStart is { } ts && parts.OfType<ThinkingPart>().FirstOrDefault() is { } th) th.DurationMs = (thinkEnd ?? sw.ElapsedMilliseconds) - ts;
                var aborted = new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    SessionId = SessionId,
                    Parts = parts,
                    Provider = model.Provider,
                    Model = model.Id,
                    StopReason = "aborted",
                    Usage = lastUsage,
                    DurationMs = sw.ElapsedMilliseconds,
                    Meta = new JsonObject { ["agentId"] = AgentId },
                };
                if (firstToken is { } firstMs) aborted.Meta["ttftMs"] = firstMs;
                try
                {
                    Ctx.Sessions.AppendMessage(SessionId, aborted);
                    if (!string.IsNullOrWhiteSpace(aborted.Text)) run.LastAssistantText = aborted.Text;
                }
                catch (Exception ex) { Ctx.Logger.LogWarning(ex, "Failed to persist the aborted message"); }
            }
            throw;
        }
        catch
        {
            emitter.Dispose();
            rt.Emit(EventTypes.StreamEnd, new JsonObject { ["sessionId"] = SessionId }, SessionId);
            throw;
        }

        emitter.Dispose();
        rt.Emit(EventTypes.StreamEnd, new JsonObject { ["sessionId"] = SessionId }, SessionId);
        if (final is null) throw new ModelException("The model stream ended without a completed message.", transient: true);

        final.Role = MessageRole.Assistant;
        final.SessionId = SessionId;
        final.Provider ??= model.Provider;
        final.Model ??= model.Id;
        final.DurationMs ??= sw.ElapsedMilliseconds;
        final.Usage ??= lastUsage;
        if (final.StopReason is null) final.StopReason = final.ToolCalls.Any() ? "tool_use" : "stop";
        if (thinkStart is { } start && final.Parts.OfType<ThinkingPart>().FirstOrDefault(p => p.DurationMs is null && p.Text.Length > 0) is { } think)
            think.DurationMs = (thinkEnd ?? sw.ElapsedMilliseconds) - start;
        final.Meta ??= new JsonObject();
        final.Meta["agentId"] = AgentId;
        if (firstToken is { } ttft) final.Meta["ttftMs"] = ttft;

        final = Ctx.Sessions.AppendMessage(SessionId, final);
        if (!string.IsNullOrWhiteSpace(final.Text)) run.LastAssistantText = final.Text;
        rt.Update(state, i => i.Turns++);

        if (final.Usage is { } usage)
        {
            rt.Update(state, i =>
            {
                i.InputTokens += usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens;
                i.OutputTokens += usage.OutputTokens;
            });
            var used = usage.ContextTokens;
            if (used > 0)
            {
                _lastContextTokens = used;
                try { Ctx.Sessions.UpdateSession(SessionId, s => s.ContextTokens = used); }
                catch (Exception ex) { Ctx.Logger.LogDebug(ex, "Failed to update session context size"); }
                rt.Emit(EventTypes.SessionContext, new JsonObject
                {
                    ["sessionId"] = SessionId,
                    ["used"] = used,
                    ["window"] = model.ContextWindow,
                });
            }
            rt.Emit(EventTypes.UsageRecorded, new JsonObject
            {
                ["provider"] = final.Provider ?? model.Provider,
                ["model"] = model.Id,
                ["usage"] = NetPiJson.ToNode(usage),
                ["sessionId"] = SessionId,
                ["agentId"] = AgentId,
            });
        }
        rt.SetActivity(state, null);
        return final;
    }

    private ChatMessage ReplaceAssistant(ChatMessage original, ChatMessage replacement)
    {
        replacement.Id = original.Id;
        replacement.Seq = original.Seq;
        replacement.SessionId = original.SessionId;
        replacement.Role = MessageRole.Assistant;
        replacement.CreatedAt = original.CreatedAt;
        replacement.Provider ??= original.Provider;
        replacement.Model ??= original.Model;
        replacement.Usage ??= original.Usage;
        replacement.DurationMs ??= original.DurationMs;
        if (replacement.ToolCalls.Any() && replacement.StopReason is null or "stop") replacement.StopReason = "tool_use";
        replacement.StopReason ??= original.StopReason;
        replacement.Meta ??= original.Meta?.DeepClone() as JsonObject ?? new JsonObject();
        replacement.Meta["agentId"] = AgentId;
        if (replacement.Meta["ttftMs"] is null && original.Meta?["ttftMs"] is { } ttft) replacement.Meta["ttftMs"] = ttft.DeepClone(); // the same model call
        try { Ctx.Sessions.UpdateMessage(replacement); }
        catch (Exception ex) { Ctx.Logger.LogWarning(ex, "Failed to update the replaced assistant message"); }
        if (!string.IsNullOrWhiteSpace(replacement.Text)) run.LastAssistantText = replacement.Text;
        return replacement;
    }

    // ---------------------------------------------------------------- tool execution

    private sealed class PreparedCall
    {
        public required ToolCallPart Call { get; init; }
        public IAgentTool? Tool { get; set; }
        public ToolCallPart? EffectiveCall { get; set; }
        public IIndirectAgentTool? Gateway { get; set; }
        public JsonElement OriginalArguments { get; set; }
        public string? ServerId { get; set; }
        public JsonElement Args { get; set; }
        public ToolResult? Early { get; set; }
        public Stopwatch Watch { get; } = Stopwatch.StartNew();
    }

    private static IAgentTool? FindTool(IReadOnlyList<IAgentTool> tools, string name) =>
        tools.FirstOrDefault(t => t.Definition.Name == name)
        ?? tools.FirstOrDefault(t => string.Equals(t.Definition.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The call only reads: a read-only tool, or a tool with actions that says so for these arguments.</summary>
    private static bool ReadsOnly(IAgentTool? tool, string? arguments)
    {
        if (tool is null || tool is IIndirectAgentTool) return false;
        if (tool is not IReadOnlyCalls calls) return tool.Definition.ReadOnly;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            return calls.IsReadOnly(doc.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private sealed class ProviderAcknowledgement(IResourceLeases? physical, string? leaseId) : IDisposable
    {
        public void Dispose()
        {
            if (leaseId is not null) physical?.Update(leaseId, new ResourceLeaseUpdate { ProviderReturned = true });
        }
    }

    private async Task ExecuteToolsAsync(ChatMessage assistant, AgentTurnContext turn, List<ToolCallPart> calls, List<IAgentTool> tools, CancellationToken ct)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var started = new HashSet<string>(StringComparer.Ordinal);
        var argsChanged = false;
        var parallel = calls.Count > 1
                       && rt.BoolSetting("agent.parallelReadOnlyTools", true)
                       && calls.All(c => ReadsOnly(FindTool(tools, c.Name), c.Arguments));
        try
        {
            if (parallel)
            {
                var prepared = new List<PreparedCall>(calls.Count);
                foreach (var call in calls)
                {
                    var (p, changed) = await PrepareAsync(turn, call, tools, ct).ConfigureAwait(false);
                    started.Add(call.Id);
                    argsChanged |= changed;
                    prepared.Add(p);
                }
                rt.SetActivity(state, $"running {calls.Count} tools");
                using var slots = new SemaphoreSlim(4);
                using var finish = new SemaphoreSlim(1);
                await Task.WhenAll(prepared.Select(async p =>
                {
                    ToolResultPart result;
                    await slots.WaitAsync(ct).ConfigureAwait(false);
                    try { result = await InvokeAsync(p, ct).ConfigureAwait(false); }
                    finally { slots.Release(); }
                    await finish.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        await FinishAsync(turn, p, result, done, ct).ConfigureAwait(false);
                    }
                    finally { finish.Release(); }
                })).ConfigureAwait(false);
            }
            else
            {
                for (var i = 0; i < calls.Count; i++)
                {
                    // Tools that handle cancellation themselves (the shell tools return an "[aborted]" result instead of
                    // throwing) must not let an aborted run go on to execute the rest of the batch.
                    ct.ThrowIfCancellationRequested();
                    var (p, changed) = await PrepareAsync(turn, calls[i], tools, ct).ConfigureAwait(false);
                    started.Add(calls[i].Id);
                    argsChanged |= changed;
                    var result = await InvokeAsync(p, ct).ConfigureAwait(false);
                    await FinishAsync(turn, p, result, done, ct).ConfigureAwait(false);

                    if (i < calls.Count - 1 && HasSteering())
                    {
                        for (var j = i + 1; j < calls.Count; j++)
                        {
                            PersistResult(calls[j], SkippedBySteering, isError: true, publishEnd: false, skipped: "steer");
                            done.Add(calls[j].Id);
                        }
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var c in calls)
                if (!done.Contains(c.Id)) PersistResult(c, NotExecutedAbort, isError: true, publishEnd: started.Contains(c.Id), skipped: "aborted");
            throw;
        }
        finally
        {
            if (argsChanged)
            {
                try { Ctx.Sessions.UpdateMessage(assistant); }
                catch (Exception ex) { Ctx.Logger.LogDebug(ex, "Failed to update tool call arguments"); }
            }
            rt.SetActivity(state, null);
        }
    }

    private ToolContext CallContext(string callId, Action<string>? output = null) => new()
    {
        AdmissionLease = _rc?.AdmissionLease,
        SessionId = SessionId, AgentId = AgentId, CallId = callId,
        Cwd = _rc!.Cwd, Project = _rc.Project, Workspace = _rc.Workspace, Model = _rc.Model,
        Services = Ctx.Services, Events = Ctx.Events, Output = output,
        EligibleTools = () => ActiveTools(Ctx.Sessions.GetSession(SessionId) ?? _rc.Session),
    };

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

    private bool StillEligible(PreparedCall p)
    {
        var eligible = ActiveTools(Ctx.Sessions.GetSession(SessionId) ?? _rc!.Session);
        return p.Tool is not null && eligible.Any(t => ReferenceEquals(t, p.Tool))
            && (p.Gateway is null || eligible.Any(t => ReferenceEquals(t, p.Gateway)));
    }

    private async Task<(PreparedCall Call, bool ArgsChanged)> PrepareAsync(AgentTurnContext turn, ToolCallPart call, List<IAgentTool> tools, CancellationToken ct)
    {
        var tool = FindTool(tools, call.Name);
        var prepared = new PreparedCall { Call = call, Tool = tool };
        var changed = false;
        var original = call.Arguments;
        try
        {
            if (tool is null) prepared.Early = ToolResult.Error($"Unknown tool '{call.Name}'. Available tools: {string.Join(", ", ToolSelection.Visible(tools).Select(t => t.Name))}.");
            else if (tool.Definition.Deferred) prepared.Early = ToolResult.Error("This tool is deferred. Discover its schema and use its invocation gateway.");
            else
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
                prepared.Args = doc.RootElement.Clone();
                if (ToolHelp.Asked(tool.Definition, prepared.Args))
                    prepared.Early = ToolResult.Ok(ToolHelp.Text(tool.Definition));
                else if (tool is IIndirectAgentTool gateway)
                {
                    prepared.Gateway = gateway;
                    prepared.OriginalArguments = prepared.Args;
                    var resolved = await gateway.ResolveAsync(CallContext(call.Id), prepared.Args, ct).ConfigureAwait(false);
                    prepared.Tool = FindTool(ActiveTools(Ctx.Sessions.GetSession(SessionId) ?? _rc!.Session), resolved.ToolName);
                    prepared.ServerId = resolved.ServerId;
                    if (prepared.Tool is null || prepared.Tool is IIndirectAgentTool)
                        prepared.Early = ToolResult.Error("The resolved tool is unavailable or recursive invocation was refused.");
                    else
                    {
                        prepared.Args = resolved.Arguments.Clone();
                        prepared.EffectiveCall = new ToolCallPart { Id = call.Id, Name = resolved.ToolName, Arguments = resolved.Arguments.GetRawText() };
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (JsonException ex) { prepared.Early = ToolResult.Error($"Invalid JSON arguments for {call.Name}: {ex.Message}"); }
        catch (Exception ex) { prepared.Early = ToolResult.Error(ex.Message); }

        var effective = prepared.EffectiveCall ?? call;
        rt.Emit(EventTypes.ToolStart, new JsonObject
        {
            ["sessionId"] = SessionId, ["agentId"] = AgentId, ["callId"] = call.Id, ["name"] = call.Name,
            ["label"] = prepared.Tool?.Definition.Label ?? call.Name, ["arguments"] = call.Arguments,
            ["resolvedTool"] = prepared.EffectiveCall?.Name, ["serverId"] = prepared.ServerId,
        }, SessionId);
        rt.SetActivity(state, $"tool: {effective.Name}");
        if (prepared.Early is not null) return (prepared, false);
        if (!StillEligible(prepared)) { prepared.Early = ToolResult.Error("The tool was disabled or replaced before execution."); return (prepared, false); }

        foreach (var hook in rt.Hooks())
        {
            var decision = await SafeAsync(() => hook.OnBeforeToolCallAsync(turn, effective), "OnBeforeToolCall", ct).ConfigureAwait(false);
            if (decision is null) continue;
            if (decision.Block)
            {
                if (prepared.Gateway is null) call.Arguments = original;
                prepared.Early = ToolResult.Error("Blocked: " + (decision.Reason ?? "this tool call was blocked by a policy hook."));
                return (prepared, false);
            }
            if (decision.Arguments is { } newArgs && newArgs != effective.Arguments)
            {
                effective.Arguments = newArgs;
                changed = prepared.Gateway is null;
            }
        }
        if (!effective.Name.Equals(prepared.Tool!.Definition.Name, StringComparison.OrdinalIgnoreCase) || !StillEligible(prepared))
        { prepared.Early = ToolResult.Error("The tool changed or was disabled during policy checks."); return (prepared, changed); }
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(effective.Arguments) ? "{}" : effective.Arguments);
            prepared.Args = doc.RootElement.Clone();
            if (ToolHelp.Asked(prepared.Tool.Definition, prepared.Args))
                prepared.Early = ToolResult.Ok(ToolHelp.Text(prepared.Tool.Definition));
        }
        catch (JsonException ex) { prepared.Early = ToolResult.Error($"Invalid JSON arguments for {effective.Name}: {ex.Message}"); }
        return (prepared, changed);
    }

    private async Task<ToolResultPart> InvokeAsync(PreparedCall p, CancellationToken ct)
    {
        ToolResult result;
        if (p.Early is not null || p.Tool is null)
        {
            result = p.Early ?? ToolResult.Error("Unknown tool.");
        }
        else
        {
            ct.ThrowIfCancellationRequested(); // never start a tool for a run that was aborted meanwhile
            using var output = new ToolOutputEmitter(Ctx.Events, SessionId, p.Call.Id);
            var context = CallContext(p.Call.Id, output.Write);
            try
            {
                if (!StillEligible(p)) result = ToolResult.Error("The tool was disabled or replaced before execution.");
                else
                {
                    if (p.Gateway is not null) await p.Gateway.ValidateAsync(context, p.OriginalArguments, ct).ConfigureAwait(false);
                    result = await p.Tool.ExecuteAsync(context, p.Args, ct).ConfigureAwait(false) ?? ToolResult.Error("The tool returned no result.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Ctx.Logger.LogDebug(ex, "Tool {Tool} threw", p.Call.Name);
                result = ToolResult.Error($"Tool '{p.Call.Name}' failed: {ex.Message}");
            }
        }

        JsonNode? details = null;
        if (result.Details is not null)
        {
            try { details = NetPiJson.ToNode(result.Details); }
            catch (Exception ex) { Ctx.Logger.LogDebug(ex, "Tool details of {Tool} are not serializable", p.Call.Name); }
        }
        if (p.EffectiveCall is not null)
        {
            var resolved = details as JsonObject ?? new JsonObject { ["result"] = details };
            resolved["resolvedTool"] = p.EffectiveCall.Name;
            resolved["serverId"] = p.ServerId;
            resolved["arguments"] = p.EffectiveCall.Arguments;
            details = resolved;
        }
        return new ToolResultPart
        {
            CallId = p.Call.Id,
            Name = p.Call.Name,
            Content = LimitResult(result.Content ?? "", p.Call),
            IsError = result.IsError,
            Images = result.Images,
            Details = details,
            DurationMs = p.Watch.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// A result longer than <c>agent.maxToolResultChars</c> (20000): the whole of it goes to a file, and the model gets its
    /// start and end with the path, to read the rest in parts or search it instead of losing it.
    /// </summary>
    private string LimitResult(string content, ToolCallPart call)
    {
        var max = rt.IntSetting(ToolResultLimit.Setting, ToolResultLimit.Default);
        if (max <= 0 || content.Length <= max) return content;
        var head = max * 2 / 3;
        if (head > 0 && char.IsHighSurrogate(content[head - 1])) head--;
        var tail = max - head;
        if (tail > 0 && char.IsLowSurrogate(content[^tail])) tail--;
        var file = rt.SaveToolResult(SessionId, call, content);
        var where = file is null
            ? "It could not be saved; narrow the request (offset/limit, a more specific pattern, head/tail) to see it."
            : $"The whole result is in {file}: read the part you need (read with offset/limit) or search it (grep) instead of running the call again.";
        return content[..head]
               + $"\n\n[... {content.Length - head - tail:N0} of {content.Length:N0} characters not shown (limit {max:N0}). {where} ...]\n\n"
               + content[^tail..];
    }

    private async Task FinishAsync(AgentTurnContext turn, PreparedCall p, ToolResultPart result, HashSet<string> done, CancellationToken ct)
    {
        Persist(result);
        // the result is in the transcript: the call is done. A run cancelled in a hook below must not write a second
        // result ("aborted") for the same call id (idea-3coif8).
        done.Add(p.Call.Id);
        rt.Emit(EventTypes.ToolEnd, new JsonObject
        {
            ["sessionId"] = SessionId,
            ["callId"] = p.Call.Id,
            ["name"] = p.Call.Name,
            ["isError"] = result.IsError,
            ["durationMs"] = result.DurationMs,
            ["resolvedTool"] = p.EffectiveCall?.Name,
            ["serverId"] = p.ServerId,
        }, SessionId);
        rt.Update(state, i => i.ToolCalls++);
        if (_rc is not null) _rc.ToolCallCount++;
        rt.PublishStatus(state, throttled: true);
        foreach (var hook in rt.Hooks())
            await SafeAsync(() => hook.OnAfterToolCallAsync(turn, p.EffectiveCall ?? p.Call, result), "OnAfterToolCall", ct).ConfigureAwait(false);
    }

    private void Persist(ToolResultPart result)
    {
        try
        {
            Ctx.Sessions.AppendMessage(SessionId, new ChatMessage
            {
                Role = MessageRole.Tool,
                SessionId = SessionId,
                Parts = [result],
                Meta = new JsonObject { ["agentId"] = AgentId },
            });
        }
        catch (Exception ex)
        {
            Ctx.Logger.LogWarning(ex, "Failed to persist the result of {Tool}", result.Name);
        }
    }

    /// <summary>Persist a result for a call that did not (fully) run. <paramref name="skipped"/> (steer | aborted | stopped)
    /// is stored as <c>details.skipped</c> so the UI can show it as skipped rather than as a failure.</summary>
    private void PersistResult(ToolCallPart call, string content, bool isError, bool publishEnd, string? skipped = null)
    {
        Persist(new ToolResultPart
        {
            CallId = call.Id, Name = call.Name, Content = content, IsError = isError, DurationMs = 0,
            Details = skipped is null ? null : new JsonObject { ["skipped"] = skipped },
        });
        if (publishEnd)
            rt.Emit(EventTypes.ToolEnd, new JsonObject
            {
                ["sessionId"] = SessionId,
                ["callId"] = call.Id,
                ["name"] = call.Name,
                ["isError"] = isError,
                ["durationMs"] = 0,
                ["skipped"] = skipped,
            }, SessionId);
    }
}
