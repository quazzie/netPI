using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Agent;
using Xunit;
using Xunit.Abstractions;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 I (Package I, slice 1): a DETERMINISTIC measurement harness for the
/// completed path. It drives <see cref="AgentRuntime"/> with a FAKE provider so
/// that harness overhead is isolated from provider latency (PLAN §11: "Separate
/// provider latency from harness overhead"), across the named fixtures:
///
///   - empty session
///   - long transcript (many pre-loaded messages → context reconstruction)
///   - long streamed text output (many deltas → coalescing, PLAN §45)
///   - large shell output (one big tool result → output retention)
///   - two simultaneous sessions (per-run isolation under concurrency)
///
/// The harness records wall-clock (harness-side), iteration count and a heap
/// delta per fixture, then writes a baseline report to .scratch (git-ignored).
/// MEASUREMENT-FIRST: no threshold is asserted and no optimization is made here —
/// these are the "before" numbers PLAN §11 requires before any change. Only run
/// CORRECTNESS is asserted (deterministic, never flaky on timing); the numbers
/// are the data. WebView2 foreground/background and retained-DOM-node metrics are
/// out of scope for a headless harness and are documented as such in the report.
/// </summary>
public class PerformanceBaselineTests(ITestOutputHelper output)
{
    private static readonly string ScratchDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".scratch"));
    private static readonly string ReportPath = Path.Combine(ScratchDir, "i-baseline.json");

    // ---- the fake provider (isolates provider latency) -------------------------

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly Queue<IReadOnlyList<ModelEvent>> _turns;
        public ScriptedProvider(params IReadOnlyList<ModelEvent>[] turns) => _turns = new(turns);
        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken ct)
        {
            var evs = _turns.Count > 0 ? _turns.Dequeue() : null;
            if (evs is null) yield break;
            foreach (var e in evs)
            {
                ct.ThrowIfCancellationRequested();
                yield return e;
            }
        }
    }

    private sealed class BigEchoTool : IAgentTool
    {
        private readonly string _body;
        public BigEchoTool(string body) => _body = body;
        public string Name => "bigecho";
        public string Description => "returns a large result";
        public IReadOnlyList<string> Guidelines => [];
        public JsonElement Parameters => JsonSerializer.SerializeToElement(new { type = "object" });
        public ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct)
            => ValueTask.FromResult(new ToolResult("b1", "bigecho", [new TextPart(_body)], false));
    }

    // ---- fixtures ---------------------------------------------------------------

    private static AgentMessage Msg(int n, string text)
        => new("m" + n, MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow);

    private static string Chars(char c, int n)
    {
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>One turn streaming ~totalChars of text in chunkChars steps.</summary>
    private static List<ModelEvent> TextTurn(string blockId, int totalChars, int chunkChars)
    {
        var evs = new List<ModelEvent> { new ModelStarted("model") };
        for (int w = 0; w < totalChars; w += chunkChars)
            evs.Add(new TextDelta(new string('w', Math.Min(chunkChars, totalChars - w))));
        evs.Add(new TextCompleted(blockId));
        evs.Add(new ModelCompleted(new AgentMessage("a1", MessageRole.Assistant,
            [new TextPart(new string('w', totalChars))], DateTimeOffset.UtcNow)));
        return evs;
    }

    private static AgentRunOptions Options(string sessionId, params AgentMessage?[]? preloaded)
    {
        var msgs = new List<AgentMessage>();
        if (preloaded is not null) msgs.AddRange(preloaded);
        msgs.Add(new("u1", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow));
        return new() { SessionId = sessionId, ModelId = "model", Messages = msgs };
    }

    /// <summary>Runs a run synchronously against a fresh context and asserts it is OK.</summary>
    private static void RunSync(NoopPluginContext ctx, AgentRunOptions options)
    {
        var rt = new AgentRuntime(ctx);
        var res = rt.RunAsync(options, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (!res.Ok) throw new InvalidOperationException($"run failed: {res.Note}");
    }

    private sealed record Fixture(string Name, int Iterations, Action Run);

    private static List<Fixture> BuildFixtures()
    {
        return
        [
            new("empty-session", 100, () =>
            {
                var ctx = new NoopPluginContext();
                ctx.Add("provider", new ScriptedProvider(new ModelEvent[]
                {
                    new ModelStarted("model"),
                    new TextCompleted("t1"),
                    new ModelCompleted(new AgentMessage("a1", MessageRole.Assistant, [new TextPart("ok")], DateTimeOffset.UtcNow)),
                }));
                RunSync(ctx, Options("empty"));
            }),

            new("long-transcript", 25, () =>
            {
                // 200 pre-loaded user messages (~400 chars each): the model request
                // is reconstructed over a long transcript each turn.
                var pre = new AgentMessage?[200];
                for (int i = 0; i < 200; i++) pre[i] = Msg(i, Chars('a', 400));
                var ctx = new NoopPluginContext();
                ctx.Add("provider", new ScriptedProvider(new ModelEvent[]
                {
                    new ModelStarted("model"),
                    new TextCompleted("t1"),
                    new ModelCompleted(new AgentMessage("a1", MessageRole.Assistant, [new TextPart("ok")], DateTimeOffset.UtcNow)),
                }));
                RunSync(ctx, Options("long", pre));
            }),

            new("long-stream", 25, () =>
            {
                // ~40KB streamed text in 200-char deltas (~200 deltas) → delta
                // coalescing (PLAN §45) and transcript growth.
                var ctx = new NoopPluginContext();
                ctx.Add("provider", new ScriptedProvider(TextTurn("t1", 40_000, 200)));
                RunSync(ctx, Options("stream"));
            }),

            new("large-shell-output", 25, () =>
            {
                // Two turns: first requests the tool, second closes the run. The
                // tool returns a ~256 KB result → output retention in the transcript.
                var big = Chars('x', 256_000);
                var reg = new TestRegistry(new BigEchoTool(big));
                var ctx = new NoopPluginContext();
                var turn1 = new ModelEvent[] { new ModelStarted("model"),
                    new ModelCompleted(new AgentMessage("a1", MessageRole.Assistant,
                        [new ToolCallPart("c1", "bigecho", JsonSerializer.SerializeToElement(new { }))], DateTimeOffset.UtcNow)) };
                var turn2 = new ModelEvent[] { new ModelStarted("model"),
                    new TextCompleted("t2"),
                    new ModelCompleted(new AgentMessage("a2", MessageRole.Assistant, [new TextPart("done")], DateTimeOffset.UtcNow)) };
                ctx.Add("provider", new ScriptedProvider(turn1, turn2));
                ctx.Add("tools", reg);
                RunSync(ctx, Options("bigout"));
            }),

            new("two-simultaneous-sessions", 25, () =>
            {
                // Two concurrent runs on distinct sessions; per-run scope must keep
                // them from clobbering each other (astra-1 E).
                // Two concurrent runs on distinct sessions, each with its own
                // context + provider lease (as the host gives a run its own lease).
                var ctxA = new NoopPluginContext();
                ctxA.Add("provider", new ScriptedProvider(TextTurn("ta", 4000, 200)));
                var ctxB = new NoopPluginContext();
                ctxB.Add("provider", new ScriptedProvider(TextTurn("tb", 4000, 200)));
                var rtA = new AgentRuntime(ctxA);
                var rtB = new AgentRuntime(ctxB);
                var tA = rtA.RunAsync(Options("A"), CancellationToken.None).AsTask();
                var tB = rtB.RunAsync(Options("B"), CancellationToken.None).AsTask();
                var both = Task.WhenAll(tA, tB).GetAwaiter().GetResult();
                if (!both[0].Ok || !both[1].Ok)
                    throw new InvalidOperationException("concurrent run failed");
            }),
        ];
    }

    // ---- the measurement fact ---------------------------------------------------

    private sealed record Row(string Name, int Iterations, double TotalMs, double MinMs, double MaxMs, double AvgMs, long HeapDelta)
    {
        public static string Report(IReadOnlyList<Row> rows) =>
            JsonSerializer.Serialize(new
            {
                generatedAt = DateTime.UtcNow,
                note = "astra-1 I baseline: harness-side wall-clock with a FAKE provider (provider latency excluded). " +
                      "No thresholds asserted. WebView2 foreground/background + retained-DOM-node metrics are out of scope " +
                      "for a headless harness and must be captured separately.",
                rows,
            }, new JsonSerializerOptions { WriteIndented = true });
    }

    [Fact]
    public void Measure_AllFixtures_WritesBaselineReport()
    {
        var fixtures = BuildFixtures();
        var rows = new List<Row>();
        foreach (var f in fixtures)
        {
            f.Run(); // one warmup (JIT/first-run) not measured
            double total = 0, min = double.MaxValue, max = 0;
            long baseHeap = GC.GetTotalMemory(true);
            for (int i = 0; i < f.Iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                f.Run();
                sw.Stop();
                var ms = sw.Elapsed.TotalMilliseconds;
                total += ms; min = Math.Min(min, ms); max = Math.Max(max, ms);
            }
            long delta = GC.GetTotalMemory(true) - baseHeap;
            rows.Add(new Row(f.Name, f.Iterations, total, min, max, total / f.Iterations, delta));
        }
        var json = Row.Report(rows);
        output.WriteLine(json);
        Directory.CreateDirectory(ScratchDir);
        File.WriteAllText(ReportPath, json);

        // The report is the deliverable: every measured fixture is present.
        foreach (var f in fixtures)
            Assert.Contains(f.Name, json);
        Assert.True(File.Exists(ReportPath));
    }

    /// <summary>Correctness property behind the concurrency fixture: two runs on
    /// distinct sessions keep their own data (astra-1 E per-run scope).</summary>
    [Fact]
    public async Task TwoSimultaneousSessions_DoNotClobber_EachKeepsItsOwnData()
    {
        var ctxA = new NoopPluginContext();
        ctxA.Add("provider", new ScriptedProvider(TextTurn("ta", 8000, 200)));
        var ctxB = new NoopPluginContext();
        ctxB.Add("provider", new ScriptedProvider(TextTurn("tb", 8000, 200)));
        var rtA = new AgentRuntime(ctxA);
        var rtB = new AgentRuntime(ctxB);

        var tA = rtA.RunAsync(Options("A"), CancellationToken.None);
        var tB = rtB.RunAsync(Options("B"), CancellationToken.None);
        var both = await Task.WhenAll(tA.AsTask(), tB.AsTask());
        var ra = both[0]; var rb = both[1];

        Assert.True(ra.Ok, $"A failed: {ra.Note}");
        Assert.True(rb.Ok, $"B failed: {rb.Note}");
        Assert.Equal(1, ra.Turns);
        Assert.Equal(1, rb.Turns);
        Assert.NotNull(ra.FinalAssistant);
        Assert.NotNull(rb.FinalAssistant);
        // Each run's streamed body is exactly what ITS provider produced.
        Assert.Equal(new string('w', 8000), ra.FinalAssistant!.Parts.OfType<TextPart>().Single().Text);
        Assert.Equal(new string('w', 8000), rb.FinalAssistant!.Parts.OfType<TextPart>().Single().Text);
    }
}
