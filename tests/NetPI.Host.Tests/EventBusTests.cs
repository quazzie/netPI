using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Events;

namespace NetPI.Host.Tests;

public static class EventBusTests
{
    public static void Register(TestRunner r)
    {
        r.Add("bus: delivers in publish order with increasing Seq", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            var seen = new List<(int N, long Seq)>();
            using var _ = bus.Subscribe("n.value", e => seen.Add(((int)e.Data!, e.Seq)));
            for (var i = 0; i < 2000; i++) bus.Publish("n.value", i);
            await bus.FlushAsync();
            Check.Equal(2000, seen.Count);
            for (var i = 0; i < seen.Count; i++) Check.Equal(i, seen[i].N);
            Check.True(seen.Zip(seen.Skip(1)).All(p => p.Second.Seq == p.First.Seq + 1), "consecutive seq");
        });

        r.Add("bus: exact, prefix and * patterns; async handlers; failing handler isolation", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            var log = new List<string>();
            using var a = bus.Subscribe("agent.status", e => log.Add("exact:" + e.Type));
            using var b = bus.Subscribe("agent.*", e => log.Add("prefix:" + e.Type));
            using var c = bus.Subscribe("*", e => log.Add("all:" + e.Type));
            using var d = bus.Subscribe("agent.status", _ => throw new InvalidOperationException("handler bug"));
            using var e1 = bus.SubscribeAsync("agent.status", async e => { await Task.Delay(5); log.Add("async:" + e.Type); });
            bus.Publish("agent.status");
            bus.Publish("agentx");
            bus.Publish("agent");
            bus.Publish("agent.queue.more");
            await bus.FlushAsync();
            Check.Equal("exact:agent.status,prefix:agent.status,all:agent.status,async:agent.status,all:agentx,all:agent,prefix:agent.queue.more,all:agent.queue.more",
                string.Join(",", log));
        });

        r.Add("bus: disposing a subscription is idempotent and stops delivery", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            var count = 0;
            var sub = bus.Subscribe("x", _ => count++);
            bus.Publish("x");
            await bus.FlushAsync();
            sub.Dispose();
            sub.Dispose();
            bus.Publish("x");
            await bus.FlushAsync();
            Check.Equal(1, count);
            Check.Equal(0, bus.SubscriberCount);
        });

        r.Add("bus: ring buffer keeps the last 500 events", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            for (var i = 0; i < 700; i++) bus.Publish(new BusEvent { Type = "r", Data = i, Ui = false });
            await bus.FlushAsync();
            var recent = bus.Recent(1000);
            Check.Equal(500, recent.Count);
            Check.Equal(200, (int)recent[0].Data!);
            Check.Equal(699, (int)recent[^1].Data!);
            var last3 = bus.Recent(3);
            Check.Equal("697,698,699", string.Join(",", last3.Select(e => e.Data)));
        });
    }
}
