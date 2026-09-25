using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace NetPI.Retry;

/// <summary>
/// Outermost model middleware: retries a model call when the connection is lost (transient
/// <see cref="ModelException"/>, <see cref="HttpRequestException"/>, <see cref="IOException"/>...) or the stream stalls,
/// after at least the wait the server asked for (<see cref="ModelException.RetryAfter"/>).
/// Caller cancellation and non-transient errors are never retried. Between attempts it yields
/// <see cref="StreamReset"/> (when something was already emitted) and a <see cref="StreamNotice"/> countdown.
/// </summary>
public sealed class RetryMiddleware(Func<RetryOptions> options, ILogger? logger = null) : IModelMiddleware
{
    public int Order => 0;

    public async IAsyncEnumerable<ModelStreamEvent> InvokeAsync(ModelRequest request, ModelCallDelegate next,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var o = options();
        if (!o.Enabled)
        {
            await foreach (var ev in next(request, ct).WithCancellation(ct).ConfigureAwait(false)) yield return ev;
            yield break;
        }

        var maxAttempts = Math.Max(1, o.MaxAttempts);
        var dirty = false; // something the consumer must discard was emitted since the last reset
        var started = System.Diagnostics.Stopwatch.StartNew();

        for (var attempt = 1; ; attempt++)
        {
            var stall = new CancellationTokenSource();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stall.Token);
            IAsyncEnumerator<ModelStreamEvent>? e = null;
            Task<bool>? pending = null;
            Exception? failure = null;
            var first = true;
            var completed = false;
            var stalled = false;
            try
            {
                try { e = next(request, linked.Token).GetAsyncEnumerator(linked.Token); }
                catch (Exception ex) { failure = ex; }

                while (failure is null)
                {
                    ModelStreamEvent current;
                    // Only time the provider, not the consumer: the stall timer runs while we wait for the next event.
                    stall.CancelAfter(first ? o.FirstEventTimeout : o.StallTimeout);
                    try
                    {
                        var vt = e!.MoveNextAsync();
                        bool has;
                        if (vt.IsCompletedSuccessfully) has = vt.Result;
                        else
                        {
                            pending = vt.AsTask();
                            // WaitAsync also unblocks when a provider ignores the token.
                            has = await pending.WaitAsync(linked.Token).ConfigureAwait(false);
                            pending = null;
                        }
                        if (!has)
                        {
                            failure = new ModelException("the response stream ended without a completed message", true, null, "stream_truncated");
                            break;
                        }
                        current = e.Current;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        stalled = stall.IsCancellationRequested && !ct.IsCancellationRequested;
                        break;
                    }
                    finally
                    {
                        if (!stall.IsCancellationRequested) stall.CancelAfter(Timeout.InfiniteTimeSpan);
                    }

                    first = false;
                    switch (current)
                    {
                        case StreamReset: dirty = false; break;
                        case StreamNotice: break;
                        case StreamCompleted: completed = true; break;
                        default: dirty = true; break;
                    }
                    yield return current;
                    if (completed) yield break; // the stream contract ends with StreamCompleted
                }
            }
            finally
            {
                await CleanupAsync(e, pending, linked, stall).ConfigureAwait(false);
            }

            // ---- the attempt failed
            if (ct.IsCancellationRequested)
                ExceptionDispatchInfo.Capture(failure!).Throw(); // caller cancellation: never retry

            string reason;
            if (stalled)
            {
                var secs = (first ? o.FirstEventTimeout : o.StallTimeout).TotalSeconds;
                reason = first ? $"no response for {secs:0}s" : $"stream stalled for {secs:0}s";
                failure = new ModelException($"Model stream stalled: {reason}", true, null, "stalled", failure);
            }
            else
            {
                if (!IsTransient(failure!)) ExceptionDispatchInfo.Capture(failure!).Throw();
                reason = Describe(failure!);
            }

            if (attempt >= maxAttempts)
            {
                logger?.LogWarning("Model call failed after {Attempts} attempts: {Reason}", attempt, reason);
                ExceptionDispatchInfo.Capture(failure!).Throw();
            }

            // the server said how long to wait (Retry-After): sooner is only refused again
            var asked = RetryAfterOf(failure!);
            var delay = Backoff(attempt, o);
            if (asked > delay) delay = asked.Value;
            if (o.MaxTotal != Timeout.InfiniteTimeSpan && started.Elapsed + delay > o.MaxTotal)
            {
                logger?.LogWarning("Model call failed after {Attempts} attempts in {Seconds:0}s (retry.maxTotalSeconds): {Reason}{Asked}",
                    attempt, started.Elapsed.TotalSeconds, reason, asked is null ? "" : $" (the server asked to wait {asked.Value.TotalSeconds:0}s)");
                ExceptionDispatchInfo.Capture(failure!).Throw();
            }
            logger?.LogWarning("Model call to {Model} failed (attempt {Attempt}/{Max}): {Reason}. Retrying in {Delay} ms",
                request.Model.Ref, attempt, maxAttempts, reason, (int)delay.TotalMilliseconds);

            if (dirty)
            {
                dirty = false;
                yield return new StreamReset(reason);
            }
            var secsText = Math.Max(0, (int)Math.Ceiling(delay.TotalSeconds));
            yield return new StreamNotice(asked is null
                ? $"Connection lost ({reason}). Retrying in {secsText}s (attempt {attempt + 1}/{maxAttempts})…"
                : $"{reason}: the server asked to wait. Retrying in {secsText}s (attempt {attempt + 1}/{maxAttempts})…", "warn");
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Exponential backoff (base × 2^(attempt-1), capped) with ±20% jitter.</summary>
    public static TimeSpan Backoff(int attempt, RetryOptions o)
    {
        var baseMs = o.BaseDelay.TotalMilliseconds;
        var maxMs = Math.Max(baseMs, o.MaxDelay.TotalMilliseconds);
        var ms = Math.Min(maxMs, baseMs * Math.Pow(2, Math.Min(attempt - 1, 30)));
        ms *= 0.8 + Random.Shared.NextDouble() * 0.4;
        return TimeSpan.FromMilliseconds(Math.Min(ms, maxMs));
    }

    /// <summary>How long the server asked to wait before the next attempt (a provider's <c>Retry-After</c>), if it did.</summary>
    public static TimeSpan? RetryAfterOf(Exception ex) => ex switch
    {
        ModelException { RetryAfter: { } wait } => wait,
        AggregateException { InnerExceptions.Count: 1 } a => RetryAfterOf(a.InnerExceptions[0]),
        _ => null,
    };

    /// <summary>True for errors that mean "the connection to the model was lost" and are worth retrying.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        ModelException m => m.Transient && !m.ContextOverflow,
        HttpRequestException h => h.StatusCode is not { } code || (int)code is 408 or 425 or 429 or >= 500,
        IOException or SocketException or TimeoutException => true,
        // HttpClient.Timeout surfaces as TaskCanceledException(inner TimeoutException).
        OperationCanceledException oce => oce.InnerException is TimeoutException,
        AggregateException { InnerExceptions.Count: 1 } a => IsTransient(a.InnerExceptions[0]),
        _ => false,
    };

    private static string Describe(Exception ex)
    {
        var msg = ex.Message;
        if (string.IsNullOrWhiteSpace(msg)) msg = ex.GetType().Name;
        var nl = msg.IndexOfAny(['\r', '\n']);
        if (nl > 0) msg = msg[..nl];
        msg = msg.Trim().TrimEnd('.');
        return msg.Length > 160 ? msg[..157] + "..." : msg;
    }

    private static async ValueTask CleanupAsync(IAsyncEnumerator<ModelStreamEvent>? e, Task<bool>? pending,
        CancellationTokenSource linked, CancellationTokenSource stall)
    {
        if (pending is { IsCompleted: false })
        {
            // The provider ignored cancellation: give it a moment, then abandon it and clean up when it finishes.
            if (!linked.IsCancellationRequested) { try { linked.Cancel(); } catch { } }
            try { await pending.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); } catch { }
            if (!pending.IsCompleted)
            {
                _ = pending.ContinueWith(async _ =>
                {
                    try { if (e is not null) await e.DisposeAsync().ConfigureAwait(false); } catch { }
                    linked.Dispose(); stall.Dispose();
                }, TaskScheduler.Default);
                return;
            }
        }
        try { if (e is not null) await e.DisposeAsync().ConfigureAwait(false); } catch { }
        linked.Dispose();
        stall.Dispose();
    }
}
