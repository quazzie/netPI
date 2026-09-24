using System.Text.Json;

namespace NetPI;

/// <summary>
/// An event on the bus. <see cref="Type"/> is a dotted name such as <c>agent.status</c>.
/// When <see cref="SessionId"/> is set, UI clients only receive the event if they subscribed to that
/// session (high-volume stream events). Events without a session id are broadcast.
/// </summary>
public sealed class BusEvent
{
    public required string Type { get; init; }
    public object? Data { get; init; }
    public string? SessionId { get; init; }
    public string? Source { get; init; }
    /// <summary>Forward to UI clients over the websocket.</summary>
    public bool Ui { get; init; } = true;
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
    public long Seq { get; set; }

    /// <summary>Get the payload as <typeparamref name="T"/> (direct cast or JSON round-trip).</summary>
    public T? As<T>()
    {
        if (Data is T t) return t;
        if (Data is null) return default;
        var target = NetPiJson.For(typeof(T));
        if (Data is JsonElement je) return je.Deserialize<T>(target);
        return NetPiJson.ToElement(Data).Deserialize<T>(target);
    }
}

public interface IEventBus
{
    void Publish(BusEvent evt);

    void Publish(string type, object? data = null, string? sessionId = null, bool ui = true)
        => Publish(new BusEvent { Type = type, Data = data, SessionId = sessionId, Ui = ui });

    /// <summary>
    /// Subscribe to events. Pattern: exact type (<c>agent.status</c>), prefix wildcard (<c>agent.*</c>) or <c>*</c>.
    /// Handlers run on the bus dispatcher in publish order; keep them fast.
    /// </summary>
    IDisposable Subscribe(string pattern, Action<BusEvent> handler);

    IDisposable SubscribeAsync(string pattern, Func<BusEvent, ValueTask> handler);

    /// <summary>Recent events (ring buffer) for diagnostics.</summary>
    IReadOnlyList<BusEvent> Recent(int max = 200);
}

/// <summary>Well-known event type names.</summary>
public static class EventTypes
{
    public const string SessionCreated = "session.created";
    public const string SessionUpdated = "session.updated";
    public const string SessionDeleted = "session.deleted";
    /// <summary>A session was attached to another project or detached: <c>{ sessionId, projectId, cwd }</c>. What the model is
    /// told about it is up to plugins (the context plugin appends a "project" notice).</summary>
    public const string SessionProject = "session.project";
    public const string ProjectCreated = "project.created";
    public const string ProjectUpdated = "project.updated";
    public const string ProjectDeleted = "project.deleted";
    public const string MessageAdded = "message.added";
    public const string MessageUpdated = "message.updated";
    public const string MessagesCompacted = "messages.compacted";
    public const string StreamStart = "stream.start";
    public const string StreamDelta = "stream.delta";
    public const string StreamTool = "stream.tool";
    public const string StreamReset = "stream.reset";
    public const string StreamEnd = "stream.end";
    public const string ToolStart = "tool.start";
    public const string ToolOutput = "tool.output";
    public const string ToolEnd = "tool.end";
    public const string AgentStatus = "agent.status";
    public const string AgentQueue = "agent.queue";
    public const string AgentNotice = "agent.notice";
    public const string SessionContext = "session.context";
    public const string LanesChanged = "lanes.changed";
    public const string ModelsChanged = "models.changed";
    public const string PluginsChanged = "plugins.changed";
    public const string UiChanged = "ui.changed";
    public const string SettingsChanged = "settings.changed";
    public const string UsageRecorded = "usage.recorded";
    public const string ProcessStarted = "process.started";
    public const string ProcessExited = "process.exited";
    public const string ProcessOutput = "process.output";
}
