using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Agent;

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
            var modelRef = session.Model ?? await DefaultModelRefAsync(ct).ConfigureAwait(false);
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
            var cwd = Ctx.Sessions.GetCwd(session);
            rt.Update(state, i => i.Model = model.Ref);

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
                _lastContextTokens = session.ContextTokens;
                foreach (var hook in rt.Hooks())
                    await SafeAsync(() => hook.OnRunStartAsync(_rc), "OnRunStart", ct).ConfigureAwait(false);
            }
            else
            {
                _rc.Session = session;
                _rc.Project = project;
                _rc.Cwd = cwd;
                _rc.Model = model;
                _rc.ReasoningEffort = session.Reasoning;
            }

            // 2. lane
            try
            {
                await EnsureLaneAsync(model, ct).ConfigureAwait(false);
            }
            catch (BudgetExceededException ex)
            {
                rt.AppendNotice(state, ex.Message, "error");
                throw new RunFailedException(ex.Message, ex);
            }

            // 3. steering input → transcript
            DrainSteering();

            // 4. turn context + hooks
            var tools = ActiveTools();
            var defs = tools.Select(t => t.Definition).ToList();
            var prompt = await BuildPromptAsync(session, project, cwd, model, defs, ct).ConfigureAwait(false);
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

            // 5. model call
            ChatMessage assistant;
            try
            {
                assistant = await CallModelAsync(turn, model, session, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
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
            TurnDecision? after = null;
            foreach (var hook in rt.Hooks())
            {
                var a = assistant;
                after = await SafeAsync(() => hook.OnAfterModelCallAsync(turn, a), "OnAfterModelCall", ct).ConfigureAwait(false);
                if (after is not null) break;
            }
            if (after is { Action: TurnAction.Replace, Replacement: { } replacement })
                assistant = ReplaceAssistant(assistant, replacement);

            var calls = assistant.ToolCalls.ToList();
            if (after?.Action == TurnAction.Stop)
            {
                foreach (var c in calls) PersistResult(c, NotExecutedStop, isError: true, publishEnd: false);
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

    /// <summary>
    /// The default model. Without a <c>defaultModel</c> setting it is derived from the catalog cache, which is empty until
    /// the first model listing finishes (right after startup, or after a provider reload): list once, then ask again.
    /// </summary>
    private async Task<string?> DefaultModelRefAsync(CancellationToken ct)
    {
        var modelRef = Ctx.Models.DefaultModelRef;
        if (!string.IsNullOrWhiteSpace(modelRef)) return modelRef;
        try { await Ctx.Models.ListAsync(refresh: Ctx.Models.Cached.Count == 0, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Ctx.Logger.LogWarning(ex, "Listing models for the default model failed"); }
        return Ctx.Models.DefaultModelRef;
    }

    private static string ModelErrorText(Exception ex) => ex switch
    {
        ModelException me => $"Model error{(me.StatusCode is { } c ? $" ({c})" : "")}: {me.Message}",
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
            if (state.Steering.Count == 0) return;
            items = [.. state.Steering];
            state.Steering.Clear();
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
            Info.QueuedMessages = state.Steering.Count + state.FollowUps.Count;
        }
        rt.PersistInput(state, next, "queue");
        rt.PublishQueue(state);
        rt.PublishStatus(state);
        return true;
    }

    // ---------------------------------------------------------------- lane

    private async Task EnsureLaneAsync(ModelInfo model, CancellationToken ct)
    {
        var scheduler = Ctx.Services.Get<ILaneScheduler>();
        if (scheduler is null)
        {
            run.Model = model;
            if (Info.Status != AgentStatus.Running) rt.SetStatus(state, AgentStatus.Running, null, keepActivity: true);
            return;
        }
        var pool = scheduler.ResolvePool(model);
        if (run.Lease is { IsReleased: false } lease && string.Equals(lease.PoolKey, pool, StringComparison.OrdinalIgnoreCase))
        {
            run.Model = model;
            return;
        }
        if (run.Lease is { } old)
        {
            run.Lease = null;
            old.Dispose();
        }
        run.Lease = await rt.AcquireLaneAsync(state, run, model, 0, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- tools and prompt

    private List<IAgentTool> ActiveTools()
    {
        var maxDepth = rt.IntSetting("agents.maxDepth", 3);
        var allow = Info.ToolAllowlist;
        var depth = Info.Depth;
        List<IAgentTool> all;
        try { all = [.. Ctx.Tools.All]; } catch { all = []; }
        return all.Where(t =>
        {
            var d = t.Definition;
            if (allow is not null && !allow.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) return false;
            if (depth >= maxDepth && d.Category == "agents" && d.Name != "agent_send") return false;
            return true;
        }).ToList();
    }

    private async Task<string> BuildPromptAsync(SessionInfo session, ProjectInfo? project, string cwd, ModelInfo model,
        IReadOnlyList<ToolDefinition> defs, CancellationToken ct)
    {
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
        return FallbackPrompt(pc);
    }

    internal static string FallbackPrompt(PromptContext pc)
    {
        var sb = new StringBuilder();
        sb.Append("You are an expert coding agent running in NetPI, a minimal agent harness on the user's machine. ");
        sb.Append("Be concise. Act, don't just describe: use your tools to do the work and check the result.\n\n");
        sb.Append("Date: ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz")).Append('\n');
        sb.Append("OS: ").Append(RuntimeInformation.OSDescription).Append('\n');
        sb.Append("Working directory: ").Append(pc.Cwd).Append('\n');
        sb.Append("Project: ").Append(pc.Project is { } p ? $"{p.Name} ({p.Path})" : "none").Append('\n');
        sb.Append("Model: ").Append(pc.Model.Ref);
        if (!string.IsNullOrWhiteSpace(pc.Instructions)) sb.Append("\n\n").Append(pc.Instructions.Trim());
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
            Model = model,
            SystemPrompt = turn.SystemPrompt,
            Messages = turn.Messages,
            Tools = turn.Tools,
            ReasoningEffort = session.Reasoning,
            MaxOutputTokens = model.MaxOutputTokens ?? rt.IntSetting("agent.defaultMaxOutputTokens", 16384),
            SessionId = SessionId,
            AgentId = AgentId,
            Purpose = "agent",
        };

        var partial = new PartialMessage();
        var sw = Stopwatch.StartNew();
        long? thinkStart = null, thinkEnd = null;
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
                        thinkStart ??= sw.ElapsedMilliseconds;
                        partial.Add<ThinkingPart>(d.Text);
                        emitter.Delta("thinking", d.Text);
                        rt.SetActivity(state, "thinking");
                        break;
                    case TextDelta d:
                        EndThinking();
                        partial.Add<TextPart>(d.Text);
                        emitter.Delta("text", d.Text);
                        rt.SetActivity(state, "writing");
                        break;
                    case ToolCallStarted t:
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
        try { Ctx.Sessions.UpdateMessage(replacement); }
        catch (Exception ex) { Ctx.Logger.LogWarning(ex, "Failed to update the replaced assistant message"); }
        if (!string.IsNullOrWhiteSpace(replacement.Text)) run.LastAssistantText = replacement.Text;
        return replacement;
    }

    // ---------------------------------------------------------------- tool execution

    private sealed class PreparedCall
    {
        public required ToolCallPart Call { get; init; }
        public IAgentTool? Tool { get; init; }
        public JsonElement Args { get; set; }
        public ToolResult? Early { get; set; }
        public Stopwatch Watch { get; } = Stopwatch.StartNew();
    }

    private static IAgentTool? FindTool(IReadOnlyList<IAgentTool> tools, string name) =>
        tools.FirstOrDefault(t => t.Definition.Name == name)
        ?? tools.FirstOrDefault(t => string.Equals(t.Definition.Name, name, StringComparison.OrdinalIgnoreCase));

    private async Task ExecuteToolsAsync(ChatMessage assistant, AgentTurnContext turn, List<ToolCallPart> calls, List<IAgentTool> tools, CancellationToken ct)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var started = new HashSet<string>(StringComparer.Ordinal);
        var argsChanged = false;
        var parallel = calls.Count > 1
                       && rt.BoolSetting("agent.parallelReadOnlyTools", true)
                       && calls.All(c => FindTool(tools, c.Name)?.Definition.ReadOnly == true);
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
                        await FinishAsync(turn, p, result, ct).ConfigureAwait(false);
                        done.Add(p.Call.Id);
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
                    await FinishAsync(turn, p, result, ct).ConfigureAwait(false);
                    done.Add(calls[i].Id);

                    if (i < calls.Count - 1 && HasSteering())
                    {
                        for (var j = i + 1; j < calls.Count; j++)
                        {
                            PersistResult(calls[j], SkippedBySteering, isError: true, publishEnd: false);
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
                if (!done.Contains(c.Id)) PersistResult(c, NotExecutedAbort, isError: true, publishEnd: started.Contains(c.Id));
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

    private async Task<(PreparedCall Call, bool ArgsChanged)> PrepareAsync(AgentTurnContext turn, ToolCallPart call, List<IAgentTool> tools, CancellationToken ct)
    {
        var tool = FindTool(tools, call.Name);
        rt.Emit(EventTypes.ToolStart, new JsonObject
        {
            ["sessionId"] = SessionId,
            ["agentId"] = AgentId,
            ["callId"] = call.Id,
            ["name"] = call.Name,
            ["label"] = tool?.Definition.Label ?? call.Name,
            ["arguments"] = call.Arguments,
        }, SessionId);
        rt.SetActivity(state, $"tool: {call.Name}");

        var prepared = new PreparedCall { Call = call, Tool = tool };
        var changed = false;

        ToolCallDecision? decision = null;
        foreach (var hook in rt.Hooks())
        {
            decision = await SafeAsync(() => hook.OnBeforeToolCallAsync(turn, call), "OnBeforeToolCall", ct).ConfigureAwait(false);
            if (decision is not null) break;
        }
        if (decision?.Block == true)
        {
            prepared.Early = ToolResult.Error("Blocked: " + (string.IsNullOrWhiteSpace(decision.Reason) ? "this tool call was blocked by a policy hook." : decision.Reason));
            return (prepared, false);
        }
        if (decision?.Arguments is { } newArgs && newArgs != call.Arguments)
        {
            call.Arguments = newArgs;
            changed = true;
        }
        if (tool is null)
        {
            var names = string.Join(", ", tools.Select(t => t.Definition.Name));
            prepared.Early = ToolResult.Error($"Unknown tool '{call.Name}'. Available tools: {(names.Length == 0 ? "(none)" : names)}.");
            return (prepared, changed);
        }
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            prepared.Args = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            prepared.Early = ToolResult.Error($"Invalid JSON arguments for {call.Name}: {ex.Message} Send the arguments as a single valid JSON object that matches the tool's schema.");
        }
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
            var context = new ToolContext
            {
                SessionId = SessionId,
                AgentId = AgentId,
                CallId = p.Call.Id,
                Cwd = _rc!.Cwd,
                Project = _rc.Project,
                Model = _rc.Model,
                Services = Ctx.Services,
                Events = Ctx.Events,
                Output = output.Write,
            };
            try
            {
                result = await p.Tool.ExecuteAsync(context, p.Args, ct).ConfigureAwait(false) ?? ToolResult.Error("The tool returned no result.");
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
        return new ToolResultPart
        {
            CallId = p.Call.Id,
            Name = p.Call.Name,
            Content = TruncateResult(result.Content ?? ""),
            IsError = result.IsError,
            Images = result.Images,
            Details = details,
            DurationMs = p.Watch.ElapsedMilliseconds,
        };
    }

    private string TruncateResult(string content)
    {
        var max = rt.IntSetting("agent.maxToolResultChars", 60_000);
        if (max <= 0 || content.Length <= max) return content;
        var head = max * 2 / 3;
        if (head > 0 && char.IsHighSurrogate(content[head - 1])) head--;
        var tail = max - head;
        if (tail > 0 && char.IsLowSurrogate(content[^tail])) tail--;
        return content[..head]
               + $"\n\n[... {content.Length - head - tail:N0} characters omitted: the result had {content.Length:N0} characters, more than the limit of {max:N0} (setting agent.maxToolResultChars). Narrow the request (offset/limit, a more specific pattern, head/tail) to see the omitted part. ...]\n\n"
               + content[^tail..];
    }

    private async Task FinishAsync(AgentTurnContext turn, PreparedCall p, ToolResultPart result, CancellationToken ct)
    {
        Persist(result);
        rt.Emit(EventTypes.ToolEnd, new JsonObject
        {
            ["sessionId"] = SessionId,
            ["callId"] = p.Call.Id,
            ["name"] = p.Call.Name,
            ["isError"] = result.IsError,
            ["durationMs"] = result.DurationMs,
        }, SessionId);
        rt.Update(state, i => i.ToolCalls++);
        if (_rc is not null) _rc.ToolCallCount++;
        rt.PublishStatus(state, throttled: true);
        foreach (var hook in rt.Hooks())
            await SafeAsync(() => hook.OnAfterToolCallAsync(turn, p.Call, result), "OnAfterToolCall", ct).ConfigureAwait(false);
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

    /// <summary>Persist a synthetic result for a call that did not run (skipped/stopped/aborted).</summary>
    private void PersistResult(ToolCallPart call, string content, bool isError, bool publishEnd)
    {
        Persist(new ToolResultPart { CallId = call.Id, Name = call.Name, Content = content, IsError = isError, DurationMs = 0 });
        if (publishEnd)
            rt.Emit(EventTypes.ToolEnd, new JsonObject
            {
                ["sessionId"] = SessionId,
                ["callId"] = call.Id,
                ["name"] = call.Name,
                ["isError"] = isError,
                ["durationMs"] = 0,
            }, SessionId);
    }
}
