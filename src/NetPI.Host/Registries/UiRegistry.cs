namespace NetPI.Host.Registries;

/// <summary>
/// Plugin UI tabs and slash commands. The host fills <see cref="UiTabInfo.PluginId"/>/<see cref="UiTabInfo.Version"/>
/// (the version changes on every plugin load and whenever its wwwroot changes, for cache busting).
/// Publishes <c>ui.changed</c> (debounced).
/// </summary>
internal sealed class UiRegistry : IUiRegistry, IDisposable
{
    private sealed record TabEntry(UiTabInfo Tab, long Seq);
    private sealed record CommandEntry(SlashCommandInfo Command, long Seq);

    private readonly Lock _gate = new();
    private readonly List<TabEntry> _tabs = [];
    private readonly List<CommandEntry> _commands = [];
    private readonly Debouncer _changed;
    private long _seq;

    public UiRegistry(IEventBus bus)
    {
        _changed = new Debouncer(TimeSpan.FromMilliseconds(100),
            () => bus.Publish(new BusEvent { Type = EventTypes.UiChanged, Data = new { }, Source = "host" }));
    }

    public IDisposable AddTab(UiTabInfo tab) => AddTab(tab, tab.PluginId ?? "host", tab.Version);

    public IDisposable AddTab(UiTabInfo tab, string pluginId, string? version)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(tab.Id);
        tab.PluginId = pluginId;
        tab.Version = version;
        var entry = new TabEntry(tab, 0);
        lock (_gate)
        {
            entry = entry with { Seq = ++_seq };
            _tabs.Add(entry);
        }
        _changed.Trigger();
        return new Registration(() =>
        {
            lock (_gate) if (!_tabs.Remove(entry)) return;
            _changed.Trigger();
        });
    }

    public IDisposable AddCommand(SlashCommandInfo command) => AddCommand(command, command.PluginId ?? "host");

    public IDisposable AddCommand(SlashCommandInfo command, string pluginId)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        command.PluginId = pluginId;
        var entry = new CommandEntry(command, 0);
        lock (_gate)
        {
            entry = entry with { Seq = ++_seq };
            _commands.Add(entry);
        }
        _changed.Trigger();
        return new Registration(() =>
        {
            lock (_gate) if (!_commands.Remove(entry)) return;
            _changed.Trigger();
        });
    }

    public IReadOnlyList<UiTabInfo> Tabs
    {
        get
        {
            lock (_gate)
                return _tabs
                    .GroupBy(t => (t.Tab.PluginId, t.Tab.Id))
                    .Select(g => g.MaxBy(t => t.Seq)!.Tab)
                    .OrderBy(t => t.Order).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
    }

    public IReadOnlyList<SlashCommandInfo> Commands
    {
        get
        {
            lock (_gate)
                return _commands
                    .GroupBy(c => c.Command.Name.TrimStart('/'), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.MaxBy(c => c.Seq)!.Command)
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
    }

    /// <summary>New UI version for a plugin's tabs (wwwroot changed).</summary>
    public void SetVersion(string pluginId, string version)
    {
        var changed = false;
        lock (_gate)
            foreach (var t in _tabs)
                if (string.Equals(t.Tab.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                {
                    t.Tab.Version = version;
                    changed = true;
                }
        if (changed) _changed.Trigger();
    }

    /// <summary>Force a ui.changed event (e.g. a plugin's static files changed).</summary>
    public void NotifyChanged() => _changed.Trigger();

    public void Dispose() => _changed.Dispose();
}
