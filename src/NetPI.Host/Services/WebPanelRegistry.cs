using NetPI.Abstractions;

namespace NetPI.Host.Services;

/// <summary>Global registry backing plugin-contributed right-panel tabs.</summary>
public sealed class WebPanelRegistry : IWebPanelRegistry
{
    private readonly object _gate = new();
    private readonly List<WebPanelDefinition> _panels = [];

    public IDisposable Register(WebPanelDefinition panel)
    {
        lock (_gate)
        {
            var existing = _panels.FindIndex(p =>
                string.Equals(p.Id, panel.Id, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) _panels.RemoveAt(existing);
            _panels.Add(panel);
        }
        return new Registration(this, panel);
    }

    public IReadOnlyList<WebPanelDefinition> All()
    {
        lock (_gate)
            return [.. _panels.OrderBy(p => p.Order).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)];
    }

    private void Remove(WebPanelDefinition panel)
    {
        lock (_gate)
        {
            // astra-1 P3: removal by REGISTRATION IDENTITY, not by (id, url)
            // value match — an old handle could otherwise delete a newer
            // replacement that happens to carry the same values.
            var i = _panels.FindIndex(p => ReferenceEquals(p, panel));
            if (i >= 0) _panels.RemoveAt(i);
        }
    }

    private sealed class Registration(WebPanelRegistry owner, WebPanelDefinition panel) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Remove(panel);
        }
    }
}

/// <summary>
/// Plugin-scoped view. Every registration is tracked and removed when the
/// owning generation unloads, just like slash commands.
/// </summary>
public sealed class ScopedWebPanels(WebPanelRegistry global) : IWebPanelRegistry
{
    private readonly List<IDisposable> _handles = [];

    public IDisposable Register(WebPanelDefinition panel)
    {
        var h = global.Register(panel);
        lock (_handles) _handles.Add(h);
        return new Registration(_handles, h);
    }

    public IReadOnlyList<WebPanelDefinition> All() => global.All();

    public void Unload()
    {
        lock (_handles)
        {
            foreach (var h in _handles) h.Dispose();
            _handles.Clear();
        }
    }

    private sealed class Registration(List<IDisposable> handles, IDisposable inner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (handles) handles.Remove(inner);
            inner.Dispose();
        }
    }
}
