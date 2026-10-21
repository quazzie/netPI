using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NetPI;

public sealed class ReasoningInfo
{
    public bool Supported { get; set; }
    public List<string> Efforts { get; set; } = [];
    public string? Default { get; set; }
}

/// <summary>
/// What <c>models.changed</c> is about: every listed fact of every model, in one string. The slot count is in it because
/// the agents read their shared capacity from the cache on that event, and the limits, modalities and reasoning because
/// the UI and the callers re-read the list on it. The host catalog and the providers' list caches share this one.
/// </summary>
public static class ModelSignature
{
    public static string Of(IEnumerable<ModelInfo> models)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in models)
        {
            sb.Append(m.Ref).Append('|').Append(m.Status).Append('|').Append(m.ContextWindow).Append('|').Append(m.DisplayName)
                .Append('|').Append(m.MaxOutputTokens).Append('|').Append(m.Concurrency).Append('|');
            if (m.InputModalities is { } modalities)   // a provider's own object: defensive about a null it should not hand over
                foreach (var modality in modalities) sb.Append(modality).Append(',');
            sb.Append('|');
            if (m.Reasoning is { } reasoning)
            {
                sb.Append(reasoning.Supported ? "reasoning:" : "no-reasoning:").Append(reasoning.Default).Append(':');
                if (reasoning.Efforts is { } efforts)
                    foreach (var effort in efforts) sb.Append(effort).Append(',');
            }
            sb.Append('|').Append(m.IsLocal).Append('|').Append(m.Extra?.ToJsonString()).Append('\n');
        }
        return sb.ToString();
    }
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
    /// <summary>
    /// Max silence between stream events for this model, in seconds, overriding the retry plugin's global
    /// <c>retry.stallTimeoutSeconds</c> (null = the global). 0 disables the stall detection. A provider whose calls
    /// can be legitimately silent for a long time (the Claude CLI works with its own tools and subagents) sets this;
    /// the process dying is detected by the stream ending, not by silence.
    /// </summary>
    public int? StallTimeoutSeconds { get; set; }
    /// <summary>
    /// The model's backend keeps and compacts the conversation itself (the Claude CLI resumes its own session): NetPI's compaction leaves such
    /// a chat alone, because summarizing NetPI's copy of the messages would not shrink what the backend holds. <see cref="ContextWindow"/> and the
    /// context size the provider reports are the backend's own.
    /// </summary>
    public bool ManagesOwnContext { get; set; }
    /// <summary>
    /// The model works with tools of its own (the Claude CLI runs its built-in ones) and NetPI's tools are not offered to it: a feature
    /// that needs the model to call one of NetPI's (goal_update) cannot run on it, and the provider holds its own tools to what NetPI
    /// offers the run (<see cref="ModelRequest.Tools"/>) and to <see cref="ModelRequest.ReadOnlyTools"/> (idea-hghbmg).
    /// </summary>
    public bool RunsOwnTools { get; set; }
    /// <summary>loaded | unloaded | stopped | offline | available</summary>
    public string? Status { get; set; }
    public bool IsLocal { get; set; }
    public JsonObject? Extra { get; set; }

    [JsonIgnore] public bool SupportsImages => InputModalities.Contains("image");
}

/// <summary>A request to a model. Messages are provider-neutral; providers convert them.</summary>
public sealed class ModelRequest
{
    /// <summary>Stable caller-generated diagnostic correlation; never sent to the provider.</summary>
    public string? CorrelationId { get; init; }
    /// <summary>
    /// Bookkeeping shared by the middlewares of one call. A hot swap briefly runs two generations of the same
    /// middleware (the old one is removed only after the new one serves); a mark here is how the second one sees
    /// that the first already reserved, so the call is metered exactly once.
    /// </summary>
    public JsonObject? PipelineState { get; set; }
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
    /// <summary>
    /// The run may only read (plan mode, or a research subagent of a chat in plan mode). NetPI's own tools are held to it by their
    /// hooks; a provider whose model runs tools of its own (<see cref="ModelInfo.RunsOwnTools"/>) must hold those to it too. Set by
    /// the plugin that owns the policy, in a model middleware, before the provider is called (idea-hghbmg).
    /// </summary>
    public bool ReadOnlyTools { get; set; }
}

/// <summary>Streaming events produced by a provider. The stream must end with <see cref="StreamCompleted"/>.</summary>
public abstract record ModelStreamEvent;
public sealed record TextDelta(string Text) : ModelStreamEvent;
public sealed record ThinkingDelta(string Text) : ModelStreamEvent;
public sealed record ToolCallStarted(string Id, string Name) : ModelStreamEvent;
public sealed record ToolCallArgsDelta(string Id, string Delta) : ModelStreamEvent;
/// <summary>A call the provider runs itself (<see cref="ToolCallPart.ProviderExecuted"/>) has its arguments and is now being worked on:
/// the chat shows it as running (<c>tool.start</c>), not as still being written.</summary>
public sealed record ProviderToolRunning(string Id, string Name, string Arguments) : ModelStreamEvent;
/// <summary>Progress of such a call (a subagent's own tool use): live output of the call's row (<c>tool.output</c>).</summary>
public sealed record ProviderToolOutput(string Id, string Text) : ModelStreamEvent;
/// <summary>Such a call has finished (<c>tool.end</c>). <paramref name="Content"/>: its result as the provider reported it, shown at
/// once (<c>tool.result</c>); it is stored as the call's tool message after the assistant message, as without it.</summary>
public sealed record ProviderToolDone(string Id, bool IsError, string? Content) : ModelStreamEvent;
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
    /// <summary><see cref="Exception.Message"/> without <see cref="Detail"/>: what a person reads, where the ids and the
    /// dump path are noise. The message itself when there is no <see cref="Detail"/>, or when it does not end with it
    /// (a provider may have written a message of its own).</summary>
    public string DisplayMessage
    {
        get
        {
            if (Detail is not { Length: > 0 } detail) return Message;
            var tail = $"[{detail}]";
            var msg = Message.AsSpan().TrimEnd();
            return msg.EndsWith(tail) ? msg[..^tail.Length].TrimEnd().ToString() : Message;
        }
    }
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
