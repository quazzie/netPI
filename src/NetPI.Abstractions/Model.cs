using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>Metadata describing an available model (PLAN §13/§14).</summary>
public sealed record ModelInfo(
    string ModelId,
    string Provider,
    string DisplayName,
    bool SupportsTools,
    bool SupportsThinking,
    int? ContextWindowTokens = null,
    int? MaxOutputTokens = null)
{
    public override string ToString() => $"{ModelId} ({DisplayName})";
}

/// <summary>Provider-neutral request for one model run.</summary>
public sealed record ModelRequest
{
    public string ModelId { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public IReadOnlyList<AgentMessage> Messages { get; init; } = [];
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
    public JsonElement? Thinking { get; init; }
    public string? Seed { get; init; }
    public int? MaxTokens { get; init; }
    public float? Temperature { get; init; }
}

/// <summary>Tool definition exposed to the model.</summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters)
{
    public override string ToString() => Name;
}

/// <summary>Model/provider service surface. Owned by a provider plugin (later phase).</summary>
public interface IModelProvider
{
    /// <summary>
    /// Execute one model run, streaming normalized <see cref="ModelEvent"/>s.
    /// The final event is always a <see cref="ModelCompleted"/> carrying the
    /// full assistant <see cref="AgentMessage"/>.
    /// </summary>
    IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>Catalog of available models (PLAN §13).</summary>
public interface IModelCatalog
{
    /// <summary>Current list of models. Refreshes are performed by the provider plugin.</summary>
    IReadOnlyList<ModelInfo> Models { get; }

    /// <summary>True if the catalog has been populated at least once.</summary>
    bool IsStale { get; }

    /// <summary>Timestamp of the last successful refresh, if any.</summary>
    DateTimeOffset? LastRefreshedAt { get; }

    ValueTask<IReadOnlyList<ModelInfo>> RefreshAsync(CancellationToken cancellationToken);
}
