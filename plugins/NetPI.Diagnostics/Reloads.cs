using System.Globalization;
using System.Text.Json.Nodes;

namespace NetPI.Diagnostics;

/// <summary>
/// The plugin reloads of the last half hour (<c>plugins.reloaded</c>), for the "someone reloaded me" line: a
/// <c>/reload</c> takes every running chat's tools away for a moment, and without this it looks like tools vanished for
/// no reason. A record keeps which plugins, when, which chats were mid-turn, and — because only a plugin that
/// <em>registers tools</em> can take any away — which tools were at stake. A hook-only plugin (context, nudge) swaps
/// under a running turn and announces nothing, and saying otherwise would make this line cry wolf.
/// </summary>
public sealed class Reloads(IPluginContext ctx)
{
    public const int Capacity = 20;
    /// <summary>How long a reload is still worth reporting.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly LinkedList<Record> _list = new();
    private readonly object _gate = new();

    /// <summary>One <c>plugins.reloaded</c> event: which plugins, when, what the host was doing, and what it cost.</summary>
    public sealed record Record(IReadOnlyList<string> Ids, DateTimeOffset Time, string Kind, IReadOnlyList<string> BusySessions,
        IReadOnlyList<string> Tools)
    {
        public string Summary => Kind switch
        {
            "disabled" => $"Plugins switched off: {string.Join(", ", Ids)}",
            "enabled" => $"Plugins switched on: {string.Join(", ", Ids)}",
            "reload-failed" => $"Plugin reload failed, still on the running version: {string.Join(", ", Ids)}",
            "deferred" => $"Plugin reload deferred (plugins.quiet), the running version keeps serving: {string.Join(", ", Ids)}",
            _ => $"Plugins reloaded: {string.Join(", ", Ids)}",
        };

        /// <summary>What this reload means for the chats that were running: the tools at stake, or that there are none.</summary>
        public string Impact => Kind == "deferred"
            ? BusySessions.Count == 0
                ? "nothing was swapped; no chat could notice"
                : $"{BusySessions.Count} chat(s) mid-turn: nothing was swapped, so nothing changed under them"
            : Tools.Count == 0
            ? BusySessions.Count == 0
                ? "no chat was running, and it registers no tools"
                : $"{BusySessions.Count} chat(s) mid-turn; it registers no tools, so nothing is announced"
            : BusySessions.Count == 0
                ? $"{Tools.Count} tool(s) reload ({string.Join(", ", Tools)})"
                : $"{BusySessions.Count} chat(s) mid-turn: {Tools.Count} tool(s) go away for a moment ({string.Join(", ", Tools)}), and each chat that holds one gets a notice";

        public JsonObject ToJson() => new()
        {
            ["ids"] = new JsonArray(Ids.Select(i => (JsonNode?)i).ToArray()),
            ["time"] = Time.ToString("O", CultureInfo.InvariantCulture),
            ["kind"] = Kind,
            ["summary"] = Summary,
            ["tools"] = new JsonArray(Tools.Select(t => (JsonNode?)t).ToArray()),
            ["busySessions"] = new JsonArray(BusySessions.Select(s => (JsonNode?)s).ToArray()),
            ["impact"] = Impact,
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
        // what the reloaded plugins register right now: their tools are the ones a chat can lose (a reload swaps, so a
        // tool that stays registered through it is never actually absent - only a plugin that changed its tools, or one
        // that was switched off, takes any away)
        var tools = ctx.Tools.Registrations
            .Where(r => ids.Contains(r.PluginId, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.Tool.Definition.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        lock (_gate)
        {
            _list.AddFirst(new Record(ids, e.Time, d?["kind"]?.GetValue<string>() ?? "reload", busy, tools));
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
