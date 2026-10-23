namespace NetPI;

/// <summary>The model a session actually uses: its own model, its named agent's model, then the catalog default.</summary>
public static class SessionModel
{
    public static async Task<string?> ResolveRefAsync(SessionInfo? session, IModelCatalog models, ISettings settings,
        IAgentScheduler? scheduler = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Clean(session?.Model) is { } stored) return stored;
        if (SessionAgent.Of(session) is { } agent)
        {
            // Ask the capability first; an alternate scheduler need not use the settings document.
            var configured = scheduler?.Snapshot().FirstOrDefault(p => p.Configured &&
                string.Equals(p.Key, agent, StringComparison.OrdinalIgnoreCase));
            if (Clean(configured?.Model) is { } selected) return selected;
            if (!agent.Contains('.'))
            {
                try { if (Clean(settings.Get<string>($"agents.{agent}.model")) is { } fallback) return fallback; }
                catch { /* A malformed optional agent setting does not hide the catalog default. */ }
            }
        }
        if (Clean(models.DefaultModelRef) is { } defaultRef) return defaultRef;
        // A provider may not have populated the catalog yet, particularly just after startup/reload.
        await models.ListAsync(refresh: models.Cached.Count == 0, ct).ConfigureAwait(false);
        return Clean(models.DefaultModelRef);
    }

    /// <summary>Why an unattended run (a schedule, a webhook) may not start on this chat, or null. A model that works with its
    /// own tools (the Claude CLI) runs them inside its provider: the autonomy brakes, the taint block and the undo copies never see
    /// them, so nobody is there to answer for what it does (idea-hghbmg, the same refusal as goal.set).</summary>
    public static async Task<string?> UnattendedRefusalAsync(string sessionId, ISessionStore sessions, IModelCatalog models, ISettings settings,
        IAgentScheduler? scheduler = null, CancellationToken ct = default)
    {
        if (sessions.GetSession(sessionId) is not { } session) return null;
        var modelRef = await ResolveRefAsync(session, models, settings, scheduler, ct).ConfigureAwait(false);
        if (modelRef is null || await models.FindAsync(modelRef, ct).ConfigureAwait(false) is not { RunsOwnTools: true }) return null;
        return $"the model ({modelRef}) works with its own tools, which the autonomy brakes cannot hold: pick another agent or model for an unattended run";
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
