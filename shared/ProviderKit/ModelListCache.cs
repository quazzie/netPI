// Compiled into each provider plugin from shared/ProviderKit (plugins do not reference each other): edit it here.
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Providers.Kit;

/// <summary>
/// The model list a provider offers: fetched once per fingerprint and TTL, the last good list kept (marked
/// <c>offline</c>) when a refetch fails, and <c>models.changed</c> published only when the list really changed.
/// The providers share this and state their own failure policy in <see cref="Policy"/>.
/// </summary>
internal sealed class ModelListCache(string providerId, ILogger log, IEventBus? events)
{
    private sealed record Entry(IReadOnlyList<ModelInfo> Models, string Fingerprint, DateTimeOffset Expires);

    /// <summary>How this provider wants its model list fetched and what to do when that fails.</summary>
    public sealed record Policy
    {
        /// <summary>Identifies the endpoint's settings: a different one (another key or base url) refetches.</summary>
        public required string Fingerprint { get; init; }
        /// <summary>Whether a fetch is attempted at all; when false the list is empty - and cached like any other.</summary>
        public required bool CanFetch { get; init; }
        /// <summary>How long a list is served before it is fetched again.</summary>
        public required TimeSpan Ttl { get; init; }
        /// <summary>The endpoint a failed fetch names in the log.</summary>
        public string Url { get; init; } = "";
        /// <summary>How long a failed attempt is remembered before another is made; null keeps <see cref="Ttl"/>.</summary>
        public TimeSpan? FailureTtl { get; init; }
        /// <summary>What to offer when a fetch fails, instead of the last good list: the static capability table.</summary>
        public Func<IReadOnlyList<ModelInfo>>? OnFailure { get; init; }
        /// <summary>Log the failure once until a fetch succeeds again (Anthropic logs every time).</summary>
        public bool LogOnce { get; init; } = true;
    }

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile Entry? _cache;
    private IReadOnlyList<ModelInfo>? _lastGood;
    private string? _lastGoodFingerprint;
    private string? _signature;
    private bool _failureLogged;

    /// <summary>True when the list was fetched before and the endpoint settings changed since.</summary>
    public bool SettingsChanged(string fingerprint)
    {
        var c = _cache;
        return c is not null && c.Fingerprint != fingerprint;
    }

    /// <summary>Drop the cached list (the next call refetches).</summary>
    public void Invalidate() => _cache = null;

    public async Task<IReadOnlyList<ModelInfo>> ListAsync(bool refresh, Policy policy,
        Func<CancellationToken, Task<IReadOnlyList<ModelInfo>>> fetch, CancellationToken ct)
    {
        var cache = _cache;
        if (!refresh && Fresh(cache)) return cache!.Models;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cache = _cache;
            if (!refresh && Fresh(cache)) return cache!.Models;

            IReadOnlyList<ModelInfo> models;
            var ttl = policy.Ttl;
            if (!policy.CanFetch) models = [];
            else
            {
                try
                {
                    models = await fetch(ct).ConfigureAwait(false);
                    _lastGood = models;
                    _lastGoodFingerprint = policy.Fingerprint;
                    if (_failureLogged) log.LogInformation("{Provider}: model list available again ({Count} models)", providerId, models.Count);
                    _failureLogged = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (!policy.LogOnce || !_failureLogged)
                        log.LogWarning("{Provider}: cannot list models from {Url}: {Error}", providerId, policy.Url, ex.Message);
                    _failureLogged = true;
                    models = Failed(policy);
                    if (policy.FailureTtl is { } failureTtl && failureTtl < ttl) ttl = failureTtl;
                }
            }

            _cache = new Entry(models, policy.Fingerprint, DateTimeOffset.UtcNow + ttl);
            PublishIfChanged(models);
            return models;
        }
        finally { _lock.Release(); }

        bool Fresh(Entry? e) => e is not null && e.Fingerprint == policy.Fingerprint && e.Expires > DateTimeOffset.UtcNow;
    }

    /// <summary>What a failed fetch leaves on offer: the caller's own list, else the last good one marked offline.</summary>
    private IReadOnlyList<ModelInfo> Failed(Policy policy)
    {
        if (policy.OnFailure is { } fallback) return fallback();
        return _lastGood is { } last && _lastGoodFingerprint == policy.Fingerprint
            ? last.Select(m => { var c = Clone(m); c.Status = "offline"; return c; }).ToList()
            : [];
    }

    private void PublishIfChanged(IReadOnlyList<ModelInfo> models)
    {
        var sb = new StringBuilder();
        foreach (var m in models)
        {
            sb.Append(m.Id).Append('|').Append(m.Status).Append('|').Append(m.ContextWindow).Append('|').Append(m.MaxOutputTokens)
              .Append('|').Append(m.Concurrency).Append('|').Append(string.Join(',', m.InputModalities)).Append('|')
              .Append(m.Reasoning is null ? "-" : $"{m.Reasoning.Supported}:{string.Join(',', m.Reasoning.Efforts)}:{m.Reasoning.Default}")
              .Append('|').Append(m.DisplayName).Append('\n');
        }
        var sig = sb.ToString();
        if (sig == _signature) return;
        var first = _signature is null;
        _signature = sig;
        if (first && models.Count == 0) return; // nothing -> nothing is not a change
        try { events?.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = providerId }); }
        catch (Exception ex) { log.LogDebug(ex, "{Provider}: publishing models.changed failed", providerId); }
    }

    /// <summary>A copy of a catalog entry: the list the cache keeps must not be mutated when it is marked offline.</summary>
    private static ModelInfo Clone(ModelInfo m) => new()
    {
        Provider = m.Provider, Id = m.Id, DisplayName = m.DisplayName, ContextWindow = m.ContextWindow,
        MaxOutputTokens = m.MaxOutputTokens, Concurrency = m.Concurrency, InputModalities = [.. m.InputModalities],
        Reasoning = m.Reasoning is null ? null : new ReasoningInfo { Supported = m.Reasoning.Supported, Efforts = [.. m.Reasoning.Efforts], Default = m.Reasoning.Default },
        Status = m.Status, IsLocal = m.IsLocal, Extra = m.Extra?.DeepClone() as JsonObject,
    };
}