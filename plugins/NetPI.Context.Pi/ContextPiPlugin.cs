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
        context.Services.Register<IInstructionContextResolver>("instruction-context",
            new InstructionContextResolver(_discovery));
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
public class ContextDiscovery
{
    /// <summary>Path → (mtime, length, text) cache; re-read only on change.</summary>
    private sealed record Entry(DateTime mtime, long length, string text);

    /// <summary>
    /// The raw file read — overridable so tests can simulate a file that
    /// EXISTS but cannot be read (an actionable failure, astra-1 C).
    /// </summary>
    protected virtual string ReadFileRaw(string path) => File.ReadAllText(path);

    /// <summary>netPI's built-in base prompt; REPLACED by SYSTEM.md (PLAN §17).</summary>
    private const string BuiltInBasePrompt = "You are netPI, a local AI agent. You work in a workspace directory using tools (read, write, edit, grep, bash, powershell, background tasks). Be direct, act with the tools when asked, and keep answers concise.";
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Read a file (cached by path/mtime/length) or return null when it is
    /// MISSING — a valid empty layer. A file that EXISTS but cannot be read
    /// (permissions, locked, decode) is an actionable failure: it throws
    /// <see cref="ProjectContextChangeException"/> (astra-1 C) rather than
    /// being silently skipped. A delete-between-check race surfaces as
    /// <see cref="FileNotFoundException"/> and is treated as missing.
    /// </summary>
    public string? ReadCached(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = File.GetLastWriteTimeUtc(path);
            var len = new FileInfo(path).Length;
            if (_cache.TryGetValue(path, out var hit) && hit.mtime == info && hit.length == len)
                return hit.text;
            var text = ReadFileRaw(path);
            _cache[path] = new Entry(info, len, text);
            return text;
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception ex)
        {
            throw new ProjectContextChangeException(
                $"Instruction file {path} exists but could not be read ({ex.Message}); " +
                "fix the file's permissions or contents.");
        }
    }

    /// <summary>
    /// astra-1 C precedence, ONE file per directory, then directories layered
    /// broad → specific:
    ///   1. <c>.netpi/AGENTS.override.md</c>
    ///   2. <c>AGENTS.override.md</c>
    ///   3. <c>AGENTS.md</c>
    ///   4. <c>.netpi/AGENTS.md</c>
    /// The user home (<c>~/.netpi</c>) is the GLOBAL layer, separated from the
    /// project layers so it is never added twice. Missing files are valid
    /// (empty layer); an existing-but-unreadable file is an actionable
    /// <see cref="ProjectContextChangeException"/>.
    /// </summary>
    public List<(string Label, string Content)> DiscoverAgentsLayers(string workspace)
    {
        var global = DiscoverGlobalLayer();
        var project = DiscoverProjectLayers(workspace);
        var layers = new List<(string, string)>(project.Count + (global is null ? 0 : 1));
        if (global is not null) layers.Add(global.Value);
        layers.AddRange(project);
        return layers;
    }

    /// <summary>
    /// astra-1 C: split discovery — the home layer (global) plus the
    /// directory chain broad → specific. Used so global instructions can be
    /// separated from project-specific layers.
    /// </summary>
    public ((string Label, string Content)? Global, List<(string Label, string Content)> Project)
        DiscoverSplit(string workspace)
    {
        var global = DiscoverGlobalLayer();
        return (global, DiscoverProjectLayers(workspace));
    }

    /// <summary>
    /// astra-1 C: resolve the project's effective instruction context —
    /// the global home layer plus every project layer broad → specific —
    /// captured at selection (never per token/tool call). A missing file is
    /// a valid empty layer; an existing unreadable file throws
    /// <see cref="ProjectContextChangeException"/>.
    /// </summary>
    public ProjectContextSnapshot ResolveInstructions(string projectId, string projectName, string workspace)
    {
        var (global, project) = DiscoverSplit(workspace);
        var sources = new List<string>();
        var sb = new System.Text.StringBuilder();
        if (global is not null)
        {
            sb.Append(global.Value.Content.TrimEnd()).Append('\n');
            sources.Add(global.Value.Label);
        }
        foreach (var (path, content) in project)
        {
            sb.Append(content.TrimEnd()).Append('\n');
            sources.Add(path);
        }
        var text = sb.ToString().Trim('\n');
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[0..16];
        return new ProjectContextSnapshot(projectId, projectName, Path.GetFullPath(workspace),
            sources, text, hash, DateTimeOffset.UtcNow);
    }

    private (string Label, string Content)? DiscoverGlobalLayer()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home is not { Length: > 0 }) return null;
        var picked = PickLayerFile(home);
        if (picked is not null && picked.Value.Content is { Length: > 0 })
            return (Label: home, picked.Value.Content);
        return null;
    }

    private List<(string Label, string Content)> DiscoverProjectLayers(string workspace)
    {
        var layers = new List<(string, string)>();
        var dirs = new List<string>();

        try
        {
            // Drive root → … → workspace, broad → specific. The home layer is
            // NOT in this list: it is handled separately so it is never
            // doubled when the workspace sits under the user profile.
            var dir = new DirectoryInfo(Path.GetFullPath(workspace));
            var chain = new List<DirectoryInfo>();
            while (dir is not null) { chain.Add(dir); dir = dir.Parent; }
            chain.Reverse();
            foreach (var d in chain)
                dirs.Add(d.FullName);
        }
        catch { return layers; }

        foreach (var raw in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var picked = PickLayerFile(raw);
            if (picked is not null && picked.Value.Content is { Length: > 0 })
                layers.Add((picked.Value.Path, picked.Value.Content));
        }
        return layers;
    }

    /// <summary>
    /// astra-1 C: one file per directory, in precedence order (see
    /// <see cref="DiscoverAgentsLayers"/>).
    /// </summary>
    private (string Path, string Content)? PickLayerFile(string dir)
    {
        var netpi = Path.Combine(dir, ".netpi");
        foreach (var path in new[]
        {
            Path.Combine(netpi, "AGENTS.override.md"),
            Path.Combine(dir, "AGENTS.override.md"),
            Path.Combine(dir, "AGENTS.md"),
            Path.Combine(netpi, "AGENTS.md"),
        })
        {
            if (!File.Exists(path)) continue;  // missing files are valid
            var text = ReadCached(path);       // existing-but-unreadable -> throws
            if (text is not null)
                return (path, text);
        }
        return null;
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

        // Custom sections appended by plugins (order-preserving) — e.g. the
        // astra-2 §10 orchestrate-mode coordinator guidance contributed by the
        // agent runner. Empty by default, so the chat-mode prompt is unchanged.
        foreach (var section in inputs.CustomSections)
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

/// <summary>
/// Wraps <see cref="ContextDiscovery"/> as the cross-ALC
/// <see cref="IInstructionContextResolver"/> contract (astra-1 C). Resolves a
/// project's exact effective instruction context at SELECTION — the agent
/// surface calls it once per project change, never per token or tool call.
/// </summary>
public sealed class InstructionContextResolver : IInstructionContextResolver
{
    private readonly ContextDiscovery _discovery;
    public InstructionContextResolver(ContextDiscovery discovery) => _discovery = discovery;

    public ValueTask<ProjectContextSnapshot> ResolveAsync(
        string projectId, string projectName, string workspace, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_discovery.ResolveInstructions(projectId, projectName, workspace));
}
