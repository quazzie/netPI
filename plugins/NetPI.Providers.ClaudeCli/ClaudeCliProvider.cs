using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// Claude through the installed Claude Code CLI (<c>claude -p</c>), signed in with the owner's subscription. One call
/// is one process: the prompt goes in on stdin, the CLI runs with no tools and no saved session, and its stream-json
/// output is turned into the usual stream events. The request's tools are not offered to the model.
/// </summary>
public sealed class ClaudeCliProvider : IModelProvider
{
    private readonly Func<JsonObject?> _config;
    private readonly ILogger _log;
    private readonly CliRunner _runner;
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "netpi-claude-cli");

    public ClaudeCliProvider(Func<JsonObject?> config, ILogger? logger = null)
        : this(config, logger, ProcessRunner.Run) { }

    internal ClaudeCliProvider(Func<JsonObject?> config, ILogger? logger, CliRunner runner)
    {
        _config = config;
        _log = logger ?? NullLogger.Instance;
        _runner = runner;
    }

    public string Id => "claude-cli";
    public string DisplayName => "Claude CLI";
    public bool IsLocal => false;

    internal ClaudeCliOptions Options()
    {
        try { return ClaudeCliOptions.Parse(_config()); }
        catch (Exception ex) { _log.LogWarning(ex, "claude-cli: could not read settings"); return ClaudeCliOptions.Parse(null); }
    }

    private string? _listed;

    private static string Fingerprint(ClaudeCliOptions o) => o.Enabled ? string.Join('|', o.Models) : "disabled";

    /// <summary>True when the settings now list other models than the last list did (so the catalog must be told).</summary>
    internal bool ModelsChangedSinceLastList() => _listed is not null && _listed != Fingerprint(Options());

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(bool refresh, CancellationToken ct)
    {
        var o = Options();
        _listed = Fingerprint(o);
        IReadOnlyList<ModelInfo> list = !o.Enabled ? [] : o.Models.Select(id => new ModelInfo
        {
            Provider = Id,
            Id = id,
            DisplayName = "Claude CLI: " + id,
            ContextWindow = 200_000,
            MaxOutputTokens = 32_000,
            InputModalities = ["text"],
            Reasoning = new ReasoningInfo { Supported = true, Efforts = [.. ClaudeCliOptions.Efforts], Default = "medium" },
            Status = "available",
            IsLocal = false,
        }).ToList();
        return Task.FromResult(list);
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var o = Options();
        if (!o.Enabled) throw new ModelException("Claude CLI: provider is disabled in settings (providers.claudeCli.enabled).", false, null, "provider_disabled");

        if (request.Tools.Count > 0)
            yield return new StreamNotice($"Claude CLI gives the model no tools: the {request.Tools.Count} tools of this request are not offered.", "warning");

        // The system prompt goes through a file: NetPI's can outgrow the 32k command line of Windows.
        Directory.CreateDirectory(_workDir);
        var promptFile = Path.Combine(_workDir, $"system-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(promptFile, request.SystemPrompt ?? "", ct).ConfigureAwait(false);
        var asm = new MessageAssembler();
        var stream = new ClaudeCliStream(asm);
        try
        {
            var call = new CliCall(o.Executable, BuildArgs(request, promptFile), RenderPrompt(request), _workDir, TimeSpan.FromSeconds(o.TimeoutSeconds));
            var warned = false;
            await foreach (var line in _runner(call, ct).ConfigureAwait(false))
            {
                stream.Handle(line);
                if (!warned && stream.ApiKeySource is { } src && src != "none")
                {
                    warned = true;
                    yield return new StreamNotice($"Claude CLI is signed in with an API key ({src}), not the subscription: this call is billed to the API.", "warning");
                }
                foreach (var ev in asm.Drain()) yield return ev;
                if (stream.Finished) break;
            }
        }
        finally { try { File.Delete(promptFile); } catch { /* the temp folder is cleaned by the OS */ } }
        if (!stream.Finished)
            throw new ModelException("Claude CLI ended without a result line (the process stopped early).", true, null, "cli_incomplete");
        foreach (var ev in asm.Drain()) yield return ev;
        var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        yield return new StreamCompleted(asm.Build(Id, request.Model.Id, request.SessionId, ms));
    }

    /// <summary>The CLI arguments: no tools, no saved session, no settings files; the system prompt is read from a file.</summary>
    internal static List<string> BuildArgs(ModelRequest request, string systemPromptFile)
    {
        var args = new List<string>
        {
            "-p", "--model", request.Model.Id, "--tools", "", "--strict-mcp-config", "--no-session-persistence", "--disable-slash-commands",
            "--setting-sources", "", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--system-prompt-file", systemPromptFile,
        };
        if (request.ReasoningEffort is { } e && ClaudeCliOptions.Efforts.Contains(e, StringComparer.OrdinalIgnoreCase))
            args.AddRange(["--effort", e.ToLowerInvariant()]);
        return args;
    }

    /// <summary>
    /// The conversation as the CLI's one prompt. A single user message goes in as it is; a longer history is written
    /// as a labelled transcript ending with what to answer, because a print-mode call has no session to carry it.
    /// </summary>
    internal static string RenderPrompt(ModelRequest request)
    {
        var turns = new List<(string Role, string Text)>();
        foreach (var m in request.Messages)
        {
            if (m.Compacted) continue;
            var text = m.Role == MessageRole.Tool
                ? string.Join("\n", m.ToolResults.Select(r => $"[{r.Name} result]\n{r.Content}"))
                : m.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            turns.Add((m.Role == MessageRole.Assistant ? "Assistant" : "User", text));
        }
        if (turns.Count == 0) return "";
        if (turns.Count == 1 && turns[0].Role == "User") return turns[0].Text;
        var sb = new StringBuilder("The conversation so far:\n\n");
        foreach (var (role, text) in turns) sb.Append(role).Append(":\n").Append(text).Append("\n\n");
        sb.Append("Reply to the last user message.");
        return sb.ToString();
    }
}
