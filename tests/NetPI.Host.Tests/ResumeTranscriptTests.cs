using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Agent;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §15 C4 — "no duplicate tool outputs after resume". When a run re-enters
/// (a resume segment, a crash recovery, a host restart), the runner REBUILDS the
/// transcript from the durable session store (BuildTranscriptAsync → ReadRecentAsync
/// or the compaction tail) — it never carries the prior segment's in-memory
/// transcript forward. If two copies of a persisted entry ever reached the store
/// (a re-entrant append, a double-persist, a replayed batch), the rebuild would
/// send the tool output to the model TWICE — the provider rejects the transcript
/// or the model sees phantom duplicated results. This test seeds a real SQLite
/// session store with a complete tool batch (assistant tool-call + tool-result)
/// and starts a fresh segment in the same session: the rebuilt transcript must
/// contain each entry exactly once.
/// </summary>
public sealed class ResumeTranscriptTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); }
            catch (IOException) { }
    }

    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    /// <summary>
    /// Like NoopPluginContext but with a real SqliteSessionStore so the runner's
    /// BuildTranscriptAsync rebuild path (store.ReadRecentAsync) is exercised.
    /// The provider is created here and handed out so the test can seed its
    /// scripted turns AFTER the runner is built.
    /// </summary>
    private sealed class Ctx : IPluginContext
    {
        private readonly FixedServiceRegistry _services = new();
        public Ctx(ISessionStore s) { _services.Put("provider", Provider); _services.Put("sessions", s); }
        public FakeProvider Provider { get; } = new();
        public PluginInfo Info => new("test", "test", "1.0.0");
        public IServiceRegistry Services => _services;
        public IEventBus Events => new NoopBus();
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => throw new NotSupportedException();
        public JsonElement OwnConfig => JsonDocument.Parse("{}").RootElement;
        public IPluginLogger Log => new NullLogger();
        public IValueLease<object> LeaseSelf() => new NoopLease();
        private sealed class NoopBus : IEventBus
        {
            public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null) => new Noop();
            public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
            private sealed class Noop : IDisposable { public void Dispose() { } }
        }
        private sealed class NoopLease : IValueLease<object>
        {
            public object Value => this;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private async Task<(AgentRunner Runner, SqliteSessionStore Store, FakeProvider Provider)> MakeAsync()
    {
        var dir = Directory.CreateTempSubdirectory("netpi-resume-").FullName;
        _dirs.Add(dir);
        var store = new SqliteSessionStore(Path.Combine(dir, "test.db"));
        var ctx = new Ctx(store);
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 1);
        return (runner, store, ctx.Provider);
    }

    [Fact]
    public async Task ResumedSegment_TranscriptRebuilds_EachToolOutputOnce()
    {
        var (runner, store, provider) = await MakeAsync();

        // Seed the session exactly as the runtime persists a completed tool batch:
        //   user  — the first request
        //   asst  — the model's tool call
        //   tool  — the tool result
        var session = (await store.CreateAsync(null, CancellationToken.None)).Id;
        var now = DateTimeOffset.UtcNow;
        var toolCallId = "call-1";
        await store.AppendAsync(new SessionEntry("u1", session, EntryKind.Message,
            new AgentMessage("u1", MessageRole.User, [new TextPart("do it")], now.AddSeconds(-10)),
            null, now.AddSeconds(-10)), CancellationToken.None);
        await store.AppendAsync(new SessionEntry("a1", session, EntryKind.Message,
            new AgentMessage("a1", MessageRole.Assistant,
                [new ToolCallPart(toolCallId, "echo", JsonSerializer.SerializeToElement(new { msg = "hello" }))],
                now.AddSeconds(-8)),
            null, now.AddSeconds(-8)), CancellationToken.None);
        await store.AppendAsync(new SessionEntry("t1", session, EntryKind.Message,
            new AgentMessage("t1", MessageRole.Tool,
                [new ToolResultPart(toolCallId, "echo", [new TextPart("echo hello")], false)],
                now.AddSeconds(-6)),
            null, now.AddSeconds(-6)), CancellationToken.None);

        // A fresh segment in the SAME session (the resume/restart path): its
        // transcript is REBUILT from the store — not carried over.
        var start = await runner.StartRunAsync(new AgentRunRequest(
            session, null, "m-1", "second request"), CancellationToken.None);
        Assert.Equal(RunDisposition.Admitted, start.Disposition);
        await runner.RunTask;
        var outcome = runner.GetRun(start.RunId!);
        Assert.True(outcome!.Outcome == RunState.Completed,
            $"the resumed segment must complete, not {outcome.Outcome} (state {outcome.State})");

        // The provider saw the rebuilt transcript: the tool output exactly once,
        // the tool call exactly once, and no duplicated message ids.
        var msgs = provider.LastMessages!;
        var toolResults = msgs.Where(m => m.Parts.OfType<ToolResultPart>().Any()).ToList();
        Assert.True(toolResults.Count == 1,
            $"the persisted tool output must appear exactly once in the rebuilt transcript; saw {toolResults.Count} tool-result message(s): {string.Join(" | ", msgs.Select(m => $"{m.Id}:{m.Role}"))}");
        var toolCalls = msgs.SelectMany(m => m.Parts.OfType<ToolCallPart>()).Count(p => p.Id == toolCallId);
        Assert.True(toolCalls == 1, $"the persisted tool call must appear exactly once; saw {toolCalls}");
        Assert.True(msgs.Count == msgs.Select(m => m.Id).Distinct().Count(),
            $"the rebuilt transcript must not contain two copies of any message; {msgs.Count} messages, {msgs.Select(m => m.Id).Distinct().Count()} distinct ids");
    }
}
