using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetPI.Plan;

/// <summary>
/// Plan mode's gate and its voice. Before every tool call of a chat in plan mode (and of the research subagents it
/// started) <see cref="PlanPolicy"/> decides: blocked calls never run, and never reach the guardrails' approvals (this hook
/// runs before them). An <c>agent_spawn</c> is allowed with its arguments rewritten: read-only tools, no checkout of its own.
/// Before every model call the chat is told once when it entered or left plan mode (a "plan" or "plan-off" notice, written
/// again if compaction removed it). The system prompt is frozen at a chat's first call, so a notice is the only way to say it.
/// </summary>
internal sealed class PlanHook(IPluginContext ctx) : IAgentHook
{
    /// <summary>Before the workspace guard (100) and the guardrails (10000): a call plan mode refuses has nothing left to ask.</summary>
    public int Order => -200;

    private const int MaxParents = 6;

    /// <summary>Whether the run is bound by plan mode: its chat is in it, or (a research subagent) the chat that started it is.</summary>
    private bool Bound(AgentRunContext run)
    {
        var session = ctx.Sessions.GetSession(run.Session.Id) ?? run.Session;
        if (PlanMode.Active(session)) return true;
        if (!run.Agent.IsSubagent || !ctx.Settings.Get("plan.subagentsReadOnly", true)) return false;
        var parent = session.ParentSessionId ?? run.Agent.ParentSessionId;
        for (var i = 0; i < MaxParents && parent is not null; i++)
        {
            var p = ctx.Sessions.GetSession(parent);
            if (PlanMode.Active(p)) return true;
            parent = p?.ParentSessionId;
        }
        return false;
    }

    public async ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call)
    {
        var run = turn.Run;
        if (!Bound(run)) return null;
        try
        {
            var tool = ctx.Tools.Get(call.Name);
            var args = Parse(call.Arguments);
            var readOnlyCall = tool is IReadOnlyCalls rc && args is { } a && rc.IsReadOnly(a);
            McpToolInfo? mcp = null;
            var allow = ctx.Settings.GetStrings("plan.mcpAllow");
            if (tool?.Definition is { Category: "mcp", ReadOnly: false } def && !PlanPolicy.McpGateways.Contains(def.Name))
                mcp = await McpInfoAsync(def.Name, run.CancellationToken).ConfigureAwait(false);
            if (PlanPolicy.Block(call.Name, tool?.Definition, readOnlyCall, mcp, allow) is { } reason) return new ToolCallDecision { Block = true, Reason = reason };
            if (call.Name == "agent_spawn" && ctx.Settings.Get("plan.subagentsReadOnly", true))
                return new ToolCallDecision { Arguments = PlanPolicy.RewriteSpawn(call.Arguments, PlanPolicy.ChildTools(ctx.Tools.All)) };
            return null;
        }
        catch (OperationCanceledException) when (run.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // fails closed: a call that cannot be checked while nothing may change does not run
            ctx.Logger.LogWarning(ex, "Plan mode could not check {Tool}", call.Name);
            return new ToolCallDecision { Block = true, Reason = $"plan mode could not check this call ({ex.Message}), so it did not run." };
        }
    }

    public ValueTask OnBeforeModelCallAsync(AgentTurnContext turn)
    {
        var run = turn.Run;
        if (run.Agent.IsSubagent) return ValueTask.CompletedTask;
        var active = PlanMode.Active(ctx.Sessions.GetSession(run.Session.Id) ?? run.Session);
        var last = LastKind(turn.Messages);
        if (active && last != PlanService.NoticeOn) return Tell(turn, PlanService.OnText, PlanService.NoticeOn);
        if (!active && last == PlanService.NoticeOn) return Tell(turn, PlanService.OffText, PlanService.NoticeOff);
        return ValueTask.CompletedTask;
    }

    private async ValueTask Tell(AgentTurnContext turn, string text, string kind)
    {
        ctx.Sessions.AppendMessage(turn.Run.Session.Id, ChatMessage.NoticeText(text, kind));
        await turn.ReloadMessagesAsync().ConfigureAwait(false);
    }

    /// <summary>The kind of the last plan notice in the context (plan | plan-off), or null.</summary>
    internal static string? LastKind(IReadOnlyList<ChatMessage> context) => context
        .Where(m => m.Role == MessageRole.Notice && m.MetaString("kind") is PlanService.NoticeOn or PlanService.NoticeOff)
        .Select(m => m.MetaString("kind")).LastOrDefault();

    private async Task<McpToolInfo?> McpInfoAsync(string id, CancellationToken ct)
    {
        if (!ctx.Rpc.Exists("mcp.tool")) return null;
        try
        {
            var info = NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("mcp.tool", new System.Text.Json.Nodes.JsonObject { ["id"] = id }, ct).ConfigureAwait(false));
            return info is null ? null : new McpToolInfo(info["serverId"]?.GetValue<string>() ?? "", info["name"]?.GetValue<string>() ?? "", info["readOnly"]?.GetValue<bool>() == true);
        }
        catch (RpcException) { return null; } // the tool went away: not known, so not allowed
    }

    private static JsonElement? Parse(string? arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; } // the tool refuses invalid JSON itself: nothing runs
    }
}
