using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Agent;
using NetPI.Tools;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// docs/plans/file-tool-reliability.md §3B/§4: the AgentRuntime file-group
/// executor — file-target calls grouped by canonical path and run in original
/// call order under a runtime-wide gate, concurrent with other files and
/// non-file calls, with predecessor-failure skip. Uses REAL file tools in a
/// temp workspace plus a recording registry for deterministic observation.
/// </summary>
public class AgentFileGroupTests
{
    /// <summary>One observed tool execution (name, entry/exit ticks, result).</summary>
    internal sealed record Obs(string Tool, int Enter, int Exit, string Text, bool IsError);

    /// <summary>
    /// A registry of REAL tools where every tool is wrapped so each execution
    /// is recorded (entry/exit tick, result text, error flag) — deterministic
    /// observation without touching the runtime. The wrapper declares itself a
    /// file-target tool ONLY when the wrapped tool is one (delegating the
    /// target), so the runtime's grouping sees exactly the real capability.
    /// </summary>
    internal sealed class RecordingRegistry : IToolRegistry
    {
        private readonly List<IAgentTool> _all = [];
        private readonly object _lock = new();
        internal List<Obs> Observed { get; } = [];

        public RecordingRegistry(params IAgentTool[] extras)
        {
            // The runtime resolves tools by NAME, so the real built-in file
            // tools are always present; extras (gating/sleep tools) are added.
            foreach (var t in new IAgentTool[] { new ReadTool(), new WriteTool(), new EditTool(), new ReplaceTool() })
                Register(t);
            foreach (var t in extras) Register(t);
        }

        public IDisposable Register(IAgentTool tool)
        {
            var wrapped = tool is IFileTargetTool f ? (IAgentTool)new FileRec(tool, this, f) : new PlainRec(tool, this);
            _all.Add(wrapped);
            return new Noop();
        }

        /// <summary>A wrapper that delegates the file-target capability.</summary>
        private sealed class FileRec(IAgentTool inner, RecordingRegistry owner, IFileTargetTool fileTarget)
            : IAgentTool, IFileTargetTool
        {
            public string Name => inner.Name;
            public string Description => inner.Description;
            public IReadOnlyList<string> Guidelines => inner.Guidelines;
            public JsonElement Parameters => inner.Parameters;
            public string? GetTargetPath(ToolContext c) => fileTarget.GetTargetPath(c);
            public ValueTask<ToolResult> ExecuteAsync(ToolContext c, CancellationToken ct) => owner.Run(inner, c, ct);
        }

        /// <summary>A plain (non-file) wrapper.</summary>
        private sealed class PlainRec(IAgentTool inner, RecordingRegistry owner) : IAgentTool
        {
            public string Name => inner.Name;
            public string Description => inner.Description;
            public IReadOnlyList<string> Guidelines => inner.Guidelines;
            public JsonElement Parameters => inner.Parameters;
            public ValueTask<ToolResult> ExecuteAsync(ToolContext c, CancellationToken ct) => owner.Run(inner, c, ct);
        }

        /// <summary>Run the inner tool and record it.</summary>
        public ValueTask<ToolResult> Run(IAgentTool inner, ToolContext c, CancellationToken ct)
        {
            var enter = Environment.TickCount;
            return RunAsync();
            async ValueTask<ToolResult> RunAsync()
            {
                var res = await inner.ExecuteAsync(c, ct);
                lock (_lock)
                    Observed.Add(new Obs(inner.Name, enter, Environment.TickCount,
                        string.Join("\n", res.Parts.Select(x => x is TextPart t ? t.Text : x.ToString())), res.IsError));
                return res;
            }
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }

        public IReadOnlyList<IAgentTool> All() => _all;
        public IAgentTool? Find(string name) => _all.FirstOrDefault(t => t.Name == name);

        public List<Obs> Of(string tool) => Observed.Where(o => o.Tool == tool).ToList();
    }

    /// <summary>A file-target tool that holds its file-group gate for a duration.</summary>
    private sealed class HoldTool(string name, string file) : IAgentTool, IFileTargetTool
    {
        public string Name => name;
        public string Description => "holds the file gate";
        public IReadOnlyList<string> Guidelines => [];
        public JsonElement Parameters => JsonSerializer.SerializeToElement(new { type = "object" });
        public string? GetTargetPath(ToolContext c) => c.ResolvePath(file);
        public ValueTask<ToolResult> ExecuteAsync(ToolContext c, CancellationToken ct)
        {
            var ms = c.Arguments.TryGetProperty("ms", out var h) ? h.GetInt32() : 0;
            if (ms > 0) Thread.Sleep(ms);
            return ValueTask.FromResult(new ToolResult("t", Name, [new TextPart("ok")], false));
        }
    }

    /// <summary>A NON-file tool that just sleeps — runs free of any group.</summary>
    private sealed class SleepTool : IAgentTool
    {
        public string Name => "sleep";
        public string Description => "sleeps without a file target";
        public IReadOnlyList<string> Guidelines => [];
        public JsonElement Parameters => JsonSerializer.SerializeToElement(new { type = "object" });
        public ValueTask<ToolResult> ExecuteAsync(ToolContext c, CancellationToken ct)
        {
            var ms = c.Arguments.TryGetProperty("ms", out var h) ? h.GetInt32() : 0;
            if (ms > 0) Thread.Sleep(ms);
            return ValueTask.FromResult(new ToolResult("t", Name, [new TextPart("ok")], false));
        }
    }

    /// <summary>
    /// A file-target tool that holds an "in-flight" slot across a real await,
    /// so concurrently-scheduled groups are simultaneously in-flight. Combined
    /// with a peak-count barrier this deterministically proves the executor runs
    /// different-file groups + non-file calls concurrently (no timing flakiness).
    /// </summary>
    private sealed class BarrierTool(string name, string file, Barrier barrier) : IAgentTool, IFileTargetTool
    {
        public string Name => name;
        public string Description => "holds an in-flight slot across a yield";
        public IReadOnlyList<string> Guidelines => [];
        public JsonElement Parameters => JsonSerializer.SerializeToElement(new { type = "object" });
        public string? GetTargetPath(ToolContext c) => file is null ? null : c.ResolvePath(file);
        public async ValueTask<ToolResult> ExecuteAsync(ToolContext c, CancellationToken ct)
        {
            await barrier.EnterAsync();   // hold the slot across a real yield
            return new ToolResult("t", Name, [new TextPart("ok")], false);
        }
    }

    /// <summary>
    /// Records the PEAK number of peers that were simultaneously in-flight.
    /// Each peer increments, awaits a real delay (holding the slot so the thread
    /// yields and other groups can overlap), then decrements. Peak == n means all
    /// n groups were in-flight at once — the concurrency proof.
    /// </summary>
    private sealed class Barrier
    {
        private readonly object _lock = new();
        private int _inFlight;
        public int Peak { get; private set; }
        public async ValueTask EnterAsync()
        {
            lock (_lock)
            {
                _inFlight++;
                if (_inFlight > Peak) Peak = _inFlight;
            }
            await Task.Delay(50).ConfigureAwait(false); // yield: other groups overlap while we hold
            lock (_lock) _inFlight--;
        }
    }

    private static string MakeWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netpi-filegroup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ToolCallPart Call(string id, string name, object args)
        => new(id, name, JsonSerializer.SerializeToElement(args));

    private static AgentMessage Assistant(params MessagePart[] parts)
        => new("a1", MessageRole.Assistant, parts, DateTimeOffset.UtcNow);

    private static AgentRunOptions Options(string ws, string text) => new()
    {
        SessionId = "s1",
        ModelId = "model",
        Workspace = ws,
        Messages = [new AgentMessage("u1", MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow)],
    };

    private static (AgentRuntime rt, RecordingRegistry reg) MakeRuntime(IModelProvider provider, RecordingRegistry reg, string ws)
    {
        var ctx = new NoopPluginContext();
        ctx.Add("provider", provider);
        ctx.Add("tools", reg);
        return (new AgentRuntime(ctx), reg);
    }

    /// <summary>The tool message's result text for a call id, from the LAST model request.</summary>
    private static string ResultText(FakeProvider provider, string callId)
    {
        foreach (var msg in (provider.LastMessages ?? []).Where(m => m.Role == MessageRole.Tool).Reverse())
        {
            foreach (var p in msg.Parts.OfType<ToolResultPart>())
                if (p.ToolCallId == callId)
                    return string.Join("\n", p.Parts.Select(x => x is TextPart t ? t.Text : x.ToString()));
        }
        return "";
    }

    [Fact]
    public async Task SameFileDependentEdits_ExecuteInCallOrder()
    {
        // write → edit → edit on the SAME file in one batch; the final content
        // is only correct if they ran sequentially in call order.
        var ws = MakeWorkspace();
        try
        {
            var path = Path.Combine(ws, "dep.txt");
            var reg = new RecordingRegistry();
            var provider = new FakeProvider(
                [new ModelStarted("model"), new ModelCompleted(Assistant(
                    Call("c1", "write", new { path = "dep.txt", content = "alpha" }),
                    Call("c2", "edit", new { path = "dep.txt", oldText = "alpha", newText = "beta" }),
                    Call("c3", "edit", new { path = "dep.txt", oldText = "beta", newText = "gamma" })))],
                [new ModelCompleted(Assistant(new TextPart("done")))]);
            var (rt, _) = MakeRuntime(provider, reg, ws);

            var result = await rt.RunAsync(Options(ws, "edit the file"), CancellationToken.None);
            Assert.True(result.Ok);
            // A dependent write→edit→edit chain yields gamma ONLY in call order.
            Assert.Equal("gamma", File.ReadAllText(path));
            // All three ran (no skip), in order.
            Assert.Equal(["write", "edit", "edit"], reg.Observed.Select(o => o.Tool));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task WriteEditReplaceRead_ReadSeesFinalContent()
    {
        // write → edit → replace → read on one path: the read result must carry
        // the post-replace content, proving the group ran to completion in order.
        var ws = MakeWorkspace();
        try
        {
            var reg = new RecordingRegistry();
            var provider = new FakeProvider(
                [new ModelStarted("model"), new ModelCompleted(Assistant(
                    Call("c1", "write", new { path = "mix.txt", content = "one two two" }),
                    Call("c2", "edit", new { path = "mix.txt", oldText = "one", newText = "ONE" }),
                    Call("c3", "replace", new { path = "mix.txt", oldText = "two", newText = "TWO", expectedCount = 2 }),
                    Call("c4", "read", new { path = "mix.txt" })))],
                [new ModelCompleted(Assistant(new TextPart("done")))]);
            var (rt, _) = MakeRuntime(provider, reg, ws);

            var result = await rt.RunAsync(Options(ws, "mixed"), CancellationToken.None);
            Assert.True(result.Ok);
            Assert.Equal("ONE TWO TWO", File.ReadAllText(Path.Combine(ws, "mix.txt")));
            // The READ's own result carries the final content (it ran last in the group).
            var read = Assert.Single(reg.Of("read"));
            Assert.Contains("ONE TWO TWO", read.Text);
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task DifferentFilesAndNonFile_RunConcurrently()
    {
        // Two DIFFERENT file groups + one non-file call must all be IN-FLIGHT at
        // the same moment. A shared barrier records the max simultaneous in-flight
        // count: == 3 proves different-file groups and non-file calls stay
        // concurrent (only the SAME-file group is serialized).
        var ws = MakeWorkspace();
        try
        {
            var barrier = new Barrier();
            var reg = new RecordingRegistry(
                new BarrierTool("barA", "a.txt", barrier),
                new BarrierTool("barB", "b.txt", barrier),
                new BarrierTool("barF", null, barrier));   // non-file (runs free)
            var provider = new FakeProvider(
                [new ModelStarted("model"), new ModelCompleted(Assistant(
                    Call("c1", "barA", new { }),
                    Call("c2", "barB", new { }),
                    Call("c3", "barF", new { })))],
                [new ModelCompleted(Assistant(new TextPart("done")))]);
            var (rt, _) = MakeRuntime(provider, reg, ws);

            var result = await rt.RunAsync(Options(ws, "conc"), CancellationToken.None);
            Assert.True(result.Ok);
            // All three groups were in-flight at once — the concurrency proof.
            Assert.Equal(3, barrier.Peak);
            Assert.All(reg.Observed, o => Assert.False(o.IsError));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task FailingPredecessor_SkipsRestOfGroupWithExplicitError()
    {
        // First call in a file group fails (bad edit → 0 matches); the second
        // call in the SAME group is skipped with an error naming the predecessor.
        var ws = MakeWorkspace();
        try
        {
            var path = Path.Combine(ws, "f.txt");
            File.WriteAllText(path, "keep");
            var reg = new RecordingRegistry();
            var provider = new FakeProvider(
                [new ModelStarted("model"), new ModelCompleted(Assistant(
                    Call("c1", "edit", new { path = "f.txt", oldText = "nope", newText = "x" }),
                    Call("c2", "edit", new { path = "f.txt", oldText = "keep", newText = "KEPT" })))],
                [new ModelCompleted(Assistant(new TextPart("done")))]);
            var (rt, _) = MakeRuntime(provider, reg, ws);

            var result = await rt.RunAsync(Options(ws, "fail"), CancellationToken.None);
            Assert.True(result.Ok);
            // c1 errored (0 matches) → c2 skipped; the file is untouched.
            Assert.Equal("keep", File.ReadAllText(path));
            var edits = reg.Of("edit");
            Assert.Single(edits); // c2 never entered the tool
            Assert.True(edits[0].IsError);
            // And the batch-level result for c2 names the failed predecessor.
            var r2 = ResultText(provider, "c2");
            Assert.Contains("skipped", r2, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("c1", r2, StringComparison.Ordinal);
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task FailureInOneFileGroup_DoesNotBlockOtherFile()
    {
        // A failing call in file A's group must not prevent file B's group.
        var ws = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(ws, "a.txt"), "a");
            File.WriteAllText(Path.Combine(ws, "b.txt"), "a");
            var reg = new RecordingRegistry();
            var provider = new FakeProvider(
                [new ModelStarted("model"), new ModelCompleted(Assistant(
                    Call("c1", "edit", new { path = "a.txt", oldText = "missing", newText = "x" }),
                    Call("c2", "edit", new { path = "b.txt", oldText = "a", newText = "A" })))],
                [new ModelCompleted(Assistant(new TextPart("done")))]);
            var (rt, _) = MakeRuntime(provider, reg, ws);

            var result = await rt.RunAsync(Options(ws, "iso"), CancellationToken.None);
            Assert.True(result.Ok);
            // c2 targets b.txt — an independent group — so it still succeeds.
            Assert.Equal("A", File.ReadAllText(Path.Combine(ws, "b.txt")));
            Assert.Equal("a", File.ReadAllText(Path.Combine(ws, "a.txt")));
            var ok = Assert.Single(reg.Of("edit"), o => !o.IsError);
            Assert.Contains("Edited", ok.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(ws, true); }
    }
}
