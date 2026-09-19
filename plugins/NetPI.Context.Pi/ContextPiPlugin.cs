using NetPI.Abstractions;

namespace NetPI.Context.Pi;

/// <summary>
/// The reloadable context plugin (PLAN §43, §46). Discovers workspace context
/// files (AGENTS.md, .netpi/AGENTS.override.md, SYSTEM.md, APPEND_SYSTEM.md)
/// and registers an <see cref="ISystemPromptProvider"/> that flattens
/// <see cref="SystemPromptInputs"/> into the final system prompt.
/// </summary>
public sealed class ContextPlugin : INetPiPlugin
{
    private ISystemPromptProvider? _provider;

    public PluginInfo Info { get; } = new("netPI.Context.Pi", "Context / System Prompt", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _provider = new SystemPromptProvider();
        context.Services.Register<ISystemPromptProvider>("system-prompt", _provider);
        context.Services.Register<IWorkspaceContextBuilder>("workspace-context", new WorkspaceContextBuilder());
        context.Log.Information("Context plugin ready");
        await ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _provider = null;
        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>
/// Discovers workspace context files. Files are resolved from the workspace
/// root, walking up for the shared AGENTS.md.
/// </summary>
public sealed class ContextDiscovery
{
    /// <summary>Read a file if it exists, else null.</summary>
    /// <summary>Read a file synchronously if it exists, else null.</summary>
    public static string? ReadOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
    /// <summary>
    /// Find AGENTS.md in <paramref name="workspace"/> or any ancestor (stop at
    /// the filesystem root). Returns the path or null.
    /// </summary>
    public static string? FindAgentsFile(string workspace)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(workspace));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "AGENTS.md");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static SystemPromptInputs BuildForWorkspace(string workspace, CancellationToken ct = default)
    {
        var netpiDir = Path.Combine(workspace, ".netpi");

        // Base prompt: .netpi/AGENTS.override.md > workspace AGENTS.md > walked-up AGENTS.md.
        var basePrompt =
            ReadOrNull(Path.Combine(netpiDir, "AGENTS.override.md"))
            ?? ReadOrNull(Path.Combine(workspace, "AGENTS.md"))
            ?? ReadOrNull(FindAgentsFile(workspace) ?? string.Empty)
            ?? string.Empty;

        // Context files.
        var contextFiles = new List<SystemPromptSection>();
        var agentsOverride = ReadOrNull(Path.Combine(netpiDir, "AGENTS.override.md"));
        if (agentsOverride is not null)
            contextFiles.Add(new SystemPromptSection("AGENTS.override.md", agentsOverride));
        var systemMd = ReadOrNull(Path.Combine(netpiDir, "SYSTEM.md"));
        if (systemMd is not null)
            contextFiles.Add(new SystemPromptSection("SYSTEM.md", systemMd));

        // Append-only tail.
        var appendPrompt =
            ReadOrNull(Path.Combine(netpiDir, "APPEND_SYSTEM.md"))
            ?? ReadOrNull(Path.Combine(netpiDir, "append_system.md"))
            ?? string.Empty;

        return new SystemPromptInputs
        {
            BasePrompt = basePrompt,
            ContextFiles = contextFiles,
            AppendPrompt = appendPrompt,
            Environment = new SystemPromptSection("environment",
                $"OS: {Environment.OSVersion}\nCWD: {Path.GetFullPath(workspace)}"),
        };
    }
}

/// <summary>
/// Flattens <see cref="SystemPromptInputs"/> into the final system prompt
/// (PLAN §46): base prompt, then context sections, then tool guidelines, then
/// the append tail.
/// </summary>
public sealed class SystemPromptProvider : ISystemPromptProvider
{
    public async ValueTask<string> BuildAsync(SystemPromptInputs inputs, CancellationToken cancellationToken = default)
    {
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(inputs.BasePrompt))
            sb.AppendLine(inputs.BasePrompt.Trim());

        foreach (var section in inputs.ContextFiles)
            if (!string.IsNullOrWhiteSpace(section.Content))
            {
                sb.AppendLine();
                sb.AppendLine($"## {section.Title}");
                sb.AppendLine(section.Content.Trim());
            }

        if (inputs.ToolGuidelines is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Tool Guidelines");
            foreach (var g in inputs.ToolGuidelines)
                sb.AppendLine("- " + g);
        }

        if (inputs.Tools is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Available Tools");
            foreach (var t in inputs.Tools)
                sb.AppendLine($"- {t.Name}: {t.Description}");
        }

        if (!string.IsNullOrWhiteSpace(inputs.Environment.Content))
        {
            sb.AppendLine();
            sb.AppendLine("## Environment");
            sb.AppendLine(inputs.Environment.Content);
        }

        if (!string.IsNullOrWhiteSpace(inputs.AppendPrompt))
        {
            sb.AppendLine();
            sb.AppendLine(inputs.AppendPrompt.Trim());
        }

        await ValueTask.CompletedTask;
        return sb.ToString();
    }
}

/// <summary>Wraps <see cref="ContextDiscovery"/> as the cross-ALC contract.</summary>
public sealed class WorkspaceContextBuilder : IWorkspaceContextBuilder
{
    public ValueTask<SystemPromptInputs> BuildAsync(string workspace, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ContextDiscovery.BuildForWorkspace(workspace));
}
