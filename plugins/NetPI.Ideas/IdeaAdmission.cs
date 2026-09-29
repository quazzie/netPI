using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// The Ideas checks ask for model work in the background — while the user is typing, while a commit sweep runs, while a
/// chat closes — and that work lands on the same local model (or provider) the chats are using. The scheduler is what
/// knows how many of those the backend actually serves, so every generative call and every decision goes through it
/// instead of through the decision plugin's own semaphore, which only counts the decision plugin's callers.
/// <para>
/// Two rules make this safe:
/// <list type="bullet">
/// <item>Background work waits for a slot, but never long: a check that arrives from inside an agent run (the agent
/// holds the last slot and is waiting for this answer) must not wait for a slot that only that agent can release.
/// After <see cref="MaxWait"/> the call goes ahead without admission and the log says so — a small overshoot is
/// better than a run that never finishes.</item>
/// <item>Nothing is sent to a paid model on its own. The checks are automatic; an invoice for a background check is
/// never what the user meant, so a non-local model is skipped unless <c>ideas.allowPaidModel</c> says otherwise.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class IdeaAdmission(IPluginContext ctx)
{
    /// <summary>How long background work waits for a slot before it runs anyway (see the remarks above).</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(2);
    /// <summary>Background work queues behind the chats, never in front of them.</summary>
    public const int Priority = -10;
    public const string AgentId = "ideas";

    private sealed class Noop : IDisposable { public void Dispose() { } }

    /// <summary>
    /// A slot for one call, released when it is disposed — or <c>null</c>, which means <b>do not make this call at
    /// all</b>: the model is not in the catalog, or it is a paid one and <c>ideas.allowPaidModel</c> is off. A no-op
    /// lease comes back when there is no scheduler (without the Agents plugin) or when the wait ran out.
    /// </summary>
    public async Task<IDisposable?> EnterAsync(ModelInfo? model, string purpose, string? sessionId, string? projectId, CancellationToken ct)
    {
        if (model is null) return null;
        if (!model.IsLocal && !PaidIsAllowed())
        {
            ctx.Logger.LogWarning("Ideas: {Purpose} did not run: {Model} is a paid model and ideas.allowPaidModel is off", purpose, model.Ref);
            return null;
        }
        var scheduler = ctx.Services.Get<IAgentScheduler>();
        if (scheduler is null) return new Noop(); // no scheduler: nothing to be admitted to (and nothing to starve)

        var sw = Stopwatch.StartNew();
        var key = scheduler.Resolve(model);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(MaxWait);
        try
        {
            var lease = await scheduler.AcquireAsync(new AgentSlotRequest
            {
                Key = key,
                AgentId = AgentId,
                SessionId = sessionId,
                Provider = model.Provider,
                Label = $"{purpose}{(projectId is { Length: > 0 } ? $" · project {projectId}" : "")}",
                Priority = Priority,
            }, bounded.Token).ConfigureAwait(false);
            ctx.Logger.LogDebug("Ideas: {Purpose} took the {Key} slot after {Ms} ms of queueing", purpose, key, sw.ElapsedMilliseconds);
            return new Leased(lease, ctx, purpose, key, sw);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Waiting longer would block the run that is waiting for this answer (or the user's chat behind it).
            ctx.Logger.LogWarning("Ideas: {Purpose} ran without a slot after {Ms} ms ({Key} is busy): the backend is carrying it as well as the chats",
                purpose, sw.ElapsedMilliseconds, key);
            return new Noop();
        }
    }

    private bool PaidIsAllowed()
    {
        try { return ctx.Settings.Get("ideas.allowPaidModel", false); }
        catch { return false; }
    }

    private sealed class Leased(IAgentSlot lease, IPluginContext ctx, string purpose, string key, Stopwatch sw) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lease.Dispose();
            ctx.Logger.LogDebug("Ideas: {Purpose} held the {Key} slot for {Ms} ms", purpose, key, sw.ElapsedMilliseconds);
        }
    }
}
