using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// The queue a verification the model was too busy to judge waits in (idea-ujife1): what it bounds, what it reports
/// and when it hands a proposal back. The save check's own deferral — three forced yields and a chat that moved on —
/// is in <see cref="IdeasCheckTests"/>.
/// </summary>
public static class IdeasVerifyQueueTests
{
    /// <summary>A scheduler whose queue the test fills: what the verifier yields to and the queue waits for.</summary>
    private sealed class QueuedScheduler : IAgentScheduler
    {
        public bool Queued { get; set; }
        public int Granted;

        public string Resolve(ModelInfo model) => model.Ref;
        public IReadOnlyList<AgentSlots> Snapshot() => [new AgentSlots
        {
            Model = "fake/qwen3.8-27b",
            Waiters = Queued ? [new SlotHolder { Priority = 0, AgentId = "chat" }] : [],
        }];
        public ValueTask<IAgentSlot> AcquireAsync(AgentSlotRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Granted);
            return ValueTask.FromResult<IAgentSlot>(new Slot(this));
        }
        public bool TryAcquire(AgentSlotRequest request, out IAgentSlot? lease) { lease = new Slot(this); return true; }

        private sealed class Slot(QueuedScheduler owner) : IAgentSlot
        {
            public string Key => "fake";
            public string AgentId => "ideas";
            public DateTimeOffset AcquiredAt => DateTimeOffset.UtcNow;
            public bool IsReleased { get; private set; }
            public void Dispose() { IsReleased = true; Interlocked.Increment(ref owner.Granted); }
        }
    }

    private static async Task Until(Func<bool> what, int ms = 5000)
    {
        for (var waited = 0; waited < ms; waited += 25)
        {
            if (what()) return;
            await Task.Delay(25);
        }
        throw new AssertException("the queue never got there");
    }

    public static void Register(TestRunner r)
    {
        r.Add("ideas verify queue: one job per proposal, and a queue that is full takes none of it", async () =>
        {
            var ctx = new FakePluginContext();
            ctx.SettingsFake.Set("ideas.verifyRetrySeconds", 3600);   // nothing is due in this test
            var work = new IdeaWork(ctx); ctx.ServicesFake.Register<IBackgroundWork>(work);
            var queue = new IdeaVerifyQueue(ctx);
            var attempts = 0;
            Task<IdeaVerifyQueue.Outcome> Busy(CancellationToken _) { Interlocked.Increment(ref attempts); return Task.FromResult(IdeaVerifyQueue.Outcome.Again()); }

            Check.True(queue.Defer("save:s1:r1", null, null, Busy, "the model was busy"), "the first proposal is taken");
            Check.True(queue.Defer("save:s1:r1", null, null, Busy, "the model was busy"), "the same proposal is taken again");
            Check.Equal(1, queue.Pending, "and is still one job: two defers of one proposal coalesce");
            for (var i = 0; i < IdeaVerifyQueue.MaxPending - 1; i++)
                Check.True(queue.Defer($"save:s{i + 2}:r1", null, null, Busy, "the model was busy"));
            Check.False(queue.Defer("save:one-too-many", null, null, Busy, "the model was busy"),
                "a full queue takes none of it: the caller keeps its own retryable mark");
            Check.Equal(IdeaVerifyQueue.MaxPending, queue.Pending);

            Check.Equal(0, attempts, "and nothing ran: a job's first attempt is a backoff away, not at the gate");
            Check.Equal(IdeaVerifyQueue.MaxPending, work.Snapshot().Count(w => w.Status == "waiting"), "one work item per waiting job");
            Check.Equal(IdeaVerifyQueue.MaxPending, work.Snapshot().Count(w => w.Reason?.Contains("no attempt yet") == true),
                "and it says the wait has not been tried yet");
            Check.Contains(string.Join("|", ctx.Log.Lines), "not queued and stays retryable", "the refusal says what it did");
            queue.Dispose();
            ctx.Unload();
        });

        r.Add("ideas verify queue: a proposal is tried a bounded number of times, then handed back with its reason", async () =>
        {
            var ctx = new FakePluginContext();
            ctx.SettingsFake.Set("ideas.verifyRetrySeconds", 1);   // the waits are the shortest the queue allows
            var work = new IdeaWork(ctx); ctx.ServicesFake.Register<IBackgroundWork>(work);
            var queue = new IdeaVerifyQueue(ctx);
            var attempts = 0;
            string? given = null;
            queue.Defer("save:s1:r1", null, null, _ => { Interlocked.Increment(ref attempts); return Task.FromResult(IdeaVerifyQueue.Outcome.Again("the model was busy")); },
                "the model was busy", why => given = why);

            await Until(() => Volatile.Read(ref attempts) == IdeaVerifyQueue.MaxTries, 20000);
            Check.Equal(0, queue.Pending, "the proposal is not waiting any more");
            Check.Contains(given, "the model was busy", "and the caller is told why it is retryable again");
            Check.Contains(given, $"tried {IdeaVerifyQueue.MaxTries} times", "including how often it was tried");
            var item = work.Snapshot().Single(w => w.Reason?.Contains("the model was busy") == true);
            Check.Equal("dropped", item.Status, "the work list ends it as dropped, not as finished");
            Check.Contains(item.Reason, "the model was busy");
            Check.Equal(IdeaVerifyQueue.MaxTries, attempts, "and no attempt is made after the bound");
            await Task.Delay(200);
            Check.Equal(IdeaVerifyQueue.MaxTries, attempts, "not a moment later either: nothing is left to wake for");
            queue.Dispose();
            ctx.Unload();
        });

        r.Add("ideas verify queue: queued chats cost nothing, and their settling starts the wait", async () =>
        {
            var ctx = new FakePluginContext();
            ctx.SettingsFake.Set("ideas.verifyRetrySeconds", 1);
            var scheduler = new QueuedScheduler { Queued = true };
            ctx.ServicesFake.Register<IAgentScheduler>(scheduler);
            var queue = new IdeaVerifyQueue(ctx);
            var attempts = 0;
            queue.Defer("save:s1:r1", null, null, _ => { Interlocked.Increment(ref attempts); return Task.FromResult(IdeaVerifyQueue.Outcome.Done()); }, "the model was busy");

            await Task.Delay(400);   // longer than the backoff, so only the queue's own gate can hold it
            Check.Equal(0, attempts, "a chat queued ahead of it is served first: waiting costs no cancelled inference");

            scheduler.Queued = false;
            ctx.Bus.Publish(new BusEvent { Type = AgentSchedulerEvents.Changed });   // the slot changed hands: the wait is over
            await Until(() => Volatile.Read(ref attempts) == 1, 5000);
            Check.Equal(0, queue.Pending, "and the proposal is done with");
            queue.Dispose();
            ctx.Unload();
        });

        r.Add("ideas verify queue: the work list says why it waits, how often it was tried and when it looks again", async () =>
        {
            var ctx = new FakePluginContext();
            ctx.SettingsFake.Set("ideas.verifyRetrySeconds", 1);
            var work = new IdeaWork(ctx); ctx.ServicesFake.Register<IBackgroundWork>(work);
            var queue = new IdeaVerifyQueue(ctx);
            var attempts = 0;
            queue.Defer("save:s1:r1", "s1", "p1", _ => { Interlocked.Increment(ref attempts); return Task.FromResult(IdeaVerifyQueue.Outcome.Again("the model was full")); }, "the model was busy");

            await Until(() => Volatile.Read(ref attempts) == 1, 20000);
            var item = work.Snapshot().Single(w => w.SessionId == "s1");
            Check.Equal("waiting", item.Status, "a proposal that waits is not finished");
            Check.Contains(item.Reason, "the model was full", "the reason it is still waiting is the model's own");
            Check.Contains(item.Reason, $"attempt 1 of {IdeaVerifyQueue.MaxTries}", "with the attempt it has spent");
            Check.Contains(item.Reason, "the next one in about", "and when it is looked at again");
            Check.Equal("p1", item.ProjectId, "and which project it is about");
            queue.Dispose();
            ctx.Unload();
        });

        r.Add("ideas verify queue: a reload hands every waiting proposal back", async () =>
        {
            var ctx = new FakePluginContext();
            ctx.SettingsFake.Set("ideas.verifyRetrySeconds", 3600);
            var queue = new IdeaVerifyQueue(ctx);
            var given = new List<string>();
            queue.Defer("save:s1:r1", null, null, _ => Task.FromResult(IdeaVerifyQueue.Outcome.Again()), "the model was busy", why => given.Add(why));
            queue.Defer("save:s2:r1", null, null, _ => Task.FromResult(IdeaVerifyQueue.Outcome.Again()), "the model was busy", why => given.Add(why));
            Check.Equal(2, queue.Pending);

            ctx.Track(queue);
            ctx.Unload();   // the plugin is reloaded or NetPI stops

            Check.Equal(2, given.Count, "every waiting proposal is handed back, not left claimed");
            Check.Contains(string.Join("|", given), "NetPI stopped", "and says so");
            Check.Equal(0, queue.Pending);
            Check.False(new IdeaVerifyQueue(ctx).Defer("save:s3:r1", null, null, _ => Task.FromResult(IdeaVerifyQueue.Outcome.Done()), "the model was busy"),
                "a stopped queue takes nothing");
            ctx.Unload();
        });
    }
}
