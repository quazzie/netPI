using NetPI.Abstractions;
using NetPI.Agent;
using NetPI.Context.Pi;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §10: the session's persisted mode is runtime/prompt policy — an
/// ORCHESTRATE-mode run gets the concise coordinator guidance section in its
/// system prompt, the default chat mode gets nothing extra (chat-mode prompt
/// bytes are byte-identical to the pre-§10 prompt), and the mode persists
/// through the session store (SetModeAsync → GetAsync).
/// </summary>
public class OrchestrateModePromptTests
{
    // The guidance must survive the round-trip through the prompt builder;
    // assert on stable lines rather than the whole block so wording tweaks in
    // the runner cannot silently break the prompt.
    private const string GuidanceSentinel = "Orchestration mode";
    private const string GuidanceLine1 = "Define concrete deliverables, dependencies, workspaces, and acceptance checks";

    // ------------------------------------------------------------------
    // Runner-level: the mode resolved from the session store shapes the
    // system prompt the model actually receives.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("orchestrate", true)]
    [InlineData("chat", false)]
    [InlineData(null, false)]
    public async Task SessionMode_ShapesSystemPrompt(string? mode, bool expectGuidance)
    {
        var (runner, ctx, provider) = New(mode);

        var start = await runner.StartRunAsync(new AgentRunRequest("s1", null, "model", "hi"));
        Assert.Equal(RunDisposition.Admitted, start.Disposition);
        Assert.True(string.IsNullOrEmpty(start.Note), $"unexpected start note: {start.Note}");
        Assert.NotNull(start.RunId);
        // The provider captures the system message synchronously inside
        // RunAsync (before any terminal state), so waiting on it is race-free.
        await WaitUntil(() => provider.SystemMessages.Count == 1);

        var system = provider.SystemMessages.Single();
        if (expectGuidance)
        {
            Assert.Contains($"## {GuidanceSentinel}", system);
            Assert.Contains(GuidanceLine1, system);
        }
        else
        {
            Assert.DoesNotContain($"## {GuidanceSentinel}", system);
        }
    }

    /// <summary>
    /// Regression guard for the 90% case: with no mode (the default chat run),
    /// the runner contributes no custom section, so the flattened prompt is
    /// byte-identical to what the provider would build without the runner's
    /// §10 involvement.
    /// </summary>
    [Fact]
    public async Task ChatMode_PromptIsByteIdenticalToProviderBaseline()
    {
        var (runner, ctx, provider) = New(mode: null);

        var start = await runner.StartRunAsync(new AgentRunRequest("s1", null, "model", "hi"));
        Assert.NotNull(start.RunId);
        await WaitUntil(() => provider.SystemMessages.Count == 1);

        // The chat-mode runner contributes NO custom section, so the flattened
        // prompt must be byte-identical to what the provider builds from the same
        // builder inputs (empty tools/guidelines add nothing) — the strongest
        // practical statement of "no regression for the 90% case".
        var system = provider.SystemMessages.Single();
        var baseline = await ctx.SystemPromptProvider.BuildAsync(
            new SystemPromptInputs { BasePrompt = "base prompt" }, CancellationToken.None);
        Assert.Equal(baseline, system);
        Assert.DoesNotContain("Orchestration mode", system);
        Assert.DoesNotContain("coordinating", system, System.StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Store-level: SetModeAsync persists and GetAsync returns the mode.
    // ------------------------------------------------------------------

    [Fact]
    public async Task SessionStore_SetMode_PersistsAndReturns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netpi-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new SqliteSessionStore(Path.Combine(dir, "t.db"));
            var s = await store.CreateAsync(null);

            Assert.Null((await store.GetAsync(s.Id))!.Mode);

            await store.SetModeAsync(s.Id, "orchestrate");
            Assert.Equal("orchestrate", (await store.GetAsync(s.Id))!.Mode);

            // SetModelAsync-style normalization: case/whitespace collapses.
            await store.SetModeAsync(s.Id, "  ORCHESTRATE  ");
            Assert.Equal("orchestrate", (await store.GetAsync(s.Id))!.Mode);

            // Clearing returns to the default (null = chat).
            await store.SetModeAsync(s.Id, null);
            Assert.Null((await store.GetAsync(s.Id))!.Mode);
            store.Dispose(); // release the pooled connection so the dir deletes on Windows
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* pooled sqlite handle may outlive the test; temp cleanup is best-effort */ }
        }
    }

    [Fact]
    public async Task SessionStore_ModeSurvivesStoreReopen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netpi-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "t.db");
        try
        {
            using var store = new SqliteSessionStore(dbPath);
            var s = await store.CreateAsync(null);
            await store.SetModeAsync(s.Id, "orchestrate");

            // A NEW store instance (plugin reload) sees the persisted mode.
            using var reopened = new SqliteSessionStore(dbPath);
            Assert.Equal("orchestrate", (await reopened.GetAsync(s.Id))!.Mode);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* pooled sqlite handle may outlive the test; temp cleanup is best-effort */ }
        }
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static (AgentRunner runner, TestContext ctx, CapturingProvider provider) New(string? mode)
    {
        var ctx = new TestContext();
        var provider = new CapturingProvider();
        ctx.Add("provider", provider);
        ctx.Add("sessions", new ModeStore(mode));
        ctx.Add("system-prompt", new SystemPromptProvider());
        ctx.Add("workspace-context", new StaticBuilder());
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 4);
        return (runner, ctx, provider);
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 20, int max = 300)
    {
        for (var i = 0; i < max && !cond(); i++) await Task.Delay(ms);
    }

    private sealed class TestContext : IPluginContext
    {
        private readonly FixedServiceRegistry _services = new();
        private readonly NoopBus _bus = new();
        public SystemPromptProvider SystemPromptProvider =>
            (SystemPromptProvider)_services.Resolve<ISystemPromptProvider>("system-prompt");

        public PluginInfo Info => new("test", "test", "1.0.0");
        public IServiceRegistry Services => _services;
        public IEventBus Events => _bus;
        public ICommandRegistry Commands => new NetPI.Host.Services.CommandRegistry();
        public IWebPanelRegistry WebPanels => new NetPI.Host.Services.WebPanelRegistry();
        public System.Text.Json.JsonElement OwnConfig =>
            System.Text.Json.JsonDocument.Parse("{}").RootElement;
        public IPluginLogger Log => new NoopLogger();
        public IValueLease<object> LeaseSelf() => new NoopLease();
        public void Add(string id, object service) => _services.Put(id, service);

        private sealed class NoopLease : IValueLease<object>
        {
            public object Value => this;
            public void Dispose() { }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
        private sealed class NoopLogger : IPluginLogger
        {
            public void Debug(string m) { }
            public void Information(string m) { }
            public void Warning(string m) { }
            public void Error(string m, Exception? e = null) { }
        }
    }

    private sealed class NoopBus : IEventBus
    {
        public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler,
            EventSubscriptionOptions? options = null) => new Noop();
        public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
            where TEvent : notnull => ValueTask.CompletedTask;
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    /// <summary>Captures the system message of the run's first model request.</summary>
    private sealed class CapturingProvider : IModelProvider
    {
        public List<string> SystemMessages { get; } = [];

        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            var sys = request.Messages?.OfType<AgentMessage>()
                .Where(m => m.Role == MessageRole.System)
                .Select(m => m.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? string.Empty)
                .FirstOrDefault();
            if (sys is not null)
            {
                lock (SystemMessages) SystemMessages.Add(sys);
            }
            yield return new ModelStarted("model");
            yield return new ModelCompleted(new AgentMessage("a", MessageRole.Assistant,
                [new TextPart("ok")], DateTimeOffset.UtcNow));
            await Task.CompletedTask;
        }
    }

    private sealed class ModeStore(string? mode) : ISessionStore
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
            => ValueTask.FromResult(new SessionInfo("s1", null, Now, Now, 0, null));
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult<SessionInfo?>(new SessionInfo(id, null, Now, Now, 0, null) { Mode = mode });
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>([]);
        public ValueTask<int> CountAsync(CancellationToken ct = default) => ValueTask.FromResult(0);
        public ValueTask RenameAsync(string id, string title, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(string sessionId, int offset, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadBeforeAsync(string sessionId, int beforeSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(string sessionId, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<ProjectChangeResult> SetProjectAsync(ProjectChangeRequest change, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Stable, mode-independent builder inputs for the prompt tests.</summary>
    private sealed class StaticBuilder : IWorkspaceContextBuilder
    {
        public ValueTask<SystemPromptInputs> BuildAsync(string workspace, CancellationToken ct = default)
            => ValueTask.FromResult(new SystemPromptInputs { BasePrompt = "base prompt" });
    }
}
