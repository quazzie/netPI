using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// The Ideas checks ask for model work in the background — while the user is typing, while a commit sweep runs, while a
/// chat closes — and that work lands on the same local model (or provider) the chats are using. The scheduler is what
/// knows how many of those the backend actually serves, so every generative call and every decision goes through it
/// instead of through the decision plugin's own semaphore, which only counts the decision plugin's callers.
/// <para>
/// Admission is a <b>bound</b>, not a courtesy: work that cannot get a slot is <b>dropped</b> with a reason, never run
/// without one. Running it anyway is what made a sweep, a save check and a recall able to put three calls on a
/// two-slot model — exactly what admission exists to prevent. The work is not lost: the save check's mark stays
/// retryable (the next close of that conversation runs it again) and the commit sweep's cursor does not move past a
/// commit it did not read (the next sweep tries it again).
/// </para>
/// <para>
/// One thing is not dropped: a call to a <b>paid</b> model is never made on the checks' own initiative, whatever the
/// queue looks like — an invoice for a background check is never what the user meant, so
/// <c>ideas.allowPaidModel</c> decides.
/// </para>
/// </summary>
internal sealed class IdeaAdmission(IPluginContext ctx)
{
    /// <summary>How long a background check waits for a slot before it is dropped (seconds, <c>ideas.checkWaitSeconds</c>).</summary>
    public const int DefaultWaitSeconds = 30;
    /// <summary>
    /// The recall answers a keystroke, so it waits briefly and then simply does not suggest. It is the one interactive
    /// caller, and a long wait there would be a spinner in the composer, not a card.
    /// </summary>
    public static readonly TimeSpan InteractiveWait = TimeSpan.FromSeconds(2);
    /// <summary>Background work queues behind the chats, never in front of them.</summary>
    public const int Priority = -10;
    public const string AgentId = "ideas";

    private sealed class Noop : IDisposable { public void Dispose() { } }

    /// <summary>
    /// Whether the call may be made: a lease to dispose (admitted), or nothing and the reason why not. The two kinds of
    /// "no" are different and the callers treat them differently: <b>skip</b> is a decision (no model, or a paid one that
    /// is not allowed) that will not change on a retry, <b>drop</b> is the queue, and that work is still there to be done
    /// later.
    /// </summary>
    internal sealed record Admission(IDisposable? Lease, string? Reason, string Kind)
    {
        public const string HeldKind = "held";
        public const string SkipKind = "skip";
        public const string DropKind = "drop";
        public bool Admitted => Lease is not null;
        /// <summary>Dropped work may run again; skipped work would be skipped again.</summary>
        public bool Retryable => Kind == DropKind;
        public static Admission Skip(string reason) => new(null, reason, SkipKind);
        public static Admission Dropped(string reason) => new(null, reason, DropKind);
        public static Admission Held(IDisposable lease) => new(lease, null, HeldKind);
    }

    /// <summary>
    /// A slot for one call, released when it is disposed — or an <see cref="Admission"/> that is not: null means
    /// <b>do not make this call at all</b>. A no-op lease comes back when there is no scheduler (without the Agents
    /// plugin): there is nothing to be admitted to, and nothing to starve.
    /// </summary>
    public async Task<Admission> EnterAsync(ModelInfo? model, string purpose, string? sessionId, string? projectId,
        CancellationToken ct, TimeSpan? wait = null)
    {
        if (model is null) return Admission.Skip("no model");
        if (!model.IsLocal && !PaidIsAllowed())
        {
            ctx.Logger.LogWarning("Ideas: {Purpose} did not run: {Model} is a paid model and ideas.allowPaidModel is off", purpose, model.Ref);
            return Admission.Skip($"{model.Ref} is a paid model and ideas.allowPaidModel is off");
        }
        var scheduler = ctx.Services.Get<IAgentScheduler>();
        if (scheduler is null) return Admission.Held(new Noop());

        var bound = wait ?? TimeSpan.FromSeconds(Math.Clamp(WaitSeconds(), 1, 300));
        var sw = Stopwatch.StartNew();
        var key = scheduler.Resolve(model);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(bound);
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
            return Admission.Held(new Leased(lease, ctx, purpose, key, sw));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The queue did not open in time. Running the call anyway would put one more request on a backend that is
            // already serving its slots, so the work is dropped and says so: the check stays retryable, the commit
            // sweep keeps its cursor, and the log names the work and the reason.
            var reason = $"{key} was full for {bound.TotalSeconds:0} s";
            ctx.Logger.LogWarning("Ideas: {Purpose} was dropped after {Ms} ms of queueing ({Reason}): the model is serving its slots and this work did not fit. It runs again on the next sweep or the next close of that chat.",
                purpose, sw.ElapsedMilliseconds, reason);
            return Admission.Dropped(reason);
        }
    }

    private int WaitSeconds() => Setting("ideas.checkWaitSeconds", DefaultWaitSeconds);

    private bool PaidIsAllowed() => Setting("ideas.allowPaidModel", false);

    private T Setting<T>(string path, T fallback)
    {
        try { return ctx.Settings.Get(path, fallback) ?? fallback; } catch { return fallback; }
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
