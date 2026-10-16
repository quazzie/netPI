using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Runtime;

/// <summary>
/// One tool batch of a turn: the calls the model asked for, prepared (a hook may rewrite the arguments), invoked and
/// finished — and what a steer, an abort or a stop does to the calls that never ran.
/// </summary>
internal sealed partial class ToolBatch(AgentRuntime rt, AgentState state, AgentRunContext rc, Func<bool> hasSteering)
{
    /// <summary>
    /// The message for arguments that are not JSON. A call that ends inside an object or array was almost always cut off
    /// (the model ran out of output): say so, so the model shortens the call instead of resending it as it was.
    /// </summary>
    internal static string InvalidJson(string tool, JsonException ex)
    {
        var text = $"Invalid JSON arguments for {tool}: {ex.Message}";
        var cutOff = ex.Message.Contains("depth to be zero", StringComparison.Ordinal) || ex.Message.Contains("end of data", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("end of the string", StringComparison.OrdinalIgnoreCase);
        return cutOff
            ? text + "\nThe call looks cut off before its end (an open object or array): it was probably too long to write in one go. "
              + "Send less per call: fewer items, or long texts in a file (agent_spawn: taskFile) instead of inline."
            : text;
    }

    private IPluginContext Ctx => rt.Ctx;
    private string SessionId => state.Info.SessionId;
    private string AgentId => state.Info.Id;
    // for the nested calls (ToolBatch.Nested.cs): a primary constructor's parameters are not members
    private AgentRuntime Rt => rt;
    private AgentState State => state;
    private AgentRunContext Rc => rc;

    /// <summary>The <c>call_tool</c> calls of this batch by call id: the runner works on the inner call, the transcript keeps the outer.</summary>
    private readonly Dictionary<string, ToolCallPart> _outer = new(StringComparer.Ordinal);

    /// <summary>
    /// The batch a run goes on with after the user answered (<see cref="RunState.Resume"/>): the answers by call id. An
    /// answered call's result is its answer (no hook, no tool runs again); a call allowed to run goes through the hooks as
    /// usual, the one that asked reading <see cref="AgentTurnContext.AllowedCalls"/>.
    /// </summary>
    public IReadOnlyDictionary<string, WaitResolution> Resolved { get; init; } = new Dictionary<string, WaitResolution>();

    /// <summary>The batch stopped at calls that wait for the user: the run ends here (<see cref="AgentState.Pending"/>).</summary>
    public bool Waiting { get; private set; }
    private string ResultName(ToolCallPart call) => _outer.TryGetValue(call.Id, out var o) ? o.Name : call.Name;

    private List<IAgentTool> ActiveTools(SessionInfo session) => rt.ToolsFor(state.Info, session);

    private sealed class PreparedCall
    {
        public required ToolCallPart Call { get; init; }
        public IAgentTool? Tool { get; set; }
        public JsonElement Args { get; set; }
        public ToolResult? Early { get; set; }
        /// <summary>The call waits for the user: its tool returned a wait, or a hook asked before it ran.</summary>
        public UserWait? Wait { get; set; }
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
        if (tool is null) return false;
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
        // call_tool { name, arguments } is unwrapped here, before any hook sees the call: everything downstream — policy hooks,
        // the guard, parallelism, events — works on the inner tool, and its result is stored under the outer call's id
        calls = [.. calls.Select(c =>
        {
            if (CallTool.TryUnwrap(c, tools) is not { } inner) return c;
            _outer[c.Id] = c;
            return inner;
        })];
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
                    ToolResultPart? result;
                    await slots.WaitAsync(ct).ConfigureAwait(false);
                    try { result = await InvokeAsync(turn, assistant, p, ct).ConfigureAwait(false); }
                    finally { slots.Release(); }
                    if (result is null) return;   // it waits for the user: no result until they answer
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
                    var result = await InvokeAsync(turn, assistant, p, ct).ConfigureAwait(false);
                    // it waits for the user: the batch stops here, and the calls after it run once they answered
                    if (result is null) break;
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
            rt.DropPending(state, assistant.Id, "cancelled");   // a wait it registered: its call has the abort marker now
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
        // calls without a result now wait for the user, or come after one that does: the run ends, and goes on from here
        if (rt.HasPending(state, assistant.Id))
        {
            rt.StopPending(state, assistant.Id, [.. calls.Where(c => !done.Contains(c.Id)).Select(c => new OpenCall(c.Id, ResultName(c)))]);
            Waiting = true;
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
        return p.Tool is not null && eligible.Any(t => ReferenceEquals(t, p.Tool));
    }

    private async Task<(PreparedCall Call, bool ArgsChanged)> PrepareAsync(AgentTurnContext turn, ToolCallPart call, List<IAgentTool> tools, CancellationToken ct)
    {
        var tool = FindTool(tools, call.Name);
        var prepared = new PreparedCall { Call = call, Tool = tool };
        // answered while it waited: that is its result (it was shown as started then, and nothing runs again)
        if (Resolved.TryGetValue(call.Id, out var answered) && answered.Result is { } answer)
        {
            prepared.Early = answer;
            return (prepared, false);
        }
        var changed = false;
        var original = call.Arguments;
        try
        {
            var session = Ctx.Sessions.GetSession(SessionId) ?? rc.Session;
            var exposure = ToolExposure.For(Ctx.Settings, state.Info, session);
            if (tool is null) prepared.Early = ToolResult.Error($"Unknown tool '{call.Name}'. Available tools: {string.Join(", ", ToolSelection.Visible(tools, exposure).Select(t => t.Name))}.");
            else
            {
                prepared.Args = ParseArgs(call.Arguments);
                if (ToolHelp.Asked(tool.Definition, prepared.Args))
                    prepared.Early = ToolResult.Ok(ToolHelp.Text(tool.Definition));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (JsonException ex) { prepared.Early = ToolResult.Error(InvalidJson(call.Name, ex)); }
        catch (Exception ex) { prepared.Early = ToolResult.Error(ex.Message); }

        rt.Emit(EventTypes.ToolStart, new JsonObject
        {
            ["sessionId"] = SessionId, ["agentId"] = AgentId, ["callId"] = call.Id, ["name"] = call.Name,
            ["label"] = prepared.Tool?.Definition.Label ?? call.Name, ["arguments"] = call.Arguments,
        }, SessionId);
        rt.SetActivity(state, $"tool: {call.Name}");
        if (prepared.Early is not null) return (prepared, false);
        if (!StillEligible(prepared)) { prepared.Early = ToolResult.Error("The tool was disabled or replaced before execution."); return (prepared, false); }

        foreach (var hook in rt.Hooks())
        {
            // this hook is a gate: one that throws blocks the call (the others are logged and skipped)
            ToolCallDecision? decision;
            try { decision = await hook.OnBeforeToolCallAsync(turn, call).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var hookName = hook.GetType().Name;
                Ctx.Logger.LogWarning(ex, "Agent hook {Hook} failed before tool call {Tool}: the call is blocked", hookName, call.Name);
                call.Arguments = original;
                prepared.Early = ToolResult.Error($"Blocked: the policy hook {hookName} failed before the call ({ex.Message}), so '{call.Name}' was not run.");
                return (prepared, false);
            }
            if (decision is null) continue;
            if (decision.Block)
            {
                call.Arguments = original;
                prepared.Early = ToolResult.Error("Blocked: " + (decision.Reason ?? "this tool call was blocked by a policy hook."));
                return (prepared, false);
            }
            if (decision.Wait is { } wait)
            {
                // the hook asks the user before the call runs: it waits, with the arguments the hooks before it settled on
                prepared.Wait = wait;
                return (prepared, changed);
            }
            if (decision.Arguments is { } newArgs && newArgs != call.Arguments)
            {
                call.Arguments = newArgs;
                changed = true;
            }
        }
        if (!call.Name.Equals(prepared.Tool!.Definition.Name, StringComparison.OrdinalIgnoreCase) || !StillEligible(prepared))
        { prepared.Early = ToolResult.Error("The tool changed or was disabled during policy checks."); return (prepared, changed); }
        try
        {
            prepared.Args = ParseArgs(call.Arguments);
            if (ToolHelp.Asked(prepared.Tool.Definition, prepared.Args))
                prepared.Early = ToolResult.Ok(ToolHelp.Text(prepared.Tool.Definition));
        }
        catch (JsonException ex) { prepared.Early = ToolResult.Error(InvalidJson(call.Name, ex)); }
        return (prepared, changed);
    }

    /// <summary>Run the call. Null when it waits for the user (<see cref="UserWait"/>): registered with the agent, no result yet.</summary>
    private async Task<ToolResultPart?> InvokeAsync(AgentTurnContext turn, ChatMessage assistant, PreparedCall p, CancellationToken ct)
    {
        ToolResult result;
        if (p.Wait is not null)
        {
            if (Wait(assistant, p) is not { } refused) return null;
            result = refused;
        }
        else if (p.Early is not null || p.Tool is null)
        {
            result = p.Early ?? ToolResult.Error("Unknown tool.");
        }
        else
        {
            ct.ThrowIfCancellationRequested(); // never start a tool for a run that was aborted meanwhile
            using var output = new ToolOutputEmitter(Ctx.Events, SessionId, p.Call.Id);
            var context = CallContext(p.Call.Id, output.Write);
            // a tool may call tools through the same pipeline (a codemode script); its nested calls get no such service
            context.Features.Set<INestedToolCalls>(new NestedCalls(this, turn, p));
            try
            {
                if (!StillEligible(p)) result = ToolResult.Error("The tool was disabled or replaced before execution.");
                else result = await p.Tool.ExecuteAsync(context, p.Args, ct).ConfigureAwait(false) ?? ToolResult.Error("The tool returned no result.");
                if (result.Wait is { } wait)
                {
                    p.Wait = wait;
                    if (Wait(assistant, p) is not { } refused) return null;
                    result = refused;
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
        return new ToolResultPart
        {
            CallId = p.Call.Id,
            Name = ResultName(p.Call),
            Content = LimitResult(result.Content ?? "", p.Call),
            IsError = result.IsError,
            Images = result.Images,
            Details = details,
            DurationMs = p.Watch.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// The call waits for the user: registered with the agent (answerable from now on), null. A subagent cannot wait
    /// (nobody watches its chat): the error result it gets instead.
    /// </summary>
    private ToolResult? Wait(ChatMessage assistant, PreparedCall p)
    {
        if (state.Info.IsSubagent)
            return ToolResult.Error($"{p.Call.Name} waits for the user, and a subagent can't: nobody watches its chat. Put what you need from them in your report instead.");
        rt.AddWait(state, assistant.Id, p.Call, p.Wait!);
        return null;
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
        }, SessionId);
        rt.Update(state, i => i.ToolCalls++);
        rc.ToolCallCount++;
        rt.PublishStatus(state, throttled: true);
        foreach (var hook in rt.Hooks())
            await AgentRunner.SafeAsync(Ctx, () => hook.OnAfterToolCallAsync(turn, p.Call, result), "OnAfterToolCall", ct).ConfigureAwait(false);
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

    /// <summary>
    /// Calls the provider ran itself (<see cref="ToolCallPart.ProviderExecuted"/>, the Claude CLI's own tools) come with their results in
    /// <see cref="MessagePart.ProviderData"/>: they are shown and stored like any tool call (start and end events, a tool result message)
    /// and never executed here. The result leaves the assistant message, so it is stored once. The after-call hooks see each one (under
    /// the NetPI tool it is the equivalent of, <see cref="ToolCallPart.ProviderAs"/>), so what watches a run's tool use (a goal's
    /// progress) sees this work too (idea-hghbmg); nothing is left to allow or block, so the before-call hooks are not asked.
    /// </summary>
    public async Task RecordProviderExecutedAsync(ChatMessage assistant, AgentTurnContext turn, IReadOnlyList<ToolCallPart> calls, CancellationToken ct)
    {
        var recorded = new List<(ToolCallPart Call, ToolResultPart Result)>(calls.Count);
        foreach (var call in calls)
        {
            var res = call.ProviderData?[ToolCallPart.ResultKey] as JsonObject;
            var content = res?["content"] is JsonValue c && c.TryGetValue<string>(out var text) ? text : "(the provider recorded no result for this call)";
            var isError = res is null || (res["isError"] is JsonValue e && e.TryGetValue<bool>(out var flag) && flag);
            // reported live while the provider worked (ProviderToolRunning/Done): only a call it never finished needs its end here
            var live = call.ProviderLive;
            if (!live)
                rt.Emit(EventTypes.ToolStart, new JsonObject
                {
                    ["sessionId"] = SessionId, ["agentId"] = AgentId, ["callId"] = call.Id, ["name"] = call.Name,
                    ["label"] = call.Name, ["arguments"] = call.Arguments,
                }, SessionId);
            var result = new ToolResultPart
            {
                CallId = call.Id, Name = call.Name, Content = content, IsError = isError,
                Details = new JsonObject { ["providerExecuted"] = true },
            };
            Persist(result, required: false);
            if (!live || res is null)
                rt.Emit(EventTypes.ToolEnd, new JsonObject
                {
                    ["sessionId"] = SessionId, ["callId"] = call.Id, ["name"] = call.Name, ["isError"] = isError,
                }, SessionId);
            rt.Update(state, i => i.ToolCalls++);
            rc.ToolCallCount++;
            call.ProviderData?.Remove(ToolCallPart.ResultKey);
            recorded.Add((call, result));
        }
        try { Ctx.Sessions.UpdateMessage(assistant); }
        catch (Exception ex) { Ctx.Logger.LogDebug(ex, "Failed to update the message of provider-executed calls"); }
        rt.PublishStatus(state, throttled: true);
        foreach (var (call, result) in recorded)
        {
            // the hooks read the call as the NetPI tool it corresponds to (the CLI's Bash as bash); the flag stays on it
            var seen = call.ProviderAs is { } name
                ? new ToolCallPart { Id = call.Id, Name = name, Arguments = call.Arguments, ProviderData = call.ProviderData }
                : call;
            foreach (var hook in rt.Hooks())
                await AgentRunner.SafeAsync(Ctx, () => hook.OnAfterToolCallAsync(turn, seen, result), "OnAfterToolCall", ct).ConfigureAwait(false);
        }
    }

    /// <summary>Persist a result for a call that did not (fully) run. <paramref name="skipped"/> (steer | aborted | stopped)
    /// is stored as <c>details.skipped</c> so the UI can show it as skipped rather than as a failure.</summary>
    public void PersistResult(ToolCallPart call, string content, bool isError, bool publishEnd, string? skipped = null)
    {
        Persist(new ToolResultPart
        {
            CallId = call.Id, Name = ResultName(call), Content = content, IsError = isError, DurationMs = 0,
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