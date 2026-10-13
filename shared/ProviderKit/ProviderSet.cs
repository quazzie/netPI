// Compiled into each provider plugin from shared/ProviderKit (plugins do not reference each other): edit it here.
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Providers.Kit;

/// <summary>
/// The providers of one type the owner has added (<see cref="ProviderInstances"/>): one provider per entry, registered and
/// unregistered live as the entries change. <paramref name="create"/> gets the id and a reader of the entry's current settings
/// (so an edited key applies without a new provider); an entry whose <paramref name="identity"/> changed (name, …) is replaced.
/// </summary>
internal sealed class ProviderSet<T> : IDisposable where T : class, IModelProvider
{
    private readonly IPluginContext _ctx;
    private readonly string _type;
    private readonly Func<string, JsonObject, Func<JsonObject?>, T> _create;
    private readonly Func<string, JsonObject, string> _identity;
    private readonly Func<string, JsonObject, string?>? _problem;
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (T Provider, IDisposable Registration, string Identity)> _items = new(StringComparer.OrdinalIgnoreCase);

    public ProviderSet(IPluginContext ctx, string type, Func<string, JsonObject, Func<JsonObject?>, T> create, Func<string, JsonObject, string>? identity = null,
        Func<string, JsonObject, string?>? problem = null)
    {
        _problem = problem;
        _ctx = ctx;
        _type = type;
        _create = create;
        _identity = identity ?? ((_, o) => Name(o));
    }

    /// <summary>The providers now registered, in the order the entries were added.</summary>
    public IReadOnlyList<T> All { get { lock (_gate) return [.. _items.Values.Select(i => i.Provider)]; } }

    /// <summary>The entry's display name: its <c>name</c>, else the kind's title.</summary>
    public static string NameOf(string id, JsonObject o, string fallback) => (o["name"] as JsonValue)?.TryGetValue<string>(out var n) == true && !string.IsNullOrWhiteSpace(n) ? n.Trim() : fallback;
    private static string Name(JsonObject o) => (o["name"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n ?? "" : "";

    /// <summary>Registers the new entries and drops the removed or renamed ones; true when the set changed.</summary>
    public bool Reconcile()
    {
        // an entry that cannot run is left out and said once; a problem that is fixed is reported again if it returns
        var wanted = new List<(string Id, JsonObject Config)>();
        var stillBad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ProviderInstances.Of(_ctx.Settings, _type, includeInvalidIds: true))
        {
            var problem = !ProviderInstances.ValidId(entry.Id) ? "its id may only hold letters, digits, - and _" : _problem?.Invoke(entry.Id, entry.Config);
            if (problem is null) { wanted.Add(entry); continue; }
            stillBad.Add(entry.Id);
            if (_reported.Add(entry.Id)) _ctx.Logger.LogWarning("{Path}: ignoring '{Id}': {Problem}", ProviderInstances.Path, entry.Id, problem);
        }
        _reported.IntersectWith(stillBad);
        var changed = false;
        lock (_gate)
        {
            var ids = wanted.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, item) in _items.ToList())
            {
                var entry = wanted.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
                if (ids.Contains(id) && _identity(id, entry.Config) == item.Identity) continue;
                Dispose(item.Registration);
                _items.Remove(id);
                changed = true;
            }
            foreach (var (id, config) in wanted)
            {
                if (_items.ContainsKey(id)) continue;
                var entryId = id;
                var provider = _create(id, config, () => _ctx.Settings.GetNode(ProviderInstances.PathOf(entryId)) as JsonObject);
                _items[id] = (provider, _ctx.Services.Register<IModelProvider>(provider), _identity(id, config));
                changed = true;
            }
        }
        return changed;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var i in _items.Values) Dispose(i.Registration);
            _items.Clear();
        }
    }

    private static void Dispose(IDisposable d)
    {
        try { d.Dispose(); } catch { /* registry already disposed */ }
    }
}
