using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// A verification the verifier could not finish is not lost: the proposal waits here and is tried again when the
/// model is free again. A busy local model used to strand a check until the user happened to close another tab or a
/// new commit landed — after paying for cancelled inference three times (idea-ujife1).
/// <para>
/// The queue is bounded in every direction, because work that waits for the model is work that can pile up:
/// </para>
/// <list type="bullet">
/// <item>one job per key (a proposal and the revision it was read from): a second deferral of the same key
/// coalesces into the waiting one instead of queueing another attempt behind it;</item>
/// <item>at most <see cref="MaxTries"/> attempts per job, and at most <see cref="MaxPending"/> jobs waiting at all —
/// past that the caller keeps its own retryable mark, exactly as it did before this queue existed;</item>
/// <item>one wait for the whole queue (never a timer per job) and one attempt at a time, so a burst of deferrals
/// cannot become a burst of requests;</item>
/// <item>an attempt starts only when no higher-priority work is queued on the model, so waiting costs nothing: a
/// deferred check does not spend a turn cancelling inference that the chats asked for.</item>
/// </list>
/// <para>
/// Every attempt asks the caller what it would do now (its own revalidation: the idea's revision, the conversation
/// the proposal was read from, whether the project is busy) and goes through the same admission, the same paid-model
/// policy and the same output and time bounds as the first try — a retry is the same call, not a cheaper one.
/// </para>
/// </summary>
public sealed class IdeaVerifyQueue : IDisposable
{
    /// <summary>How often one deferred proposal is tried before the queue hands it back to its caller.</summary>
    public const int MaxTries = 3;
    /// <summary>How many proposals wait at once. A NetPI with more deferred work than this gets none of it.</summary>
    public const int MaxPending = 8;
    /// <summary>The wait before the first retry (<c>ideas.verifyRetrySeconds</c>); it doubles with every attempt.</summary>
    public const int DefaultRetrySeconds = 30;
    private const int MaxBackoffSeconds = 600;
    /// <summary>How long the queue looks again after an attempt said "not now" (the run or the project is busy).</summary>
    private static readonly TimeSpan PostponeStep = TimeSpan.FromSeconds(15);
    private const string Purpose = "Deferred verification";

    /// <summary>What one attempt of a deferred proposal says.</summary>
    public readonly record struct Outcome(bool Finished, string? Error = null, bool NotYet = false)
    {
        /// <summary>The check is over: its mark is written as a check that ran, or as the failure it ended in.</summary>
        public static Outcome Done() => new(true);
        public static Outcome Failed(string error) => new(true, error);
        /// <summary>The model is busy again: try after the backoff (an attempt was spent).</summary>
        public static Outcome Again(string? reason = null) => new(false, reason);
        /// <summary>Nothing was asked and nothing spent: look at this proposal again shortly.</summary>
        public static readonly Outcome Later = new(false, null, true);
    }

    private sealed class Job(string key, string? sessionId, string? projectId, Func<CancellationToken, Task<Outcome>> attempt,
        Action<string>? giveUp, string reason)
    {
        public string Key { get; } = key;
        public string? SessionId { get; } = sessionId;
        public string? ProjectId { get; } = projectId;
        /// <summary>Replaced when the same key is deferred again: the newest proposal for that revision wins.</summary>
        public Func<CancellationToken, Task<Outcome>> Attempt { get; set; } = attempt;
        public Action<string>? GiveUp { get; } = giveUp;
        public string Reason { get; set; } = reason;
        public int Tries { get; set; }
        public DateTimeOffset Due { get; set; }
        public string? WorkId { get; set; }
    }

    private readonly IPluginContext _ctx;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private readonly CancellationTokenSource _stop;
    /// <summary>The queue's one wake-up: a deferral, or foreground work settling, releases it.</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);
    private bool _disposed;

    public IdeaVerifyQueue(IPluginContext ctx)
    {
        _ctx = ctx;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        // The two moments foreground work settles: a slot changed hands, and an agent run reached a resting state.
        // Either can be the moment a waiting proposal fits, and a backoff nobody wakes is a wait for nothing.
        _subscriptions.Add(ctx.Events.Subscribe(AgentSchedulerEvents.Changed, _ => Wake()));
        _subscriptions.Add(ctx.Events.Subscribe(EventTypes.AgentStatus, _ => Wake()));
        _ = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>How many proposals are waiting (what the tests count, and what a bound is about).</summary>
    public int Pending
    {
        get { lock (_gate) return _jobs.Count; }
    }

    /// <summary>
    /// Put a proposal in the queue, or say that this queue will not take it. <paramref name="key"/> names the
    /// proposal and the revision it was read from, so two defers of one proposal are one job. False leaves the
    /// caller where it was: holding a retryable mark, and nothing else.
    /// <para>
    /// <paramref name="attempt"/> answers what it would do now, and writes whatever its own mark needs; returning
    /// <see cref="Outcome.Failed"/> hands the mark to <paramref name="giveUp"/> as well, which is what a job that is
    /// dropped without a completed attempt (its bound, a reload) calls with the reason it stopped.
    /// </para>
    /// </summary>
    public bool Defer(string key, string? sessionId, string? projectId, Func<CancellationToken, Task<Outcome>> attempt,
        string reason, Action<string>? giveUp = null)
    {
        lock (_gate)
        {
            if (_disposed || _stop.IsCancellationRequested) return false;
            if (_jobs.TryGetValue(key, out var waiting))
            {
                // The same proposal again: what it would do now is the newest answer, and it waits where it is.
                waiting.Attempt = attempt;
                waiting.Reason = reason;
                _ctx.Logger.LogDebug("Ideas: the deferred verification {Key} was asked again ({Reason}); it stays one job", key, reason);
                return true;
            }
            if (_jobs.Count >= MaxPending)
            {
                _ctx.Logger.LogWarning("Ideas: {Count} deferred verifications are already waiting: {Key} is not queued and stays retryable ({Reason})",
                    MaxPending, key, reason);
                return false;
            }
            var work = _ctx.Services.Get<IBackgroundWork>();
            var job = new Job(key, sessionId, projectId, attempt, giveUp, reason) { Due = DateTimeOffset.UtcNow.AddSeconds(Backoff(0)) };
            job.WorkId = work?.Begin(Purpose, null, sessionId, projectId);
            work?.Set(job.WorkId!, "waiting", $"{reason}; no attempt yet, the next one in about {Backoff(0):0} s");
            _jobs[key] = job;
        }
        _ctx.Logger.LogInformation("Ideas: {Purpose} {Key} is deferred ({Reason}); it is tried again when the model is free",
            Purpose, key, reason);
        Wake();
        return true;
    }

    public void Dispose()
    {
        List<Job> waiting;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            waiting = [.. _jobs.Values];
            _jobs.Clear();
        }
        foreach (var s in _subscriptions) { try { s.Dispose(); } catch { } }
        _subscriptions.Clear();
        _stop.Cancel();
        Wake();
        // A plugin that is reloaded or stopped hands every proposal back, so the check it was holding is retryable
        // at the next start instead of a claim nobody owns for the rest of its ten minutes.
        foreach (var job in waiting) Drop(job, "NetPI stopped before the deferred verification ran");
    }

    // ------------------------------------------------------------------ the loop

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Job? due = null;
            var wait = Timeout.InfiniteTimeSpan;
            var busy = ForegroundBusy();
            lock (_gate)
            {
                var next = _jobs.Values.OrderBy(j => j.Due).FirstOrDefault();
                if (next is null) { }                                     // nothing pending: wait to be woken
                else if (next.Due > DateTimeOffset.UtcNow) wait = next.Due - DateTimeOffset.UtcNow;
                else if (busy) wait = PostponeStep;                        // a chat is queued ahead of it: not yet
                else due = next;
            }
            if (due is null) { await WaitAsync(wait, ct).ConfigureAwait(false); continue; }
            await TryAsync(due, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One attempt. Nothing escapes it into the loop: a job that cannot be tried is handed back.</summary>
    private async Task TryAsync(Job job, CancellationToken ct)
    {
        job.Tries++;
        var work = _ctx.Services.Get<IBackgroundWork>();
        work?.Set(job.WorkId!, "running", $"attempt {job.Tries} of {MaxTries}: {job.Reason}");
        Outcome outcome;
        try { outcome = await job.Attempt(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }   // the caller's catch wrote its mark
        catch (Exception ex)
        {
            _ctx.Logger.LogWarning("Ideas: the deferred verification {Key} failed: {Message}", job.Key, ex.Message);
            outcome = Outcome.Failed(ex.Message);
        }
        if (ct.IsCancellationRequested) return;
        if (outcome.Finished)
        {
            lock (_gate) _jobs.Remove(job.Key);
            work?.Set(job.WorkId!, outcome.Error is null ? "finished" : "failed", outcome.Error);
            // An outcome that ended in a failure is the caller's mark to write; a caller that wrote it already loses
            // nothing (the write is a compare-and-set on its own claim).
            if (outcome.Error is { } ended) HandBack(job, ended);
            return;
        }
        if (outcome.NotYet)
        {
            // Nothing was asked and nothing charged: the proposal is looked at again shortly, and the reason says so.
            job.Due = DateTimeOffset.UtcNow + PostponeStep;
            work?.Set(job.WorkId!, "waiting", $"{job.Reason}; nothing asked yet, looked at again in about {PostponeStep.TotalSeconds:0} s");
            return;
        }
        if (outcome.Error is { } why) job.Reason = why;   // the attempt says why it is still waiting
        if (job.Tries >= MaxTries)
        {
            Drop(job, $"{job.Reason} — it was tried {job.Tries} times and the model stayed busy");
            return;
        }
        var seconds = Backoff(job.Tries);
        job.Due = DateTimeOffset.UtcNow.AddSeconds(seconds);
        work?.Set(job.WorkId!, "waiting", Deferred(job, seconds));
        _ctx.Logger.LogWarning("Ideas: the deferred verification {Key} is still waiting ({Reason}); attempt {Tries} of {Max}, the next one in about {Seconds:0} s",
            job.Key, job.Reason, job.Tries, MaxTries, seconds);
    }

    /// <summary>A proposal that will not be tried again: the work list says why, and the caller gets it back.</summary>
    private void Drop(Job job, string reason)
    {
        lock (_gate) _jobs.Remove(job.Key);
        _ctx.Services.Get<IBackgroundWork>()?.Set(job.WorkId!, "dropped", reason);
        _ctx.Logger.LogWarning("Ideas: the deferred verification {Key} is given up ({Reason}). It stays retryable: the next close of that conversation, or the next sweep, runs it again.",
            job.Key, reason);
        HandBack(job, reason);
    }

    /// <summary>The caller's work is over without having been finished: it writes its own retryable mark.</summary>
    private void HandBack(Job job, string reason)
    {
        try { job.GiveUp?.Invoke(reason); }
        catch (Exception ex) { _ctx.Logger.LogDebug("Ideas: the deferred verification {Key} could not hand its mark back: {Message}", job.Key, ex.Message); }
    }

    /// <summary>The work list says why a proposal waits, how often it has been tried and when it is looked at again.</summary>
    private static string Deferred(Job job, int seconds) =>
        $"{job.Reason}; attempt {job.Tries} of {MaxTries}, the next one in about {seconds:0} s";

    private int Backoff(int tries)
    {
        var first = Math.Clamp(RetrySeconds(), 1, 3600);
        var shift = (int)Math.Min(tries, 10);
        return Math.Min(first * (1 << shift), MaxBackoffSeconds);
    }

    private int RetrySeconds()
    {
        try { return _ctx.Settings.Get("ideas.verifyRetrySeconds", DefaultRetrySeconds); }
        catch { return DefaultRetrySeconds; }
    }

    /// <summary>Higher-priority work is queued on the model: a check must not take a turn the chats are waiting for.</summary>
    private bool ForegroundBusy()
    {
        var scheduler = _ctx.Services.Get<IAgentScheduler>();
        if (scheduler is null) return false;   // nothing to starve
        try { return scheduler.Snapshot().Any(p => p.Waiters.Any(w => w.Priority > IdeaAdmission.Priority)); }
        catch (Exception ex) { _ctx.Logger.LogDebug("Ideas: the model's queue could not be read: {Message}", ex.Message); return false; }
    }

    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay == Timeout.InfiniteTimeSpan) await _wake.WaitAsync(ct).ConfigureAwait(false);
            else await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }   // stopping: the loop's own check ends it
        catch (ObjectDisposedException) { }
    }
}
