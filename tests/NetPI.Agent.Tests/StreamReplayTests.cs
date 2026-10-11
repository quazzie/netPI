using System.Text.Json.Nodes;

namespace NetPI.Agent.Tests;

/// <summary>
/// agent.stream (StreamReplay): what a chat's answer has streamed so far and the output of its calls still running, for a
/// page that opens the chat mid-answer or comes back from a lost connection and so has missed the start.
/// </summary>
public static class StreamReplayTests
{
    public static void Register(TestRunner t)
    {
        t.Add("stream replay: the answer so far, as one delta per run of text, complete up to its seq; nothing once the run is over", AnswerSoFar);
        t.Add("stream replay: a running call's output so far, as one event; gone when the call ends", ToolOutputSoFar);
    }

    private static async Task<JsonArray> Events(TestHost h, string sessionId, Action<JsonObject>? whole = null)
    {
        var snap = (await h.Rpc.CallAsync("agent.stream", new { sessionId }))!.AsObject();
        whole?.Invoke(snap);
        return snap["events"]!.AsArray();
    }

    private static string? Type(JsonNode? e) => (string?)e!["type"];

    private static async Task AnswerSoFar()
    {
        await using var h = await TestHost.StartAsync();
        var s = h.NewSession();
        const string text = "The scheduler names the missing pool now.";
        var gate = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) => Reply.Text(text, c => gate.Task.WaitAsync(c)); // every delta, then it waits
        await h.SendAsync(s.Id, "go");
        await Wait.Until(() => string.Concat(h.Bus.OfType(EventTypes.StreamDelta).Where(e => e.SessionId == s.Id).Select(e => (string?)FakeBus.Data(e)["text"])) == text,
            "the text streamed");

        long upTo = 0;
        var latest = h.Bus.All.Max(e => e.Seq); // published, perhaps not yet seen by every subscriber
        var events = await Events(h, s.Id, snap => upTo = (long)snap["seq"]!);
        Check.Equal("stream.start", Type(events[0]), "the stream's start comes first");
        var deltas = events.Where(e => Type(e) == "stream.delta").ToList();
        Check.Equal(1, deltas.Count, "consecutive text of one kind is one event");
        Check.Equal(text, (string?)deltas[0]!["d"]!["text"]);
        Check.True(events.All(e => (long)e!["seq"]! <= upTo) && upTo >= latest, "complete up to its seq, at least what was published before the call");
        Check.Equal(0, (await Events(h, h.NewSession().Id)).Count, "another chat has nothing in flight");

        gate.SetResult();
        await h.IdleAsync(s.Id);
        Check.Equal(0, (await Events(h, s.Id)).Count, "nothing once the run is over");
    }

    private static async Task ToolOutputSoFar()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource();
        h.AddTool(new FakeTool("build", async (ctx, args, ct) =>
        {
            ctx.Output?.Invoke("compiling\n");
            ctx.Output?.Invoke("linking\n");
            await gate.Task.WaitAsync(ct);
            return ToolResult.Ok("built");
        }));
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("done") : Reply.Tool("build");
        await h.SendAsync(s.Id, "build it");
        await Wait.Until(() => string.Concat(h.Bus.OfType(EventTypes.ToolOutput).Where(e => e.SessionId == s.Id).Select(e => (string?)FakeBus.Data(e)["chunk"])) == "compiling\nlinking\n",
            "the output streamed");

        var events = await Events(h, s.Id);
        var types = string.Join(",", events.Select(Type));
        Check.Equal("tool.start,tool.output", types, "the call's start and its output so far as one event (the model call's stream ended)");
        Check.Equal("compiling\nlinking\n", (string?)events[1]!["d"]!["chunk"]);

        gate.SetResult();
        await h.IdleAsync(s.Id);
        Check.Equal(0, (await Events(h, s.Id)).Count, "the call's output goes when it ends: its result is a stored message");
    }
}
