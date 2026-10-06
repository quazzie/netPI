using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// Claude through the installed Claude Code CLI (<c>claude -p</c>), signed in with the owner's subscription. One call
/// is one process: the prompt goes in on stdin and the CLI works on its own, with its own tools, in the session's
/// workspace; its stream-json output is turned into the usual stream events. NetPI's tools are not offered to it.
/// </summary>
public sealed class ClaudeCliProvider : IModelProvider
{
    private readonly Func<JsonObject?> _config;
    private readonly ILogger _log;
    private readonly CliRunner _runner;
    private readonly Func<string?, string?> _cwdOf;
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "netpi-claude-cli");

    public ClaudeCliProvider(Func<JsonObject?> config, ILogger? logger = null, Func<string?, string?>? cwdOf = null)
        : this(config, logger, ProcessRunner.Run, cwdOf) { }

    internal ClaudeCliProvider(Func<JsonObject?> config, ILogger? logger, CliRunner runner, Func<string?, string?>? cwdOf = null)
    {
        _config = config;
        _log = logger ?? NullLogger.Instance;
        _runner = runner;
        _cwdOf = cwdOf ?? (_ => null);
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
            StallTimeoutSeconds = o.StallTimeoutSeconds,
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

        var tools = o.Tools.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : o.Tools;
        // The CLI works where the session works; without a session folder it has only an empty scratch folder.
        var cwd = _cwdOf(request.SessionId);
        var workDir = cwd is { Length: > 0 } && Directory.Exists(cwd) ? cwd : _workDir;
        if (request.Tools.Count > 0)
            yield return new StreamNotice(
                $"Claude CLI does not use NetPI's tools (the {request.Tools.Count} of this request): it works with its own" +
                (tools.Length == 0 ? " (none enabled)." : $" in {workDir}; its tool calls show in the chat."), "info");

        // The system prompt goes through a file: NetPI's can outgrow the 32k command line of Windows.
        Directory.CreateDirectory(_workDir);
        var promptFile = Path.Combine(_workDir, $"system-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(promptFile, SystemPromptWithNote(request.SystemPrompt, tools.Length > 0), ct).ConfigureAwait(false);
        var asm = new MessageAssembler();
        var stream = new ClaudeCliStream(asm);
        try
        {
            var call = new CliCall(o.Executable, BuildArgs(request, o, promptFile), RenderPrompt(request), workDir, TimeSpan.FromSeconds(o.TimeoutSeconds));
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
            throw new ModelException("Claude CLI ended without a result line (the process stopped early)." +
                (stream.RanTools ? " It had already run tools, so the call is not repeated automatically: look at what it changed before sending the task again." : ""),
                !stream.RanTools, null, "cli_incomplete");
        foreach (var ev in asm.Drain()) yield return ev;
        var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        yield return new StreamCompleted(asm.Build(Id, request.Model.Id, request.SessionId, ms));
    }

    internal const string NoToolsNote =
        "# This call has no tools\n" +
        "In this call you cannot run commands, read files or call any tool, whatever the instructions above say about tools. " +
        "Never write tool calls, <invoke> blocks or function-call markup: they are not executed and only clutter the answer. " +
        "Work from the conversation alone, and when you need something you cannot see, say exactly what is missing.";

    internal const string CliToolsNote =
        "# Tools in this call\n" +
        "The tools described above (NetPI's) are not available here and are never executed: do not write their calls or <invoke> markup. " +
        "Use your own tools (read, search, edit, shell) in the working folder to do the work, then answer in plain text.";

    /// <summary>NetPI's system prompt describes its tools; the model is told which tools it really has, or it writes the others' calls as text.</summary>
    internal static string SystemPromptWithNote(string? system, bool cliTools)
    {
        var note = cliTools ? CliToolsNote : NoToolsNote;
        return string.IsNullOrWhiteSpace(system) ? note : system.TrimEnd() + "\n\n" + note;
    }

    /// <summary>The CLI arguments: no saved session, no skills; its tools and permissions as the settings say; the system prompt is read from a file.</summary>
    internal static List<string> BuildArgs(ModelRequest request, ClaudeCliOptions o, string systemPromptFile)
    {
        var args = new List<string> { "-p", "--model", request.Model.Id };
        var tools = o.Tools.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : o.Tools;
        args.AddRange(["--tools", tools]);
        if (tools.Length > 0) args.AddRange(["--permission-mode", o.PermissionMode]);
        if (!o.Connectors) args.Add("--strict-mcp-config");
        args.AddRange(["--no-session-persistence", "--disable-slash-commands", "--setting-sources", o.SettingSources,
            "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--system-prompt-file", systemPromptFile]);
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
        // what the CLI's own tools returned stays out: the next call has its own tools to look again
        var ranByCli = request.Messages.SelectMany(m => m.ToolCalls).Where(c => c.ProviderExecuted).Select(c => c.Id).ToHashSet();
        foreach (var m in request.Messages)
        {
            if (m.Compacted) continue;
            var text = m.Role == MessageRole.Tool
                ? string.Join("\n", m.ToolResults.Where(r => !ranByCli.Contains(r.CallId)).Select(r => $"[{r.Name} result]\n{r.Content}"))
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
