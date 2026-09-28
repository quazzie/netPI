using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NetPI;

public sealed class ReasoningInfo
{
    public bool Supported { get; set; }
    public List<string> Efforts { get; set; } = [];
    public string? Default { get; set; }
}

/// <summary>A model offered by a provider. <see cref="Ref"/> ("provider/model") is the global key.</summary>
public sealed class ModelInfo
{
    public required string Provider { get; init; }
    public required string Id { get; init; }
    public string Ref => $"{Provider}/{Id}";
    public string? DisplayName { get; set; }
    public int? ContextWindow { get; set; }
    public int? MaxOutputTokens { get; set; }
    /// <summary>Parallel slots the backend serves (the agents on a local model share them). Null = unknown.</summary>
    public int? Concurrency { get; set; }
    public List<string> InputModalities { get; set; } = ["text"];
    public ReasoningInfo? Reasoning { get; set; }
    /// <summary>loaded | unloaded | stopped | offline | available</summary>
    public string? Status { get; set; }
    public bool IsLocal { get; set; }
    public JsonObject? Extra { get; set; }

    [JsonIgnore] public bool SupportsImages => InputModalities.Contains("image");
}

/// <summary>A request to a model. Messages are provider-neutral; providers convert them.</summary>
public sealed class ModelRequest
{
    public required ModelInfo Model { get; init; }
    public string? SystemPrompt { get; set; }
    public required IReadOnlyList<ChatMessage> Messages { get; set; }
    public IReadOnlyList<ToolDefinition> Tools { get; set; } = [];
    public string? ReasoningEffort { get; set; }
    public int? MaxOutputTokens { get; set; }
    public double? Temperature { get; set; }
    public string? SessionId { get; init; }
    public string? AgentId { get; init; }
    /// <summary>agent | compaction | title | other</summary>
    public string Purpose { get; init; } = "agent";
}

/// <summary>Streaming events produced by a provider. The stream must end with <see cref="StreamCompleted"/>.</summary>
public abstract record ModelStreamEvent;
public sealed record TextDelta(string Text) : ModelStreamEvent;
public sealed record ThinkingDelta(string Text) : ModelStreamEvent;
public sealed record ToolCallStarted(string Id, string Name) : ModelStreamEvent;
public sealed record ToolCallArgsDelta(string Id, string Delta) : ModelStreamEvent;
public sealed record UsageUpdate(Usage Usage) : ModelStreamEvent;
/// <summary>Discard everything streamed so far in this call (used by retry middleware).</summary>
public sealed record StreamReset(string Reason) : ModelStreamEvent;
/// <summary>Informational notice (e.g. "retrying in 4s").</summary>
public sealed record StreamNotice(string Text, string Level = "info") : ModelStreamEvent;
/// <summary>The fully assembled assistant message (parts, usage, stop reason).</summary>
public sealed record StreamCompleted(ChatMessage Message) : ModelStreamEvent;

/// <summary>A model backend (plugin-provided).</summary>
public interface IModelProvider
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>Local models: their agents share the catalog's concurrency, and they are free.</summary>
    bool IsLocal { get; }
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(bool refresh, CancellationToken ct);
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct);
}

/// <summary>Thrown by providers. <see cref="Transient"/> errors are retried by the retry plugin.</summary>
public sealed class ModelException(string message, bool transient, int? statusCode = null, string? errorType = null, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Transient { get; } = transient;
    public int? StatusCode { get; } = statusCode;
    public string? ErrorType { get; } = errorType;
    /// <summary>True when the provider reported a context-length overflow.</summary>
    public bool ContextOverflow { get; init; }
    /// <summary>How long the server asked to wait before trying again (HTTP <c>Retry-After</c>), when it said.</summary>
    public TimeSpan? RetryAfter { get; init; }
    /// <summary>The provider's own decoration of <see cref="Exception.Message"/> (request, response and generation ids,
    /// the path of the saved failed request). It stays in the message — the log, <c>diag</c> and a bug report want it —
    /// and is also named here, so a UI that shows the failure to a person can leave it out (idea-qz1a5z).</summary>
    public string? Detail { get; init; }
}

public delegate IAsyncEnumerable<ModelStreamEvent> ModelCallDelegate(ModelRequest request, CancellationToken ct);

/// <summary>Wraps every model call (retry, logging, budgets...). Lower Order runs outermost.</summary>
public interface IModelMiddleware
{
    int Order => 0;
    IAsyncEnumerable<ModelStreamEvent> InvokeAsync(ModelRequest request, ModelCallDelegate next, CancellationToken ct);
}

/// <summary>Aggregated model catalog over all registered providers (host service).</summary>
public interface IModelCatalog
{
    Task<IReadOnlyList<ModelInfo>> ListAsync(bool refresh = false, CancellationToken ct = default);
    /// <summary>Find by "provider/model" ref (or a bare model id if unambiguous).</summary>
    Task<ModelInfo?> FindAsync(string modelRef, CancellationToken ct = default);
    IReadOnlyList<ModelInfo> Cached { get; }
    IModelProvider? GetProvider(string providerId);
    IReadOnlyList<IModelProvider> Providers { get; }
    /// <summary>Stream through the middleware pipeline to the owning provider.</summary>
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct);
    /// <summary>Convenience: run a request to completion and return the assistant message.</summary>
    Task<ChatMessage> CompleteAsync(ModelRequest request, CancellationToken ct);
    /// <summary>Default model ref from settings (or first available).</summary>
    string? DefaultModelRef { get; }
}
