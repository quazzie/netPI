using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Runtime;

/// <summary>One model call of a run: the request, its stream (events, activity, what an abort produced so far) and its cost.</summary>
internal sealed class ModelCall(AgentRuntime rt, AgentState state, RunState run)
{
    private IPluginContext Ctx => rt.Ctx;
    private string SessionId => state.Info.SessionId;
    private string AgentId => state.Info.Id;

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

    private sealed class ProviderAcknowledgement(IResourceLeases? physical, string? leaseId) : IDisposable
    {
        public void Dispose()
        {
            if (leaseId is not null) physical?.Update(leaseId, new ResourceLeaseUpdate { ProviderReturned = true });
        }
    }

    /// <summary>
    /// Streams one model call and persists what it produced. Returns the assistant message and the context size the
    /// provider reported (0 when it reported none, so the run keeps the last one it knows).
    /// </summary>
    public async Task<(ChatMessage Assistant, long ContextTokens)> RunAsync(AgentTurnContext turn, ModelInfo model,
        SessionInfo session, CancellationToken ct)
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
            MaxOutputTokens = OutputLimit.Model(model, Ctx.Settings),
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
        var contextTokens = 0L;

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
                contextTokens = used;
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
        return (final, contextTokens);
    }

    public ChatMessage ReplaceAssistant(ChatMessage original, ChatMessage replacement)
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
}