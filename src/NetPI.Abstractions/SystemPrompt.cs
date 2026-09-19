using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>Structured inputs for building the system prompt (PLAN §46).</summary>
public sealed record SystemPromptInputs
{
    /// <summary>The base prompt (AGENTS.md etc. resolved by the context plugin).</summary>
    public string BasePrompt { get; init; } = string.Empty;

    /// <summary>Tool definitions to include in the prompt.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];

    /// <summary>Free-form guidelines added by plugins.</summary>
    public IReadOnlyList<string> ToolGuidelines { get; init; } = [];

    /// <summary>Environment block (OS, cwd, shell id, ...).</summary>
    public SystemPromptSection Environment { get; init; } = new("environment");

    /// <summary>Context files discovered in the workspace (.netpi/...).</summary>
    public IReadOnlyList<SystemPromptSection> ContextFiles { get; init; } = [];

    /// <summary>Sections appended by plugins (order-preserving).</summary>
    public IReadOnlyList<SystemPromptSection> CustomSections { get; init; } = [];

    /// <summary>Append-only prompt tail (APPEND_SYSTEM.md style).</summary>
    public string AppendPrompt { get; init; } = string.Empty;
}

/// <summary>A named section of the system prompt.</summary>
public sealed record SystemPromptSection(string Title, string Content = "");

/// <summary>Flattens structured inputs into the final system prompt text (PLAN §46).</summary>
public interface ISystemPromptProvider
{
    ValueTask<string> BuildAsync(SystemPromptInputs inputs, CancellationToken cancellationToken = default);
}
