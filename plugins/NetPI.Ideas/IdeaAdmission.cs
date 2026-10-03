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
/// without one. Running it anyway is what made a sweep and a save check able to put several calls on a
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
        public IAgentSlot? Slot => Lease is Leased held ? held.Slot : Lease as IAgentSlot;
        public void Report(string status, string? reason = null) { if (Lease is Leased held) held.Report(status, reason); }
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
        CancellationToken ct, TimeSpan? wait = null, bool decision = false)
    {
        var work = ctx.Services.Get<IBackgroundWork>();
        var workId = work?.Begin(purpose, model?.Ref, sessionId, projectId);
        if (model is null) { if (workId is not null) work!.Set(workId, "skipped", "no model"); return Admission.Skip("no model"); }
        if (!model.IsLocal && !PaidIsAllowed())
        {
            ctx.Logger.LogWarning("Ideas: {Purpose} did not run: {Model} is a paid model and ideas.allowPaidModel is off", purpose, model.Ref);
            var reason = $"{model.Ref} is a paid model and ideas.allowPaidModel is off";
            if (workId is not null) work!.Set(workId, "skipped", reason);
            return Admission.Skip(reason);
        }
        // A short decision on a local model goes on the server's decision lane (decide.lane): it does not queue behind
        // the chats' slots, so a check answers while two agents work and the sweep is not dropped for a full model.
        if (decision && model.IsLocal && ctx.Settings.GetOr("decide.lane", true))
        {
            if (workId is not null) work!.Set(workId, "running", "decision lane");
            return Admission.Held(new Leased(new Noop(), ctx, purpose, model.Ref, Stopwatch.StartNew(), work, workId));
        }
        var scheduler = ctx.Services.Get<IAgentScheduler>();
        if (scheduler is null)
        {
            using var fallbackBound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fallbackBound.CancelAfter(wait ?? TimeSpan.FromSeconds(Math.Clamp(WaitSeconds(), 1, 300)));
            try
            {
                IDisposable lease = await ResourceLeaseSlot.AcquireAsync(ctx.Services, ctx.Settings, model, new AgentSlotRequest
                    { Key = model.Ref, AgentId = AgentId, SessionId = sessionId, Label = purpose, Priority = Priority, Provider = model.Provider }, fallbackBound.Token).ConfigureAwait(false) ?? (IDisposable)new Noop();
                if (workId is not null) work!.Set(workId, "running", "Scheduler unavailable; shared physical admission is used when the host supports it");
                return Admission.Held(new Leased(lease, ctx, purpose, model.Ref, Stopwatch.StartNew(), work, workId));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (workId is not null) work!.Set(workId, "dropped", "Physical capacity did not open before the deadline");
                return Admission.Dropped("Physical capacity did not open before the deadline");
            }
            catch (OperationCanceledException) { if (workId is not null) work!.Set(workId, "cancelled", "The check was cancelled"); throw; }
            catch (Exception ex) { if (workId is not null) work!.Set(workId, "failed", ex.Message); throw; }
        }

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
            if (workId is not null) work!.Set(workId, "running");
            return Admission.Held(new Leased(lease, ctx, purpose, key, sw, work, workId));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The queue did not open in time. Running the call anyway would put one more request on a backend that is
            // already serving its slots, so the work is dropped and says so: the check stays retryable, the commit
            // sweep keeps its cursor, and the log names the work and the reason.
            var reason = $"{key} was full for {bound.TotalSeconds:0} s";
            if (workId is not null) work!.Set(workId, "dropped", reason);
            ctx.Logger.LogWarning("Ideas: {Purpose} was dropped after {Ms} ms of queueing ({Reason}): the model is serving its slots and this work did not fit. It runs again on the next sweep or the next close of that chat.",
                purpose, sw.ElapsedMilliseconds, reason);
            return Admission.Dropped(reason);
        }
        catch (CallRefusedException ex) when (ex.Kind == "unavailable")
        {
            if (workId is not null) work!.Set(workId, "dropped", ex.Message);
            return Admission.Dropped(ex.Message);
        }
        catch (OperationCanceledException)
        {
            if (workId is not null) work!.Set(workId, "cancelled", "The check was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            if (workId is not null) work!.Set(workId, "failed", ex.Message);
            throw;
        }
    }

    private int WaitSeconds() => ctx.Settings.GetOr("ideas.checkWaitSeconds", DefaultWaitSeconds);

    private bool PaidIsAllowed() => ctx.Settings.GetOr("ideas.allowPaidModel", false);

    private sealed class Leased(IDisposable lease, IPluginContext ctx, string purpose, string key, Stopwatch sw, IBackgroundWork? work, string? workId) : IDisposable
    {
        public IAgentSlot? Slot => lease as IAgentSlot;
        private int _done;
        private bool _reported;
        public void Report(string status, string? reason)
        {
            _reported = status is not ("running" or "waiting" or "yielding");
            if (workId is not null) work?.Set(workId, status, reason);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lease.Dispose();
            if (workId is not null && !_reported) work?.Set(workId, "finished");
            ctx.Logger.LogDebug("Ideas: {Purpose} held the {Key} slot for {Ms} ms", purpose, key, sw.ElapsedMilliseconds);
        }
    }
}
