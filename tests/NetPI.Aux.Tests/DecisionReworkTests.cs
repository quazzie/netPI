using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

public static class DecisionReworkTests
{
    public static void Register(TestRunner r)
    {
        r.Add("decision rework: near ties and malformed probabilities never authorize a change", () =>
        {
            Check.False(DecisionConfidence.Clear(0.51, 0.49, 0.5));
            Check.False(DecisionConfidence.Clear(double.NaN, 0, 0.8));
            Check.False(DecisionConfidence.Clear(1.2, 0, 0.8));
            var pick = IdeaMatch.Pick(new Dictionary<string, double> { ["A"] = 0.48, ["B"] = 0.47, ["C"] = 0.05 }, ["A", "B", "C"]);
            Check.False(pick!.Clear(0.4), "runner-up ideas count, not only none");
            Check.False(IdeaVerifier.Parse("{\"verified\":true,\"confidence\":0.51,\"reason\":\"Maybe\"}").Verified);
            Check.False(IdeaVerifier.Parse("not JSON").Verified);
            Check.True(IdeaVerifier.Parse("{\"verified\":true,\"confidence\":0.95,\"reason\":\"Evidence matches\"}").Verified);
        });
        r.Add("decision rework: verifier is read-only, takes low-priority admission and reports rejection", async () =>
        {
            var ctx = new FakePluginContext(T.TempDir("verifier"));
            ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "fake", Id = IdeaRecall.DefaultModel, IsLocal = true });
            var scheduler = new FakeAgentScheduler();
            ctx.ServicesFake.Register<IAgentScheduler>(scheduler);
            var work = new IdeaWork(ctx); ctx.ServicesFake.Register<IBackgroundWork>(work);
            ctx.ModelsFake.Responder = request => new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart
                { Text = "{\"verified\":false,\"confidence\":0.95,\"reason\":\"Only half the plan is implemented\"}" }] };
            var verdict = await new IdeaVerifier(ctx).VerifyAsync("Mark done", "Partial commit", null, null, CancellationToken.None);
            Check.False(verdict.Verified);
            Check.Equal(-10, scheduler.Acquired.Single().Priority);
            Check.Equal(1, scheduler.Released);
            Check.Equal(0, ctx.ModelsFake.Requests.Single().Tools.Count);
            Check.Equal("rejected", work.Snapshot().Single().Status);
            Check.Contains(work.Snapshot().Single().Reason, "half");
        });
        r.Add("decision rework: typed capability accepts a live lease without going through a peer RPC", async () =>
        {
            var ctx = new FakePluginContext();
            var replacement = new Replacement(); ctx.ServicesFake.Register<IDecisionService>(replacement);
            var result = await DecisionCapabilities.InvokeAsync(ctx.Services, ctx.Rpc, "decide.decision", new JsonObject { ["model"] = "custom" }, CancellationToken.None);
            Check.True(DecisionCapabilities.Available(ctx.Services, ctx.Rpc, "decide.decision"));
            Check.Equal("custom", replacement.Request!.Model);
            Check.True(replacement.Request.Conversation);
            Check.True(DecisionHints.Yes(result, "check"));
        });
        r.Add("decision rework: verifier keeps capacity until cancellation is acknowledged then restarts", async () =>
        {
            var ctx = new FakePluginContext();
            ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "fake", Id = IdeaRecall.DefaultModel, IsLocal = true });
            var scheduler = new YieldScheduler(); ctx.ServicesFake.Register<IAgentScheduler>(scheduler);
            var work = new IdeaWork(ctx); ctx.ServicesFake.Register<IBackgroundWork>(work);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            ctx.ModelsFake.AsyncResponder = async (request, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    using var registration = token.Register(() => cancelled.TrySetResult());
                    started.TrySetResult();
                    await acknowledged.Task;
                    token.ThrowIfCancellationRequested();
                }
                return new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = "{\"verified\":true,\"confidence\":0.95,\"reason\":\"Evidence supports it\"}" }] };
            };
            var verifying = new IdeaVerifier(ctx).VerifyAsync("Save plan", "An unfinished plan", null, null, CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            scheduler.Queued = true; ctx.Events.Publish(AgentSchedulerEvents.Changed);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal(0, scheduler.Released, "request cancellation does not free active inference");
            acknowledged.SetResult();
            Check.True((await verifying.WaitAsync(TimeSpan.FromSeconds(3))).Verified);
            Check.Equal(2, calls);
            Check.Equal(2, scheduler.Released);
            Check.True(work.Snapshot().Any(w => w.Status == "yielded"));
            Check.True(work.Snapshot().Any(w => w.Status == "verified"));
        });
    }
    private sealed class Replacement : IDecisionService
    {
        public DecisionRequest? Request;
        public Task<JsonObject> EvaluateAsync(DecisionRequest request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new JsonObject { ["branches"] = new JsonArray(new JsonObject
                { ["id"] = "check", ["probabilities"] = new JsonObject { ["YES"] = 0.95, ["NO"] = 0.05 } }) });
        }
    }
    private sealed class YieldScheduler : IAgentScheduler
    {
        public bool Queued; public int Released;
        public string Resolve(ModelInfo model) => model.Ref;
        public IReadOnlyList<AgentSlots> Snapshot() => [new AgentSlots { Model = "fake/" + IdeaRecall.DefaultModel,
            Waiters = Queued ? [new SlotHolder { Priority = 0 }] : [] }];
        public ValueTask<IAgentSlot> AcquireAsync(AgentSlotRequest request, CancellationToken ct) => ValueTask.FromResult<IAgentSlot>(new Lease(this, request));
        public bool TryAcquire(AgentSlotRequest request, out IAgentSlot? slot) { slot = new Lease(this, request); return true; }
        private sealed class Lease(YieldScheduler owner, AgentSlotRequest request) : IAgentSlot
        {
            public string Key => request.Key; public string AgentId => request.AgentId;
            public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;
            public bool IsReleased { get; private set; }
            public void Dispose() { if (IsReleased) return; IsReleased = true; owner.Released++; owner.Queued = false; }
        }
    }
}
