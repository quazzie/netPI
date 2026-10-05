using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Runtime;

/// <summary>
/// One tool batch of a turn: the calls the model asked for, prepared (a gateway resolved, a hook may rewrite the
/// arguments), invoked and finished — and what a steer, an abort or a stop does to the calls that never ran.
/// </summary>
internal sealed class ToolBatch(AgentRuntime rt, AgentState state, AgentRunContext rc, Func<bool> hasSteering)
{
    private IPluginContext Ctx => rt.Ctx;
    private string SessionId => state.Info.SessionId;
    private string AgentId => state.Info.Id;

    private List<IAgentTool> ActiveTools(SessionInfo session) => rt.ToolsFor(state.Info, session);

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

    /// <summary>
    /// The arguments a tool (or a hook) reads: some models send the object as a JSON <em>string</em>, unwrapped here
    /// once for every call — the tools and the policy hooks see the same object the runner hands the tool, and the
    /// stored <c>ToolCallPart.Arguments</c> is never rewritten for it.
    /// </summary>
    private static JsonElement ParseArgs(string? arguments)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        return ToolArgs.Unwrap(doc.RootElement.Clone());
    }

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

    /// <summary>Prepare, invoke and finish every call of the turn (read-only ones in parallel), then persist the results.</summary>
    public async Task RunAsync(ChatMessage assistant, AgentTurnContext turn, List<ToolCallPart> calls, List<IAgentTool> tools, CancellationToken ct)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var started = new HashSet<string>(StringComparer.Ordinal);
        var argsChanged = false;
        var parallel = calls.Count > 1
                       && rt.Ctx.Settings.GetBool("agent.parallelReadOnlyTools", true)
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

                    if (i < calls.Count - 1 && hasSteering())
                    {
                        for (var j = i + 1; j < calls.Count; j++)
                        {
                            PersistResult(calls[j], AgentRunner.SkippedBySteering, isError: true, publishEnd: false, skipped: "steer");
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
                if (!done.Contains(c.Id)) PersistResult(c, AgentRunner.NotExecutedAbort, isError: true, publishEnd: started.Contains(c.Id), skipped: "aborted");
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

    private ToolContext CallContext(string callId, Action<string>? output = null)
    {
        var context = new ToolContext
        {
            SessionId = SessionId, AgentId = AgentId, CallId = callId,
            Cwd = rc.Cwd, Project = rc.Project, Model = rc.Model,
            Services = Ctx.Services, Events = Ctx.Events, Output = output,
            EligibleTools = () => ActiveTools(Ctx.Sessions.GetSession(SessionId) ?? rc.Session),
        };
        context.Features.Set(rc.Workspace());
        context.Features.Set(rc.AdmissionLease());
        return context;
    }

    private bool StillEligible(PreparedCall p)
    {
        var eligible = ActiveTools(Ctx.Sessions.GetSession(SessionId) ?? rc.Session);
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
                prepared.Args = ParseArgs(call.Arguments);
                if (ToolHelp.Asked(tool.Definition, prepared.Args))
                    prepared.Early = ToolResult.Ok(ToolHelp.Text(tool.Definition));
                else if (tool is IIndirectAgentTool gateway)
                {
                    prepared.Gateway = gateway;
                    prepared.OriginalArguments = prepared.Args;
                    var resolved = await gateway.ResolveAsync(CallContext(call.Id), prepared.Args, ct).ConfigureAwait(false);
                    prepared.Tool = FindTool(ActiveTools(Ctx.Sessions.GetSession(SessionId) ?? rc.Session), resolved.ToolName);
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
            var decision = await AgentRunner.SafeAsync(Ctx, () => hook.OnBeforeToolCallAsync(turn, effective), "OnBeforeToolCall", ct).ConfigureAwait(false);
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
            prepared.Args = ParseArgs(effective.Arguments);
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
    private string LimitResult(string content, ToolCallPart call) =>
        ResultLimiter.Limit(content, rt.Ctx.Settings.GetInt(ToolResultLimit.Setting, ToolResultLimit.Default),
            c => rt.SaveToolResult(SessionId, call, c));

    private async Task FinishAsync(AgentTurnContext turn, PreparedCall p, ToolResultPart result, HashSet<string> done, CancellationToken ct)
    {
        Persist(result, required: true);
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
        rc.ToolCallCount++;
        rt.PublishStatus(state, throttled: true);
        foreach (var hook in rt.Hooks())
            await AgentRunner.SafeAsync(Ctx, () => hook.OnAfterToolCallAsync(turn, p.EffectiveCall ?? p.Call, result), "OnAfterToolCall", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Store a result. The result of a call that ran is <paramref name="required"/>: when the store refuses it, the run
    /// fails with the store's error in a notice, because a transcript without the result closes the call as "not
    /// executed" at the next model call (<see cref="ModelMessages.Normalize"/>), and the model runs a write or a shell
    /// command again that did run. A marker for a call that never ran (skipped, aborted, stopped) is best effort: the
    /// next request closes such a call the same way when the marker is missing.
    /// </summary>
    private void Persist(ToolResultPart result, bool required)
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
            if (!required)
            {
                Ctx.Logger.LogWarning(ex, "Failed to persist the result of {Tool}", result.Name);
                return;
            }
            Ctx.Logger.LogError(ex, "Failed to persist the result of {Tool}; the run stops", result.Name);
            var msg = $"The result of {result.Name} could not be stored ({ex.Message}). The run stopped here: the tool did run, " +
                      "and a transcript without its result would make the model run it again.";
            rt.AppendNotice(state, msg, "error");
            throw new RunFailedException(msg, ex);
        }
    }

    /// <summary>Persist a result for a call that did not (fully) run. <paramref name="skipped"/> (steer | aborted | stopped)
    /// is stored as <c>details.skipped</c> so the UI can show it as skipped rather than as a failure.</summary>
    public void PersistResult(ToolCallPart call, string content, bool isError, bool publishEnd, string? skipped = null)
    {
        Persist(new ToolResultPart
        {
            CallId = call.Id, Name = call.Name, Content = content, IsError = isError, DurationMs = 0,
            Details = skipped is null ? null : new JsonObject { ["skipped"] = skipped },
        }, required: false);
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