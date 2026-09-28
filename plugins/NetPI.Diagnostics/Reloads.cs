using System.Globalization;
using System.Text.Json.Nodes;

namespace NetPI.Diagnostics;

/// <summary>
/// The plugin reloads of the last half hour (<c>plugins.reloaded</c>), for the "someone reloaded me" line: a
/// <c>/reload</c> takes every running chat's tools away for a moment, and without this it looks like tools vanished for
/// no reason. Each record keeps which sessions were mid-turn at the time — the ones that got a "tools" notice because
/// of it.
/// </summary>
public sealed class Reloads(IPluginContext ctx)
{
    public const int Capacity = 20;
    /// <summary>How long a reload is still worth reporting.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly LinkedList<Record> _list = new();
    private readonly object _gate = new();

    /// <summary>One <c>plugins.reloaded</c> event and the chats that were running when it happened.</summary>
    public sealed record Record(IReadOnlyList<string> Ids, DateTimeOffset Time, string Kind, IReadOnlyList<string> BusySessions)
    {
        public string Summary => Kind switch
        {
            "disabled" => $"Plugins switched off: {string.Join(", ", Ids)}",
            "enabled" => $"Plugins switched on: {string.Join(", ", Ids)}",
            _ => $"Plugins reloaded: {string.Join(", ", Ids)}",
        };

        public JsonObject ToJson() => new()
        {
            ["ids"] = new JsonArray(Ids.Select(i => (JsonNode?)i).ToArray()),
            ["time"] = Time.ToString("O", CultureInfo.InvariantCulture),
            ["kind"] = Kind,
            ["summary"] = Summary,
            ["busySessions"] = new JsonArray(BusySessions.Select(s => (JsonNode?)s).ToArray()),
            ["ago"] = Inspector.Ago(DateTimeOffset.UtcNow - Time),
        };
    }

    public void OnEvent(BusEvent e)
    {
        var d = e.As<JsonObject>();
        var ids = d?["ids"] is JsonArray a ? a.Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? [] : [];
        if (ids.Count == 0) return;
        var busy = ctx.Services.Get<IAgentRuntime>()?.List(false)
            .Where(x => x.Status is AgentStatus.Running or AgentStatus.Queued)
            .Select(x => x.SessionId).Distinct(StringComparer.Ordinal).ToList() ?? [];
        lock (_gate)
        {
            _list.AddFirst(new Record(ids, e.Time, d?["kind"]?.GetValue<string>() ?? "reload", busy));
            while (_list.Count > Capacity) _list.RemoveLast();
            while (_list.Last is { } old && DateTimeOffset.UtcNow - old.Value.Time > Window) _list.RemoveLast();
        }
    }

    /// <summary>The reloads of the last <paramref name="window"/> (default <see cref="Window"/>), newest first.</summary>
    public IReadOnlyList<Record> Recent(TimeSpan? window = null)
    {
        var since = DateTimeOffset.UtcNow - (window ?? Window);
        lock (_gate) return [.. _list.Where(r => r.Time >= since)];
    }

    /// <summary>The reloads as JSON, newest first (<c>diag.overview</c>).</summary>
    public JsonArray ToJson(TimeSpan? window = null) => new([.. Recent(window).Select(r => (JsonNode?)r.ToJson())]);
}
