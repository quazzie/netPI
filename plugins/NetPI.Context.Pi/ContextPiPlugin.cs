using NetPI.Abstractions;

namespace NetPI.Context.Pi;

/// <summary>
/// The reloadable context plugin (PLAN §16, §17, §43, §46).
///
/// §16: AGENTS.md layering, broad → specific:
///   ~/.netpi/AGENTS.md, drive-root/.netpi/AGENTS.md, …up to the workspace
///   itself; a <c>.netpi/AGENTS.override.md</c> in a directory replaces that
///   directory's ordinary AGENTS.md; cached by path/mtime/length.
/// §17: SYSTEM.md REPLACES netPI's base prompt (project level overrides
///   global); APPEND_SYSTEM.md layers global → project; AGENTS context is
///   always kept regardless of the SYSTEM.md override.
/// </summary>
public sealed class ContextPlugin : INetPiPlugin
{
    private ISystemPromptProvider? _provider;
    private readonly ContextDiscovery _discovery = new();

    public PluginInfo Info { get; } = new("netPI.Context.Pi", "Context / System Prompt", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _provider = new SystemPromptProvider();
        context.Services.Register<ISystemPromptProvider>("system-prompt", _provider);
        context.Services.Register<IWorkspaceContextBuilder>("workspace-context",
            new WorkspaceContextBuilder(_discovery));
        context.Log.Information("Context plugin ready (AGENTS layering + SYSTEM/APPEND_SYSTEM)");
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
/// Discovers and caches workspace context files (PLAN §16/§17).
/// </summary>
public sealed class ContextDiscovery
{
    /// <summary>Path → (mtime, length, text) cache; re-read only on change.</summary>
    private sealed record Entry(DateTime mtime, long length, string text);

    /// <summary>netPI's built-in base prompt; REPLACED by SYSTEM.md (PLAN §17).</summary>
    private const string BuiltInBasePrompt = "You are netPI, a local AI agent. You work in a workspace directory using tools (read, write, edit, grep, bash, powershell, background tasks). Be direct, act with the tools when asked, and keep answers concise.";
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Read a file if it exists (cached by path/mtime/length), else null.</summary>
    public string? ReadCached(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = File.GetLastWriteTimeUtc(path);
            var len = new FileInfo(path).Length;
            if (_cache.TryGetValue(path, out var hit) && hit.mtime == info && hit.length == len)
                return hit.text;
            var text = File.ReadAllText(path);
            _cache[path] = new Entry(info, len, text);
            return text;
        }
        catch { return null; }
    }

    /// <summary>
    /// §16 discovery order, broad → specific: the user home layer, the drive
    /// root, then every directory from the drive root down to the workspace
    /// itself. Each layer is the directory's <c>.netpi/AGENTS.override.md</c>
    /// (replacing the ordinary file) or <c>.netpi/AGENTS.md</c>.
    /// </summary>
    public List<(string Label, string Content)> DiscoverAgentsLayers(string workspace)
    {
        var layers = new List<(string, string)>();
        var dirs = new List<string>();

        // 1. Global: ~/.netpi/AGENTS.md
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home is { Length: > 0 })
            dirs.Add(home);
        // 2..n. Drive root → … → workspace (broad → specific)
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(workspace));
            var chain = new List<DirectoryInfo>();
            while (dir is not null) { chain.Add(dir); dir = dir.Parent; }
            chain.Reverse(); // drive root first
            foreach (var d in chain) dirs.Add(d.FullName);
        }
        catch { }

        foreach (var raw in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var netpi = Path.Combine(raw, ".netpi");
            // A directory with .netpi/AGENTS.override.md uses THAT instead of
            // its ordinary AGENTS.md (PLAN §16).
            var text = ReadCached(Path.Combine(netpi, "AGENTS.override.md"))
                ?? ReadCached(Path.Combine(netpi, "AGENTS.md"));
            if (text is { Length: > 0 })
                layers.Add(($"{raw}\\.netpi\\AGENTS.md", text));
        }
        return layers;
    }

    /// <summary>
    /// §17: the effective SYSTEM.md (project level overrides global) replaces
    /// the base prompt; APPEND layers are returned global → project.
    /// </summary>
    public (string? systemOverride, List<(string Label, string Content)> appends)
        DiscoverSystemLayers(string workspace)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalSystem = home is { Length: > 0 }
            ? ReadCached(Path.Combine(home, ".netpi", "SYSTEM.md")) : null;
        var projectSystem = ReadCached(Path.Combine(workspace, ".netpi", "SYSTEM.md"));
        var systemOverride = projectSystem ?? globalSystem;

        var appends = new List<(string, string)>();
        var globalAppend = home is { Length: > 0 }
            ? ReadCached(Path.Combine(home, ".netpi", "APPEND_SYSTEM.md")) : null;
        if (globalAppend is { Length: > 0 }) appends.Add(("global APPEND_SYSTEM.md", globalAppend));
        var projectAppend = ReadCached(Path.Combine(workspace, ".netpi", "APPEND_SYSTEM.md"));
        if (projectAppend is { Length: > 0 }) appends.Add(("project APPEND_SYSTEM.md", projectAppend));
        return (systemOverride, appends);
    }

    /// <summary>Builds the structured prompt inputs for a workspace (fresh at each user turn).</summary>
    public SystemPromptInputs BuildForWorkspace(string workspace)
    {
        // §16: AGENTS context — broad → specific layers, ALWAYS included.
        var agentsLayers = DiscoverAgentsLayers(workspace);
        var contextFiles = agentsLayers.Select(l =>
            new SystemPromptSection(l.Label, l.Content)).ToList();

        // §17: SYSTEM.md replaces the base prompt; the broad→specific AGENTS
        // concatenation is netPI's default base prompt (no AGENTS files found
        // at all → the caller-level built-in default).
        var (systemOverride, appendLayers) = DiscoverSystemLayers(workspace);

        // APPEND layers global → project, then any legacy workspace append.
        var appendPrompt = appendLayers.Select(a => a.Content).ToList();
        var legacyAppend = ReadCached(Path.Combine(workspace, ".netpi", "append_system.md"));
        if (legacyAppend is { Length: > 0 }) appendPrompt.Add(legacyAppend);

        return new SystemPromptInputs
        {
            BasePrompt = systemOverride ?? BuiltInBasePrompt,
            ContextFiles = contextFiles,
            AppendPrompt = string.Join("\n\n", appendPrompt),
            Environment = new SystemPromptSection("environment",
                $"OS: {Environment.OSVersion}\nCWD: {Path.GetFullPath(workspace)}\nShell: bash/powershell (detected per invocation)\nDate: {DateTime.Now:yyyy-MM-dd} ({DateTime.Now:dddd})"),
        };
    }
}

/// <summary>
/// Flattens <see cref="SystemPromptInputs"/> into the final system prompt
/// (PLAN §46): base prompt (AGENTS layers or SYSTEM.md override), the AGENTS
/// context sections (kept even when SYSTEM.md replaces the base), tool
/// guidelines, available tools, environment, then the append tail.
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

        if (inputs.Environment is { Content.Length: > 0 })
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
    private readonly ContextDiscovery _discovery;
    public WorkspaceContextBuilder(ContextDiscovery discovery) => _discovery = discovery;

    /// <summary>Context refreshes at the beginning of a new user turn (PLAN §16), not per token.</summary>
    public ValueTask<SystemPromptInputs> BuildAsync(string workspace, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_discovery.BuildForWorkspace(workspace));
}
