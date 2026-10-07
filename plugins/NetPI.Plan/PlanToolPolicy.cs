namespace NetPI.Plan;

/// <summary>
/// Plan mode for a model that runs tools of its own (<see cref="ModelInfo.RunsOwnTools"/>, the Claude CLI). <see cref="PlanHook"/>
/// holds NetPI's tools at the call, a call such a model never makes: its own Edit or Bash ran unchecked while the chat was in plan
/// mode, and a "read-only" research subagent on it got every tool (idea-hghbmg). This middleware marks the agent call of a chat that
/// may only read (<see cref="ModelRequest.ReadOnlyTools"/>), and the provider keeps its own tools to the reading ones.
/// </summary>
internal sealed class PlanToolPolicy(IPluginContext ctx) : IModelMiddleware
{
    /// <summary>Outermost: the mark is on the request before retries and the ledger see it, and a retried attempt keeps it.</summary>
    public int Order => -1000;

    public IAsyncEnumerable<ModelStreamEvent> InvokeAsync(ModelRequest request, ModelCallDelegate next, CancellationToken ct)
    {
        if (!request.ReadOnlyTools && request.Model.RunsOwnTools && request.Purpose == "agent" && request.SessionId is { Length: > 0 } sid
            && ctx.Sessions.GetSession(sid) is { } session
            && PlanHook.Bound(ctx, session, session.Kind == "subagent", session.ParentSessionId))
            request.ReadOnlyTools = true;
        return next(request, ct);
    }
}
