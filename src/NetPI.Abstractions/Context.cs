namespace NetPI;

/// <summary>Input for building a system prompt.</summary>
public sealed class PromptContext
{
    public required SessionInfo Session { get; init; }
    public ProjectInfo? Project { get; init; }
    public required string Cwd { get; init; }
    public required ModelInfo Model { get; init; }
    public required IReadOnlyList<ToolDefinition> Tools { get; init; }
    public AgentInfo? Agent { get; init; }
    public bool IsSubagent => Agent?.IsSubagent ?? false;
    /// <summary>Extra instructions (e.g. subagent role) appended at the end.</summary>
    public string? Instructions { get; init; }
}

/// <summary>
/// A section of the system prompt. Register with <c>context.Services.Register&lt;IPromptSection&gt;(...)</c>;
/// the context plugin renders all sections in ascending <see cref="Order"/>. Return null/empty to skip.
/// Suggested orders: 0 identity, 100 environment, 200 tools, 300 guidelines, 500 project instructions, 900 extra.
/// </summary>
public interface IPromptSection
{
    string Id { get; }
    int Order { get; }
    ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct);
}

/// <summary>Builds the full system prompt (provided by the context plugin).</summary>
public interface ISystemPromptBuilder
{
    ValueTask<string> BuildAsync(PromptContext context, CancellationToken ct);
}
