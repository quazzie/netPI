using NetPI.Abstractions;

namespace NetPI.Host.Services;

/// <summary>
/// Hot-reloadable command registry (PLAN §37). Plugins register commands
/// through their scoped <see cref="ICommandRegistry"/> view (see
/// <see cref="PluginScopedCommands"/>); the host removes them on unload.
/// The Web plugin resolves this under id "commands" to answer
/// <c>commands.list</c> requests.
/// </summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly List<CommandDefinition> _commands = [];
    private readonly object _lock = new();

    public IDisposable Register(CommandDefinition command)
    {
        lock (_lock) _commands.Add(command);
        return new Removable(_commands, command);
    }

    public IReadOnlyList<CommandDefinition> All()
    {
        lock (_lock)
            return [.. _commands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public CommandDefinition? Find(string name)
    {
        var n = name.StartsWith('/') ? name : "/" + name;
        lock (_lock)
            return _commands.FirstOrDefault(c =>
                string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Removable(List<CommandDefinition> list, CommandDefinition cmd) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (list) list.Remove(cmd);
        }
    }
}

/// <summary>
/// Plugin-scoped view: every registration is owned by the plugin generation
/// and removed automatically on unload (mirrors <c>PluginContextServices</c>).
/// </summary>
public sealed class PluginScopedCommands(ICommandRegistry inner) : ICommandRegistry
{
    private readonly List<IDisposable> _disposables = [];
    public IDisposable Register(CommandDefinition command)
    {
        var d = inner.Register(command);
        _disposables.Add(d);
        return new Aggregate(_disposables, d);
    }
    public IReadOnlyList<CommandDefinition> All() => inner.All();
    public CommandDefinition? Find(string name) => inner.Find(name);

    /// <summary>Called by the host on plugin unload; removes all commands.</summary>
    public void Unload()
    {
        foreach (var d in _disposables) d.Dispose();
        _disposables.Clear();
    }

    private sealed class Aggregate(List<IDisposable> all, IDisposable one) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (all) { all.Remove(one); one.Dispose(); }
        }
    }
}

/// <summary>
/// Per-plugin view of the global command registry (PLAN §37). Every command a
/// plugin registers through this handle is removed automatically when
/// <see cref="Unload"/> is called on plugin generation unload, so commands
/// are hot-reloadable. Mirrors <c>PluginContextServices</c>.
/// </summary>
public sealed class ScopedCommands : ICommandRegistry
{
    private readonly CommandRegistry _global;
    private readonly List<IDisposable> _handles = [];
    public ScopedCommands(CommandRegistry global) => _global = global;

    public IDisposable Register(CommandDefinition command)
    {
        var h = _global.Register(command);
        _handles.Add(h);
        return h;
    }
    public IReadOnlyList<CommandDefinition> All() => _global.All();
    public CommandDefinition? Find(string name) => _global.Find(name);
    public void Unload()
    {
        foreach (var h in _handles) h.Dispose();
        _handles.Clear();
    }
}
