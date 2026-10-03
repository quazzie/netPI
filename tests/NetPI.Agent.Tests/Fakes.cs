using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NetPI.Agent.Tests;

// The Disposer and the recording services/settings/rpc/tools/ui fakes are shared (tests/Shared/Fakes.cs);
// what stays here is the Agent suite's: the async-dispatch bus and the scripted model plumbing.

// ------------------------------------------------------------------ event bus (async dispatcher like the host)

public sealed class FakeBus : IEventBus, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<(string Pattern, Func<BusEvent, ValueTask> Handler)> _subs = [];
    private readonly List<BusEvent> _all = [];
    private readonly Channel<BusEvent> _queue = Channel.CreateUnbounded<BusEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private long _seq;
    private long _dispatched;

    public FakeBus()
    {
        _pump = Task.Run(async () =>
        {
            await foreach (var evt in _queue.Reader.ReadAllAsync())
            {
                List<(string Pattern, Func<BusEvent, ValueTask> Handler)> subs;
                lock (_gate) subs = [.. _subs];
                foreach (var (pattern, handler) in subs)
                {
                    if (!Matches(pattern, evt.Type)) continue;
                    try { await handler(evt); }
                    catch (Exception ex) { Console.WriteLine($"    [bus] handler for {evt.Type} failed: {ex.Message}"); }
                }
                Interlocked.Increment(ref _dispatched);
            }
        });
    }

    private static bool Matches(string pattern, string type) =>
        pattern == "*" || pattern == type || (pattern.EndsWith(".*", StringComparison.Ordinal) && type.StartsWith(pattern[..^1], StringComparison.Ordinal));

    public void Publish(BusEvent evt)
    {
        lock (_gate)
        {
            evt.Seq = ++_seq;
            _all.Add(evt);
        }
        _queue.Writer.TryWrite(evt);
    }

    public IDisposable Subscribe(string pattern, Action<BusEvent> handler) =>
        SubscribeAsync(pattern, e => { handler(e); return ValueTask.CompletedTask; });

    public IDisposable SubscribeAsync(string pattern, Func<BusEvent, ValueTask> handler)
    {
        var entry = (pattern, handler);
        lock (_gate) _subs.Add(entry);
        return new Disposer(() => { lock (_gate) _subs.Remove(entry); });
    }

    public IReadOnlyList<BusEvent> Recent(int max = 200)
    {
        lock (_gate) return _all.TakeLast(max).ToList();
    }

    public List<BusEvent> All
    {
        get { lock (_gate) return [.. _all]; }
    }

    public List<BusEvent> OfType(string type) => All.Where(e => e.Type == type).ToList();

    /// <summary>JSON view of an event payload.</summary>
    public static JsonObject Data(BusEvent e) => (NetPiJson.ToNode(e.Data) as JsonObject) ?? new JsonObject();

    /// <summary>Wait until every event published so far has been dispatched.</summary>
    public async Task DrainAsync()
    {
        long target;
        lock (_gate) target = _seq;
        await Wait.Until(() => Interlocked.Read(ref _dispatched) >= target, "bus drained", 5000);
    }

    public void Dispose() => _queue.Writer.TryComplete();
}

// ------------------------------------------------------------------ models

public delegate IAsyncEnumerable<ModelStreamEvent> ModelHandler(ModelRequest request, CancellationToken ct);

public sealed class FakeProvider(string id, bool isLocal, FakeCatalog catalog) : IModelProvider
{
    public string Id { get; } = id;
    public string DisplayName => Id;
    public bool IsLocal { get; } = isLocal;

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(bool refresh, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelInfo>>(catalog.Cached.Where(m => m.Provider == Id).ToList());

    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct) => catalog.Invoke(request, ct);
}

public sealed class FakeCatalog(IServiceRegistry services) : IModelCatalog
{
    private readonly Lock _gate = new();
    private readonly List<ModelInfo> _models = [];
    private readonly Dictionary<string, FakeProvider> _providers = [];
    public ConcurrentQueue<ModelRequest> Requests { get; } = new();
    public int Calls => Requests.Count;

    /// <summary>The scripted model. Default: reply "ok".</summary>
    public ModelHandler Handler { get; set; } = (_, _) => Reply.Text("ok");

    public string? DefaultModelRef { get; set; }

    public void AddModel(ModelInfo model)
    {
        lock (_gate)
        {
            _models.Add(model);
            if (!_providers.ContainsKey(model.Provider)) _providers[model.Provider] = new FakeProvider(model.Provider, model.IsLocal, this);
        }
        DefaultModelRef ??= model.Ref;
    }

    public IReadOnlyList<ModelInfo> Cached { get { lock (_gate) return [.. _models]; } }

    /// <summary>Called by <see cref="ListAsync"/> (simulates a catalog that loads lazily).</summary>
    public Action? OnList { get; set; }

    public Task<IReadOnlyList<ModelInfo>> ListAsync(bool refresh = false, CancellationToken ct = default)
    {
        OnList?.Invoke();
        return Task.FromResult(Cached);
    }

    public Task<ModelInfo?> FindAsync(string modelRef, CancellationToken ct = default)
    {
        var all = Cached;
        var m = all.FirstOrDefault(x => string.Equals(x.Ref, modelRef, StringComparison.OrdinalIgnoreCase));
        if (m is null)
        {
            var byId = all.Where(x => string.Equals(x.Id, modelRef, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byId.Count == 1) m = byId[0];
        }
        return Task.FromResult(m);
    }

    public IModelProvider? GetProvider(string providerId) { lock (_gate) return _providers.GetValueOrDefault(providerId); }
    public IReadOnlyList<IModelProvider> Providers { get { lock (_gate) return [.. _providers.Values]; } }

    internal IAsyncEnumerable<ModelStreamEvent> Invoke(ModelRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return Handler(request, ct);
    }

    /// <summary>Normalize the transcript (like the host), then run the middleware pipeline to the provider.</summary>
    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct)
    {
        var normalized = new ModelRequest
        {
            CorrelationId = request.CorrelationId,
            Model = request.Model,
            SystemPrompt = request.SystemPrompt,
            Messages = ModelMessages.Normalize(request.Messages),
            Tools = request.Tools,
            ReasoningEffort = request.ReasoningEffort,
            MaxOutputTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            SessionId = request.SessionId,
            AgentId = request.AgentId,
            Purpose = request.Purpose,
        };
        var provider = GetProvider(request.Model.Provider) ?? throw new ModelException($"No provider {request.Model.Provider}", false);
        ModelCallDelegate pipeline = provider.StreamAsync;
        foreach (var mw in services.GetAll<IModelMiddleware>().OrderByDescending(m => m.Order))
        {
            var next = pipeline;
            var m = mw;
            pipeline = (r, c) => m.InvokeAsync(r, next, c);
        }
        return pipeline(normalized, ct);
    }

    public async Task<ChatMessage> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        await foreach (var e in StreamAsync(request, ct))
            if (e is StreamCompleted c) return c.Message;
        throw new ModelException("no completion", false);
    }
}

/// <summary>Scripted model replies.</summary>
public static class Reply
{
    public static Usage DefaultUsage() => new() { InputTokens = 100, OutputTokens = 10 };

    public static ChatMessage Message(params MessagePart[] parts) => new()
    {
        Role = MessageRole.Assistant,
        Parts = [.. parts],
        Usage = DefaultUsage(),
        StopReason = parts.OfType<ToolCallPart>().Any() ? "tool_use" : "stop",
    };

    public static ToolCallPart Call(string name, object? args = null, string? id = null) => new()
    {
        Id = id ?? "call_" + Ids.Short(10),
        Name = name,
        Arguments = args is string s ? s : NetPiJson.Serialize(args ?? new { }),
    };

    /// <summary>Stream a message: deltas for every part, optionally a gate before completion, then StreamCompleted.</summary>
    public static async IAsyncEnumerable<ModelStreamEvent> Stream(ChatMessage final, Func<CancellationToken, Task>? gate = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        foreach (var part in final.Parts)
        {
            switch (part)
            {
                case ThinkingPart th:
                    yield return new ThinkingDelta(th.Text);
                    break;
                case TextPart t:
                    for (var i = 0; i < t.Text.Length; i += 4) yield return new TextDelta(t.Text.Substring(i, Math.Min(4, t.Text.Length - i)));
                    break;
                case ToolCallPart c:
                    yield return new ToolCallStarted(c.Id, c.Name);
                    yield return new ToolCallArgsDelta(c.Id, c.Arguments);
                    break;
            }
        }
        if (gate is not null) await gate(ct);
        ct.ThrowIfCancellationRequested();
        if (final.Usage is not null) yield return new UsageUpdate(final.Usage);
        yield return new StreamCompleted(final);
    }

    public static IAsyncEnumerable<ModelStreamEvent> Text(string text, Func<CancellationToken, Task>? gate = null) =>
        Stream(Message(new TextPart { Text = text }), gate);

    public static IAsyncEnumerable<ModelStreamEvent> Tools(params ToolCallPart[] calls) => Stream(Message([.. calls]));

    public static IAsyncEnumerable<ModelStreamEvent> Tool(string name, object? args = null) => Tools(Call(name, args));

    public static async IAsyncEnumerable<ModelStreamEvent> Fail(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>
    /// The last user-visible text of the (normalized) request: the last User message, skipping the context notices
    /// (working directory, instruction files) that the harness appends before a session's first model call.
    /// </summary>
    public static string LastUser(ModelRequest r) => r.Messages.LastOrDefault(m => m.Role == MessageRole.User && !IsContextNotice(m))?.Text ?? "";

    public static bool IsContextNotice(ChatMessage m) =>
        m.Text.StartsWith("<system-notice kind=\"project\">", StringComparison.Ordinal)
        || m.Text.StartsWith("<system-notice kind=\"instructions\">", StringComparison.Ordinal)
        || m.Text.StartsWith("<system-notice kind=\"tools\">", StringComparison.Ordinal);

    /// <summary>Messages after the last assistant message (what is new this turn).</summary>
    public static List<ChatMessage> Tail(ModelRequest r)
    {
        var idx = -1;
        for (var i = r.Messages.Count - 1; i >= 0; i--)
            if (r.Messages[i].Role == MessageRole.Assistant) { idx = i; break; }
        return r.Messages.Skip(idx + 1).ToList();
    }

    public static bool HasToolResult(ModelRequest r) => r.Messages.LastOrDefault()?.Role == MessageRole.Tool;
}

// ------------------------------------------------------------------ fake tool

public sealed class FakeTool(string name, Func<ToolContext, JsonElement, CancellationToken, Task<ToolResult>> exec, bool readOnly = false, string category = "general",
    IReadOnlyList<string>? guidelines = null, string? help = null) : IAgentTool
{
    public int Calls;
    public ToolDefinition Definition { get; } = new()
    {
        Name = name,
        Description = $"Fake tool {name}. Does test things.",
        Help = help,
        ReadOnly = readOnly,
        Category = category,
        Label = name.ToUpperInvariant(),
        PromptGuidelines = guidelines ?? [$"Use {name} for testing."],
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return exec(context, args, ct);
    }
}

// ------------------------------------------------------------------ logger

public sealed class ConsoleLogger(string category, bool verbose) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => verbose ? logLevel >= LogLevel.Debug : logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Console.WriteLine($"    [{category} {logLevel}] {formatter(state, exception)}{(exception is null ? "" : " — " + exception.GetType().Name + ": " + exception.Message)}");
    }
}
