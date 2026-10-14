using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetPI.Runtime;

internal sealed partial class ToolBatch
{
    /// <summary>Read-only nested calls of one tool running at once.</summary>
    internal const int NestedParallel = 4;

    /// <summary>
    /// The <see cref="INestedToolCalls"/> of one model-issued call: its nested calls are named <c>&lt;callId&gt;~1</c>,
    /// <c>~2</c>…, go through the before-call hooks (one at a time, as in a batch), run (calls that change something one at a
    /// time, read-only ones up to <see cref="NestedParallel"/> at once), and are shown to the after-call hooks.
    /// </summary>
    private sealed class NestedCalls(ToolBatch batch, AgentTurnContext turn, PreparedCall parent) : INestedToolCalls
    {
        private int _count;
        private readonly SemaphoreSlim _slots = new(NestedParallel);
        private readonly SemaphoreSlim _writes = new(1);
        private readonly SemaphoreSlim _hooks = new(1);

        public IReadOnlyList<ToolDefinition> Tools => [.. Candidates().Select(t => t.Definition)];

        private List<IAgentTool> Candidates() =>
            [.. batch.ActiveTools(batch.Ctx.Sessions.GetSession(batch.SessionId) ?? batch.Rc.Session)
                .Where(t => t is not IDeferredToolInfrastructure && !ReferenceEquals(t, parent.Tool))];

        public async Task<ToolResult> CallAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            var tool = FindTool(Candidates(), name);
            if (tool is null) return ToolResult.Error($"Unknown tool '{name}': it is not one of the tools this chat can call from here.");
            var id = parent.Call.Id + "~" + Interlocked.Increment(ref _count);
            JsonElement args;
            try { args = ToolArgs.Unwrap(arguments.Clone()); }
            catch (JsonException ex) { return ToolResult.Error(InvalidJson(name, ex)); }
            if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) args = JsonDocument.Parse("{}").RootElement.Clone();
            var call = new ToolCallPart { Id = id, Name = tool.Definition.Name, Arguments = args.GetRawText() };
            var readOnly = ReadsOnly(tool, call.Arguments);

            await _slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!readOnly) await _writes.WaitAsync(ct).ConfigureAwait(false);
                try { return await RunAsync(tool, call, ct).ConfigureAwait(false); }
                finally { if (!readOnly) _writes.Release(); }
            }
            finally { _slots.Release(); }
        }

        /// <summary>
        /// A nested call that would wait for the user (a question, a guardrail's OK): a script cannot stop and go on later,
        /// so it does not run. Made directly, the call waits like any other (<see cref="UserWait"/>).
        /// </summary>
        private static ToolResult CannotWait(string name) => ToolResult.Error(
            $"Not run: {name} waits for the user here (a question, or a guardrail that asks first), and a call made from inside a script cannot wait for them. Call {name} directly, not from the script.");

        private async Task<ToolResult> RunAsync(IAgentTool tool, ToolCallPart call, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            await _hooks.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var hook in batch.Rt.Hooks())
                {
                    var decision = await AgentRunner.SafeAsync(batch.Ctx, () => hook.OnBeforeToolCallAsync(turn, call), "OnBeforeToolCall", ct).ConfigureAwait(false);
                    if (decision is null) continue;
                    if (decision.Block) return ToolResult.Error("Blocked: " + (decision.Reason ?? "this tool call was blocked by a policy hook."));
                    if (decision.Wait is not null) return CannotWait(call.Name);
                    if (decision.Arguments is { } rewritten) call.Arguments = rewritten;
                }
            }
            finally { _hooks.Release(); }
            if (!Candidates().Any(t => ReferenceEquals(t, tool)))
                return ToolResult.Error("The tool was disabled or replaced before execution.");

            ToolResult result;
            try
            {
                var args = ParseArgs(call.Arguments);
                result = await tool.ExecuteAsync(batch.CallContext(call.Id), args, ct).ConfigureAwait(false) ?? ToolResult.Error("The tool returned no result.");
                if (result.Wait is not null) result = CannotWait(call.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (JsonException ex) { result = ToolResult.Error(InvalidJson(call.Name, ex)); }
            catch (Exception ex)
            {
                batch.Ctx.Logger.LogDebug(ex, "Nested tool {Tool} threw", call.Name);
                result = ToolResult.Error($"Tool '{call.Name}' failed: {ex.Message}");
            }

            var part = new ToolResultPart
            {
                CallId = call.Id, Name = call.Name, Content = result.Content ?? "", IsError = result.IsError, Images = result.Images,
                DurationMs = watch.ElapsedMilliseconds,
            };
            try { part.Details = result.Details is null ? null : NetPiJson.ToNode(result.Details); }
            catch (Exception ex) { batch.Ctx.Logger.LogDebug(ex, "Tool details of {Tool} are not serializable", call.Name); }
            batch.Rt.Update(batch.State, i => i.ToolCalls++);
            batch.Rc.ToolCallCount++;
            await _hooks.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                foreach (var hook in batch.Rt.Hooks())
                    await AgentRunner.SafeAsync(batch.Ctx, () => hook.OnAfterToolCallAsync(turn, call, part), "OnAfterToolCall", ct).ConfigureAwait(false);
            }
            finally { _hooks.Release(); }
            return result;
        }
    }
}
