using Microsoft.Extensions.Logging;

namespace NetPI.Host.Registries;

/// <summary>
/// Agent tools. The effective tool for a name is the registration with the highest priority (ties: latest);
/// names listed in the <c>tools.disabled</c> setting are excluded. Publishes <c>tools.changed</c> (debounced).
/// </summary>
internal sealed class ToolRegistry : IToolRegistry, IDisposable
{
    public const string ToolsChanged = "tools.changed";

    private sealed record Entry(IAgentTool Tool, string Name, string PluginId, int Priority, long Seq);

    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly ISettings _settings;
    private readonly IEventBus _bus;
    private readonly ILogger _log;
    private readonly Debouncer _changed;
    private HashSet<string> _disabled;
    private long _seq;
    private IReadOnlyList<IAgentTool>? _all;
    private Dictionary<string, IAgentTool>? _byName;

    public ToolRegistry(ISettings settings, IEventBus bus, ILogger log)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _disabled = ReadDisabled();
        _changed = new Debouncer(TimeSpan.FromMilliseconds(100),
            () => _bus.Publish(new BusEvent { Type = ToolsChanged, Data = new { }, Source = "host" }));
    }

    public IDisposable Register(IAgentTool tool, int priority = 0) => Register(tool, priority, "host");

    public IDisposable Register(IAgentTool tool, int priority, string pluginId)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var name = tool.Definition?.Name;
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool definition must have a name");
        var entry = new Entry(tool, name, pluginId, priority, 0);
        lock (_gate)
        {
            entry = entry with { Seq = ++_seq };
            if (_entries.Any(e => e.Name == name && e.Priority == priority))
                _log.LogDebug("Tool '{Name}' from {Plugin} shadows an earlier registration with the same priority", name, pluginId);
            _entries.Add(entry);
            Invalidate();
        }
        _changed.Trigger();
        return new Registration(() =>
        {
            lock (_gate)
            {
                if (!_entries.Remove(entry)) return;
                Invalidate();
            }
            _changed.Trigger();
        });
    }

    public IReadOnlyList<IAgentTool> All
    {
        get
        {
            lock (_gate)
            {
                Build();
                return _all!;
            }
        }
    }

    public IAgentTool? Get(string name)
    {
        lock (_gate)
        {
            Build();
            return _byName!.GetValueOrDefault(name);
        }
    }

    public IReadOnlyList<ToolRegistration> Registrations
    {
        get
        {
            lock (_gate)
                return _entries.OrderBy(e => e.Name, StringComparer.Ordinal).ThenByDescending(e => e.Priority).ThenByDescending(e => e.Seq)
                    .Select(e => new ToolRegistration(e.Tool, e.PluginId, e.Priority)).ToList();
        }
    }

    public bool IsDisabled(string name)
    {
        lock (_gate) return _disabled.Contains(name);
    }

    /// <summary>Re-read <c>tools.disabled</c> (called on settings.changed).</summary>
    public void OnSettingsChanged()
    {
        var disabled = ReadDisabled();
        lock (_gate)
        {
            if (disabled.SetEquals(_disabled)) return;
            _disabled = disabled;
            Invalidate();
        }
        _changed.Trigger();
    }

    private HashSet<string> ReadDisabled() =>
        new(_settings.Get<List<string>>("tools.disabled") ?? [], StringComparer.Ordinal);

    private void Invalidate()
    {
        _all = null;
        _byName = null;
    }

    private void Build()
    {
        if (_all is not null) return;
        var best = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var e in _entries)
        {
            if (!best.TryGetValue(e.Name, out var cur) || e.Priority > cur.Priority || (e.Priority == cur.Priority && e.Seq > cur.Seq))
                best[e.Name] = e;
        }
        var effective = best.Values.Where(e => !_disabled.Contains(e.Name)).OrderBy(e => e.Seq).ToList();
        _all = effective.Select(e => e.Tool).ToList().AsReadOnly();
        _byName = effective.ToDictionary(e => e.Name, e => e.Tool, StringComparer.Ordinal);
    }

    public void Dispose() => _changed.Dispose();
}
