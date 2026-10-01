namespace NetPI.Ideas;

/// <summary>Backlog checks wait for the working agent, including a queued or yielded run.</summary>
internal static class IdeaRuns
{
    public static bool Active(AgentInfo? agent) => agent?.Status is AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded;

    public static bool SessionBusy(IPluginContext ctx, string sessionId) =>
        Active(ctx.Services.Get<IAgentRuntime>()?.GetBySession(sessionId));

    public static bool ProjectBusy(IPluginContext ctx, string? projectId) =>
        ctx.Services.Get<IAgentRuntime>()?.List().Any(a => Active(a) &&
            ctx.Sessions.GetSession(a.SessionId) is { } s && s.ProjectId == projectId) == true;
}
