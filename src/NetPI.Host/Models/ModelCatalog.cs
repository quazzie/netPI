using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Models;

/// <summary>
/// Aggregated model catalog over every registered <see cref="IModelProvider"/>. Listing queries providers in parallel
/// (5 s timeout each; a failing provider keeps its last known models) and caches the result until a provider registers,
/// unregisters or publishes <c>models.changed</c>. Streaming normalizes the transcript and runs the
/// <see cref="IModelMiddleware"/> pipeline (lowest <see cref="IModelMiddleware.Order"/> outermost).
/// </summary>
internal sealed class ModelCatalog : IModelCatalog, IDisposable
{
    public const string Source = "host.models";
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceRegistry _services;
    private readonly ISettings _settings;
    private readonly IEventBus _bus;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Debouncer _refresh;
    private readonly Dictionary<string, IReadOnlyList<ModelInfo>> _lastByProvider = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ModelInfo> _cached = [];
    private string _signature = "";
    private bool _dirty = true;
    private Task<IReadOnlyList<ModelInfo>>? _inflight;
    private int _generation;
    private bool _disposed;

    public ModelCatalog(IServiceRegistry services, ISettings settings, IEventBus bus, ILogger log)
    {
        _services = services;
        _settings = settings;
        _bus = bus;
        _log = log;
        _refresh = new Debouncer(TimeSpan.FromMilliseconds(250), () => _ = RefreshQuietlyAsync());
    }

    public IReadOnlyList<ModelInfo> Cached => Volatile.Read(ref _cached);

    public IReadOnlyList<IModelProvider> Providers => _services.GetAll<IModelProvider>();

    public IModelProvider? GetProvider(string providerId)
    {
        foreach (var p in Providers)
            if (string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    public string? DefaultModelRef
    {
        get
        {
            var configured = _settings.Get<string>("defaultModel");
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            var cached = Cached;
            return cached.FirstOrDefault(m => m.IsLocal && m.Status == "loaded")?.Ref ?? cached.FirstOrDefault()?.Ref;
        }
    }

    /// <summary>Mark the cache stale and refresh in the background (publishes models.changed when the set changes).</summary>
    public void Invalidate()
    {
        lock (_gate) _dirty = true;
        _refresh.Trigger();
    }

    /// <summary>Hook for bus/services notifications.</summary>
    public void OnModelsChanged(BusEvent e)
    {
        if (e.Source != Source) Invalidate();
    }

    public void OnServiceChanged(Type type)
    {
        if (typeof(IModelProvider).IsAssignableFrom(type) || type.IsAssignableFrom(typeof(IModelProvider))) Invalidate();
    }

    public async Task<IReadOnlyList<ModelInfo>> ListAsync(bool refresh = false, CancellationToken ct = default)
    {
        Task<IReadOnlyList<ModelInfo>> task;
        lock (_gate)
        {
            if (!refresh && !_dirty && _inflight is null) return _cached;
            if (_inflight is not null && !refresh) task = _inflight;
            else task = _inflight = RefreshAsync(refresh, ++_generation);
        }
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<ModelInfo?> FindAsync(string modelRef, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelRef)) return null;
        modelRef = modelRef.Trim();
        var models = await ListAsync(false, ct).ConfigureAwait(false);
        var slash = modelRef.IndexOf('/');
        if (slash > 0)
        {
            var providerId = modelRef[..slash];
            var id = modelRef[(slash + 1)..];
            var hit = models.FirstOrDefault(m => string.Equals(m.Provider, providerId, StringComparison.OrdinalIgnoreCase) && m.Id == id)
                      ?? models.FirstOrDefault(m => string.Equals(m.Provider, providerId, StringComparison.OrdinalIgnoreCase) &&
                                                    string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
            // The provider exists but did not list it (offline, unlisted id): let the provider decide at call time.
            if (GetProvider(providerId) is { } provider)
                return new ModelInfo { Provider = provider.Id, Id = id, IsLocal = provider.IsLocal, Status = "unknown" };
            // A bare id that itself contains '/' (e.g. "org/model" on an OpenAI-compatible endpoint).
            return models.FirstOrDefault(m => m.Id == modelRef);
        }
        return models.FirstOrDefault(m => m.Id == modelRef)
               ?? models.FirstOrDefault(m => string.Equals(m.Id, modelRef, StringComparison.OrdinalIgnoreCase));
    }

    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Messages = ModelMessages.Normalize(request.Messages);
        ModelCallDelegate pipeline = CallProvider;
        var middleware = _services.GetAll<IModelMiddleware>().OrderBy(m => m.Order).ToList(); // stable: priority order kept for ties
        for (var i = middleware.Count - 1; i >= 0; i--)
        {
            var mw = middleware[i];
            var next = pipeline;
            pipeline = (req, token) => mw.InvokeAsync(req, next, token);
        }
        return pipeline(request, ct);
    }

    public async Task<ChatMessage> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ChatMessage? result = null;
        await foreach (var e in StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
            if (e is StreamCompleted done) result = done.Message;
        return result ?? throw new ModelException($"Model {request.Model.Ref} ended the stream without a completed message", transient: false);
    }

    private IAsyncEnumerable<ModelStreamEvent> CallProvider(ModelRequest request, CancellationToken ct)
    {
        var provider = GetProvider(request.Model.Provider)
                       ?? throw new ModelException($"No model provider '{request.Model.Provider}' is registered (is its plugin loaded?)", transient: false);
        return provider.StreamAsync(request, ct);
    }

    private async Task RefreshQuietlyAsync()
    {
        if (_disposed) return;
        try { await ListAsync(false).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "Model catalog refresh failed"); }
    }

    private async Task<IReadOnlyList<ModelInfo>> RefreshAsync(bool refresh, int generation)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // never run on a caller's UI context
        try
        {
            lock (_gate) _dirty = false;
            var providers = Providers;
            var results = await Task.WhenAll(providers.Select(p => ListProviderAsync(p, refresh))).ConfigureAwait(false);

            var all = new List<ModelInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < providers.Count; i++)
            {
                if (!live.Add(providers[i].Id)) continue; // a shadowed provider with the same id
                foreach (var m in results[i])
                    if (seen.Add(m.Ref)) all.Add(m);
            }

            string signature;
            bool changed;
            lock (_gate)
            {
                foreach (var stale in _lastByProvider.Keys.Where(k => !live.Contains(k)).ToList()) _lastByProvider.Remove(stale);
                signature = Signature(all);
                changed = signature != _signature;
                _signature = signature;
                _cached = all.AsReadOnly();
            }
            if (changed)
                _bus.Publish(new BusEvent { Type = EventTypes.ModelsChanged, Data = new { count = all.Count }, Source = Source });
            return all;
        }
        finally
        {
            lock (_gate)
                if (_generation == generation) _inflight = null;
        }
    }

    private async Task<IReadOnlyList<ModelInfo>> ListProviderAsync(IModelProvider provider, bool refresh)
    {
        using var cts = new CancellationTokenSource(ProviderTimeout);
        try
        {
            var listed = await provider.ListModelsAsync(refresh, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            // Own copy: the provider's collection object may be a plugin type (it must not outlive a plugin unload).
            IReadOnlyList<ModelInfo> models = listed is null ? [] : listed.Where(m => m is not null).ToList();
            lock (_gate) _lastByProvider[provider.Id] = models;
            return models;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) _log.LogWarning("Model provider '{Provider}' did not list its models within {Timeout}s", provider.Id, ProviderTimeout.TotalSeconds);
            else _log.LogWarning("Model provider '{Provider}' failed to list models: {Error}", provider.Id, ex.Message);
            lock (_gate) return _lastByProvider.GetValueOrDefault(provider.Id) ?? [];
        }
    }

    private static string Signature(List<ModelInfo> models)
    {
        var sb = new StringBuilder();
        foreach (var m in models)
            sb.Append(m.Ref).Append('|').Append(m.Status).Append('|').Append(m.ContextWindow).Append('|').Append(m.DisplayName).Append('\n');
        return sb.ToString();
    }

    public void Dispose()
    {
        _disposed = true;
        _refresh.Dispose();
    }
}
