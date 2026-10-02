using System.Net;
using System.Runtime.CompilerServices;
using NetPI.Retry;

namespace NetPI.Aux.Tests;

public static class RetryTests
{
    private static readonly ModelRequest Request = new() { Model = T.Model(), Messages = [T.User("hi")] };

    private static RetryOptions Fast(int attempts = 4, int firstMs = 5000, int stallMs = 5000, int maxTotalMs = 300_000) => new()
    {
        MaxAttempts = attempts, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5),
        FirstEventTimeout = TimeSpan.FromMilliseconds(firstMs), StallTimeout = TimeSpan.FromMilliseconds(stallMs),
        MaxTotal = TimeSpan.FromMilliseconds(maxTotalMs),
    };

    private static StreamCompleted Done(string text = "hello") =>
        new(new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = text }], StopReason = "stop" });

    /// <summary>A provider whose behaviour depends on the attempt number (1-based).</summary>
    private sealed class Script(Func<int, CancellationToken, IAsyncEnumerable<ModelStreamEvent>> body)
    {
        public int Calls;
        public ModelCallDelegate Next => (_, ct) => body(Interlocked.Increment(ref Calls), ct);
    }

    private static async Task<List<ModelStreamEvent>> Collect(IAsyncEnumerable<ModelStreamEvent> s, CancellationToken ct = default,
        int consumerDelayMs = 0)
    {
        var list = new List<ModelStreamEvent>();
        await foreach (var e in s.WithCancellation(ct))
        {
            list.Add(e);
            if (consumerDelayMs > 0) await Task.Delay(consumerDelayMs);
        }
        return list;
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Events(params ModelStreamEvent[] events)
    {
        foreach (var e in events) { await Task.Yield(); yield return e; }
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Fail(Exception ex, params ModelStreamEvent[] before)
    {
        foreach (var e in before) { await Task.Yield(); yield return e; }
        await Task.Yield();
        throw ex;
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Hang([EnumeratorCancellation] CancellationToken ct, bool honorToken = true, params ModelStreamEvent[] before)
    {
        foreach (var e in before) { await Task.Yield(); yield return e; }
        if (honorToken) await Task.Delay(Timeout.Infinite, ct);
        else await Task.Delay(Timeout.Infinite, CancellationToken.None);
        yield break;
    }

    public static void Register(TestRunner r)
    {
        r.Add("retry: transient failure after partial output → reset, notice, success", async () =>
        {
            var script = new Script((n, _) => n == 1
                ? Fail(new ModelException("backend_unavailable", true, 503), new TextDelta("hel"))
                : Events(new TextDelta("hello"), Done()));
            var mw = new RetryMiddleware(() => Fast());
            var events = await Collect(mw.InvokeAsync(Request, script.Next, CancellationToken.None));
            Check.Equal(2, script.Calls);
            Check.Equal(5, events.Count, string.Join(", ", events));
            Check.True(events[0] is TextDelta { Text: "hel" });
            Check.True(events[1] is StreamReset);
            var notice = events[2] as StreamNotice;
            Check.True(notice is not null);
            Check.Equal("warn", notice!.Level);
            Check.Equal("Connection lost. Retrying in 1s (attempt 2/4)…", notice.Text);
            Check.Contains(notice.Text, "(attempt 2/4)");
            Check.True(events[3] is TextDelta { Text: "hello" });
            Check.True(events[4] is StreamCompleted);
        });

        r.Add("retry: failure before any output → notice only, no reset", async () =>
        {
            var script = new Script((n, _) => n < 3 ? Fail(new HttpRequestException("Connection refused")) : Events(Done()));
            var events = await Collect(new RetryMiddleware(() => Fast()).InvokeAsync(Request, script.Next, CancellationToken.None));
            Check.Equal(3, script.Calls);
            Check.False(events.Any(e => e is StreamReset));
            Check.Equal(2, events.Count(e => e is StreamNotice));
            Check.Contains(((StreamNotice)events[1]).Text, "(attempt 3/4)");
            Check.True(events[^1] is StreamCompleted);
        });

        r.Add("retry: IOException and synchronous throw from next() are retried", async () =>
        {
            var calls = 0;
            ModelCallDelegate next = (_, _) =>
            {
                calls++;
                if (calls == 1) throw new IOException("reset by peer");
                if (calls == 2) return Fail(new IOException("unexpected EOF"), new ThinkingDelta("hmm"));
                return Events(Done());
            };
            var events = await Collect(new RetryMiddleware(() => Fast()).InvokeAsync(Request, next, CancellationToken.None));
            Check.Equal(3, calls);
            Check.Equal(1, events.Count(e => e is StreamReset));
            Check.True(events[^1] is StreamCompleted);
        });

        r.Add("retry: transient classification", () =>
        {
            Check.True(RetryMiddleware.IsTransient(new ModelException("x", true)));
            Check.False(RetryMiddleware.IsTransient(new ModelException("x", false, 400)));
            Check.False(RetryMiddleware.IsTransient(new ModelException("too long", true) { ContextOverflow = true }));
            Check.True(RetryMiddleware.IsTransient(new HttpRequestException("down")));
            Check.True(RetryMiddleware.IsTransient(new HttpRequestException("x", null, HttpStatusCode.BadGateway)));
            Check.True(RetryMiddleware.IsTransient(new HttpRequestException("x", null, HttpStatusCode.TooManyRequests)));
            Check.False(RetryMiddleware.IsTransient(new HttpRequestException("x", null, HttpStatusCode.Unauthorized)));
            Check.True(RetryMiddleware.IsTransient(new IOException("eof")));
            Check.True(RetryMiddleware.IsTransient(new TaskCanceledException("timeout", new TimeoutException())));
            Check.False(RetryMiddleware.IsTransient(new OperationCanceledException()));
            Check.False(RetryMiddleware.IsTransient(new InvalidOperationException("bug")));
            Check.True(RetryMiddleware.IsTransient(new AggregateException(new IOException("x"))));
        });

        r.Add("retry: non-transient error is rethrown immediately", async () =>
        {
            var script = new Script((_, _) => Fail(new ModelException("invalid api key", false, 401), new TextDelta("x")));
            var ex = await Check.ThrowsAsync<ModelException>(() => Collect(new RetryMiddleware(() => Fast()).InvokeAsync(Request, script.Next, CancellationToken.None)));
            Check.Equal("invalid api key", ex.Message);
            Check.Equal(1, script.Calls);
        });

        r.Add("retry: context overflow is not retried", async () =>
        {
            var script = new Script((_, _) => Fail(new ModelException("prompt is too long", false, 400) { ContextOverflow = true }));
            var ex = await Check.ThrowsAsync<ModelException>(() => Collect(new RetryMiddleware(() => Fast()).InvokeAsync(Request, script.Next, CancellationToken.None)));
            Check.True(ex.ContextOverflow);
            Check.Equal(1, script.Calls);
        });

        r.Add("retry: the notice is a status line — the provider's words, ids and dump path are not in it", async () =>
        {
            // What the OpenRouter and AiProxy providers do: the ids go into the message and are named as Detail.
            const string Detail = "generation gen-1790629699-WxYBdRSnM2s3j77xSLV3, saved C:\\Users\\quazz\\.netpi\\logs\\failed-requests\\20260928-232737-795-stealth.json";
            const string Reason = "OpenRouter: stream error: Provider returned an empty response (upstream provider Stealth)";
            static ModelException Failure() => new($"{Reason} [{Detail}]", true, null, "provider_error") { Detail = Detail };

            var script = new Script((n, _) => n == 1
                ? Fail(Failure(), new TextDelta("hel"))      // partial output: a reset carries the reason to diag
                : Events(new TextDelta("hello"), Done()));
            var events = await Collect(new RetryMiddleware(() => Fast()).InvokeAsync(Request, script.Next, CancellationToken.None));
            Check.Equal("Connection lost. Retrying in 1s (attempt 2/4)…", ((StreamNotice)events[2]).Text);
            var notice = ((StreamNotice)events[2]).Text;
            Check.NotContains(notice, "OpenRouter");
            Check.NotContains(notice, "gen-1790629699");
            Check.NotContains(notice, "failed-requests");
            // diagnostics keep the whole reason: the reset reason is what diag shows
            Check.Contains(((StreamReset)events[1]).Reason, "gen-1790629699");

            // the error a person is finally shown, when the retries run out, is the reason without the decoration
            var always = new Script((_, _) => Fail(Failure()));
            var thrown = await Check.ThrowsAsync<ModelException>(() =>
                Collect(new RetryMiddleware(() => Fast(attempts: 2)).InvokeAsync(Request, always.Next, CancellationToken.None)));
            Check.Contains(thrown.Message, "gen-1790629699");
            Check.Equal(Reason, thrown.DisplayMessage);
        });

        r.Add("retry: DisplayMessage is the message without Detail, and the message itself when there is none to remove", () =>
        {
            Check.Equal("boom", new ModelException("boom", true).DisplayMessage);
            var ids = "request req-1, response resp-1";
            Check.Equal("boom", new ModelException($"boom [{ids}]", true) { Detail = ids }.DisplayMessage);
            // a provider that wrote its own message, or renamed the decoration, keeps it: nothing is cut out of a guess
            Check.Equal("boom [code 1]", new ModelException("boom [code 1]", true) { Detail = "generation gen-1" }.DisplayMessage);
            Check.Equal("boom", new ModelException("boom", true) { Detail = "" }.DisplayMessage);
        });

        r.Add("retry: gives up after maxAttempts and rethrows the last error", async () =>
        {
            var script = new Script((n, _) => Fail(new ModelException($"overloaded #{n}", true, 529)));
            var notices = new List<ModelStreamEvent>();
            var ex = await Check.ThrowsAsync<ModelException>(async () =>
            {
                await foreach (var e in new RetryMiddleware(() => Fast(attempts: 3)).InvokeAsync(Request, script.Next, CancellationToken.None))
                    notices.Add(e);
            });
            Check.Equal("overloaded #3", ex.Message);
            Check.Equal(3, script.Calls);
            Check.Equal(2, notices.Count(e => e is StreamNotice));
        });

        r.Add("retry: stall before the first event (firstEventTimeout) is detected and retried", async () =>
        {
            var script = new Script((n, ct) => n == 1 ? Hang(ct) : Events(new TextDelta("ok"), Done()));
            var events = await Collect(new RetryMiddleware(() => Fast(firstMs: 250, stallMs: 5000)).InvokeAsync(Request, script.Next, CancellationToken.None));
            Check.Equal(2, script.Calls);
            var notice = events.OfType<StreamNotice>().Single();
            Check.Equal("No response for 1s. Retrying in 1s (attempt 2/4)…", notice.Text);
            Check.False(events.Any(e => e is StreamReset));
            Check.True(events[^1] is StreamCompleted);
        });

        r.Add("retry: a first-token stall is still retried when maxTotalSeconds is below firstEventTimeoutSeconds", async () =>
        {
            // the default relationship (300 s budget, 600 s first-token wait) used to give up at once: a full stall
            // alone outlived the budget, so the documented first-event retry never happened (idea-ohk2bz)
            var o = new RetryOptions
            {
                MaxAttempts = 4, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5),
                FirstEventTimeout = TimeSpan.FromMilliseconds(200), StallTimeout = TimeSpan.FromMilliseconds(5000),
                MaxTotal = TimeSpan.FromMilliseconds(100),
            };
            var script = new Script((n, ct) => n == 1 ? Hang(ct) : Events(new TextDelta("ok"), Done()));
            var events = await Collect(new RetryMiddleware(() => o).InvokeAsync(Request, script.Next, CancellationToken.None));
            Check.Equal(2, script.Calls);
            Check.Equal("No response for 1s. Retrying in 1s (attempt 2/4)…", events.OfType<StreamNotice>().Single().Text);
            Check.True(events[^1] is StreamCompleted);
        });

        r.Add("retry: the settings reader clamps maxTotalSeconds to at least firstEventTimeoutSeconds", () =>
        {
            var ctx = new FakePluginContext();
            Check.Equal(TimeSpan.FromSeconds(600), RetryOptions.From(ctx.Settings).MaxTotal); // the defaults agree
            ctx.SettingsFake.Set("retry.firstEventTimeoutSeconds", 300);
            ctx.SettingsFake.Set("retry.maxTotalSeconds", 100);
            Check.Equal(TimeSpan.FromSeconds(300), RetryOptions.From(ctx.Settings).MaxTotal);
            ctx.SettingsFake.Set("retry.maxTotalSeconds", 600);
            Check.Equal(TimeSpan.FromSeconds(600), RetryOptions.From(ctx.Settings).MaxTotal); // a larger budget stays
        });

        r.Add("retry: stall between events (stallTimeout) → reset + retry", async () =>
        {
            var script = new Script((n, ct) => n == 1 ? Hang(ct, true, new TextDelta("partial")) : Events(new TextDelta("full"), Done()));
            var events = await Collect(new RetryMiddleware(() => Fast(firstMs: 5000, stallMs: 250)).InvokeAsync(Request, script.Next, CancellationToken.None));
            Check.Equal(2, script.Calls);
            Check.True(events[0] is TextDelta { Text: "partial" });
            Check.True(events[1] is StreamReset { Reason: var reason } && reason.Contains("stalled"));
            Check.Equal("Stream stalled for 1s. Retrying in 1s (attempt 2/4)…", ((StreamNotice)events[2]).Text);
            Check.True(events[^1] is StreamCompleted);
        });

        r.Add("retry: stall is detected even when the provider ignores cancellation", async () =>
        {
            var script = new Script((n, ct) => n == 1 ? Hang(ct, honorToken: false) : Events(Done()));
            var events = await Collect(new RetryMiddleware(() => Fast(firstMs: 150)).InvokeAsync(Request, script.Next, CancellationToken.None));
            Check.Equal(2, script.Calls);
            Check.True(events[^1] is StreamCompleted);
        });

        r.Add("retry: time spent by the consumer does not count as a stall", async () =>
        {
            var script = new Script((_, _) => Events(new TextDelta("a"), new TextDelta("b"), new TextDelta("c"), Done()));
            var events = await Collect(new RetryMiddleware(() => Fast(firstMs: 300, stallMs: 300)).InvokeAsync(Request, script.Next, CancellationToken.None),
                consumerDelayMs: 450);
            Check.Equal(1, script.Calls);
            Check.Equal(4, events.Count);
        });

        r.Add("retry: a drop after a stream longer than maxTotalSeconds is still retried", async () =>
        {
            // a slow model streams for longer than the whole retry budget and then the connection drops:
            // the productive time must not count against the budget, or this — the case retries exist for — is lost (idea-ohk2bz)
            static async IAsyncEnumerable<ModelStreamEvent> SlowThenDrop([EnumeratorCancellation] CancellationToken ct)
            {
                for (var i = 0; i < 3; i++) { await Task.Delay(80, ct); yield return new TextDelta($"x{i}"); }
                throw new IOException("reset by peer");
            }
            var script = new Script((n, ct) => n == 1 ? SlowThenDrop(ct) : Events(new TextDelta("ok"), Done()));
            var events = await Collect(new RetryMiddleware(() => Fast(maxTotalMs: 150)).InvokeAsync(Request, script.Next, CancellationToken.None));
            Check.Equal(2, script.Calls);
            Check.True(events.Any(e => e is StreamReset));
            Check.Equal("Connection lost. Retrying in 1s (attempt 2/4)…", events.OfType<StreamNotice>().Single().Text);
            Check.True(events[^1] is StreamCompleted);
        });

        r.Add("retry: stall on the last attempt throws a transient 'stalled' ModelException", async () =>
        {
            var script = new Script((_, ct) => Hang(ct));
            var ex = await Check.ThrowsAsync<ModelException>(() => Collect(new RetryMiddleware(() => Fast(attempts: 2, firstMs: 80)).InvokeAsync(Request, script.Next, CancellationToken.None)));
            Check.Equal("stalled", ex.ErrorType);
            Check.True(ex.Transient);
            Check.Equal(2, script.Calls);
        });

        r.Add("retry: caller cancellation during a call is never retried", async () =>
        {
            using var cts = new CancellationTokenSource();
            var script = new Script((_, ct) => Hang(ct, true, new TextDelta("x")));
            var task = Collect(new RetryMiddleware(() => Fast()).InvokeAsync(Request, script.Next, cts.Token), cts.Token);
            await Task.Delay(100);
            cts.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => task);
            Check.Equal(1, script.Calls);
        });

        r.Add("retry: caller cancellation during the backoff delay", async () =>
        {
            using var cts = new CancellationTokenSource();
            var script = new Script((_, _) => Fail(new IOException("x")));
            var o = new RetryOptions { MaxAttempts = 5, BaseDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(10) };
            var seen = new List<ModelStreamEvent>();
            var task = Task.Run(async () =>
            {
                await foreach (var e in new RetryMiddleware(() => o).InvokeAsync(Request, script.Next, cts.Token)) seen.Add(e);
            });
            await Task.Delay(150);
            cts.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => task);
            Check.Equal(1, script.Calls);
            var text = ((StreamNotice)seen.Single()).Text;
            Check.True(text.Contains("Retrying in 8s") || text.Contains("Retrying in 9s") || text.Contains("Retrying in 10s")
                       || text.Contains("Retrying in 11s") || text.Contains("Retrying in 12s"), text);
            Check.Contains(text, "(attempt 2/5)");
        });

        // A 429 or 529 says how long to wait: retrying sooner is only refused again
        r.Add("retry: waits at least as long as the server asked (Retry-After)", async () =>
        {
            var script = new Script((n, _) => n == 1
                ? Fail(new ModelException("OpenRouter: HTTP 429: rate limited", true, 429) { RetryAfter = TimeSpan.FromMilliseconds(400) })
                : Events(Done()));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var events = await Collect(new RetryMiddleware(() => Fast()).InvokeAsync(Request, script.Next, CancellationToken.None));
            Check.True(clock.ElapsedMilliseconds >= 390, $"waited {clock.ElapsedMilliseconds} ms (the backoff alone is 1 ms)");
            Check.Equal(2, script.Calls);
            var notice = events.OfType<StreamNotice>().Single().Text;
            Check.Equal("The server asked to wait 1s. Retrying in 1s (attempt 2/4)…", notice);
            Check.Equal(TimeSpan.FromSeconds(3), RetryMiddleware.RetryAfterOf(new AggregateException(new ModelException("x", true) { RetryAfter = TimeSpan.FromSeconds(3) })));
            Check.True(RetryMiddleware.RetryAfterOf(new IOException("x")) is null);
        });

        r.Add("retry: a Retry-After beyond retry.maxTotalSeconds gives up at once", async () =>
        {
            var script = new Script((_, _) => Fail(new ModelException("rate limited", true, 429) { RetryAfter = TimeSpan.FromMinutes(10) }));
            var o = new RetryOptions { MaxAttempts = 5, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5), MaxTotal = TimeSpan.FromSeconds(60) };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Check.ThrowsAsync<ModelException>(() => Collect(new RetryMiddleware(() => o).InvokeAsync(Request, script.Next, CancellationToken.None)));
            Check.Equal("rate limited", ex.Message);
            Check.Equal(1, script.Calls);
            Check.True(clock.ElapsedMilliseconds < 2000, "no waiting");
        });

        r.Add("retry: a stream that ends without StreamCompleted is retried", async () =>
        {
            var script = new Script((n, _) => n == 1 ? Events(new TextDelta("cut")) : Events(Done()));
            var events = await Collect(new RetryMiddleware(() => Fast()).InvokeAsync(Request, script.Next, CancellationToken.None));
            Check.Equal(2, script.Calls);
            Check.True(events.Any(e => e is StreamReset));
            Check.True(events[^1] is StreamCompleted);
        });

        r.Add("retry: disabled → pass-through, errors propagate", async () =>
        {
            var script = new Script((_, _) => Fail(new IOException("x"), new TextDelta("a")));
            var mw = new RetryMiddleware(() => new RetryOptions { Enabled = false });
            await Check.ThrowsAsync<IOException>(() => Collect(mw.InvokeAsync(Request, script.Next, CancellationToken.None)));
            Check.Equal(1, script.Calls);
        });

        r.Add("retry: backoff doubles with jitter and is capped", () =>
        {
            var o = new RetryOptions { BaseDelay = TimeSpan.FromMilliseconds(1000), MaxDelay = TimeSpan.FromMilliseconds(30_000) };
            for (var i = 0; i < 50; i++)
            {
                var d1 = RetryMiddleware.Backoff(1, o).TotalMilliseconds;
                var d3 = RetryMiddleware.Backoff(3, o).TotalMilliseconds;
                var d10 = RetryMiddleware.Backoff(10, o).TotalMilliseconds;
                Check.True(d1 is >= 800 and <= 1200, $"attempt 1: {d1}");
                Check.True(d3 is >= 3200 and <= 4800, $"attempt 3: {d3}");
                Check.True(d10 is >= 24_000 and <= 30_000, $"attempt 10: {d10}");
            }
        });

        r.Add("retry: settings are read per call", async () =>
        {
            var ctx = new FakePluginContext();
            await new RetryPlugin().StartAsync(ctx, CancellationToken.None);
            var mw = ctx.Services.Get<IModelMiddleware>();
            Check.True(mw is RetryMiddleware);
            Check.Equal(0, mw!.Order);
            ctx.SettingsFake.Set("retry.maxAttempts", 2);
            ctx.SettingsFake.Set("retry.baseDelayMs", 1);
            var script = new Script((_, _) => Fail(new IOException("x")));
            await Check.ThrowsAsync<IOException>(() => Collect(mw.InvokeAsync(Request, script.Next, CancellationToken.None)));
            Check.Equal(2, script.Calls);
            var o = RetryOptions.From(ctx.Settings);
            Check.Equal(TimeSpan.FromSeconds(600), o.FirstEventTimeout);
            Check.Equal(TimeSpan.FromSeconds(180), o.StallTimeout);
            Check.Equal(TimeSpan.FromSeconds(600), o.MaxTotal); // the budget clamps to the first-token wait, so the defaults agree
        });
    }
}
