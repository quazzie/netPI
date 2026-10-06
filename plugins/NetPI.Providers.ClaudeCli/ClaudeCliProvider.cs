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
/// Only an agent's own turns get the CLI's tools: a title or a compaction summary written through this model is text only.
/// </summary>
public sealed class ClaudeCliProvider : IModelProvider
{
    private readonly Func<JsonObject?> _config;
    private readonly ILogger _log;
    private readonly CliRunner _runner;
    private readonly Func<string?, string?> _cwdOf;
    private readonly Func<IReadOnlyCollection<string>> _hiddenEnv;
    private readonly ClaudeUsageTracker? _usage;
    private readonly ICliSessions _sessions;
    private readonly CallGate _gate = new();
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "netpi-claude-cli");

    public ClaudeCliProvider(Func<JsonObject?> config, ILogger? logger = null, Func<string?, string?>? cwdOf = null,
        Func<IReadOnlyCollection<string>>? hiddenEnv = null, ClaudeUsageTracker? usage = null)
        : this(config, logger, ProcessRunner.Run, cwdOf, hiddenEnv, usage) { }

    internal ClaudeCliProvider(Func<JsonObject?> config, ILogger? logger, Func<string?, string?>? cwdOf,
        Func<IReadOnlyCollection<string>>? hiddenEnv, ClaudeUsageTracker? usage, ICliSessions sessions)
        : this(config, logger, ProcessRunner.Run, cwdOf, hiddenEnv, usage, sessions) { }

    internal ClaudeCliProvider(Func<JsonObject?> config, ILogger? logger, CliRunner runner, Func<string?, string?>? cwdOf = null,
        Func<IReadOnlyCollection<string>>? hiddenEnv = null, ClaudeUsageTracker? usage = null, ICliSessions? sessions = null)
    {
        _sessions = sessions ?? new MemoryCliSessions();
        _config = config;
        _log = logger ?? NullLogger.Instance;
        _runner = runner;
        _cwdOf = cwdOf ?? (_ => null);
        _hiddenEnv = hiddenEnv ?? (() => []);
        _usage = usage;
        SweepStalePromptFiles();
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

    private static string Fingerprint(ClaudeCliOptions o) => o.Enabled ? string.Join('|', o.Models) + "|" + o.StallTimeoutSeconds + "|" + o.MaxConcurrent : "disabled";

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
            Concurrency = o.MaxConcurrent,
            InputModalities = ["text"],
            Reasoning = new ReasoningInfo { Supported = true, Efforts = [.. ClaudeCliOptions.Efforts], Default = "medium" },
            StallTimeoutSeconds = o.StallTimeoutSeconds,
            Status = "available",
            IsLocal = false,
        }).ToList();
        return Task.FromResult(list);
    }

    /// <summary>One try of a call: resume the CLI session of this chat, or start one (the whole conversation as the prompt).</summary>
    internal sealed record CliAttempt(bool Resume, string CliId, string Prompt, bool Persist);

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var o = Options();
        if (!o.Enabled) throw new ModelException("Claude CLI: provider is disabled in settings (providers.claudeCli.enabled).", false, null, "provider_disabled");
        var agentTurn = request.Purpose == "agent";
        var attempt = Plan(request, agentTurn);
        while (true)
        {
            var retry = false;
            await using var run = RunAsync(request, o, agentTurn, attempt, ct).GetAsyncEnumerator(ct);
            while (true)
            {
                ModelStreamEvent current;
                try
                {
                    if (!await run.MoveNextAsync().ConfigureAwait(false)) yield break;
                    current = run.Current;
                }
                // the CLI no longer has the session (its files were cleaned, or the chat came from another machine): start over with the conversation
                catch (ModelException ex) when (attempt.Resume && ex.ErrorType == ClaudeCliStream.SessionNotFound)
                {
                    _log.LogWarning("claude-cli: session {Cli} of {Session} is gone; starting a new one", attempt.CliId, request.SessionId);
                    _sessions.Remove(request.SessionId!);
                    retry = true;
                    break;
                }
                yield return current;
            }
            if (!retry) yield break;
            attempt = new CliAttempt(false, Guid.NewGuid().ToString(), RenderPrompt(request), true);
            yield return new StreamNotice("The Claude CLI session of this chat is gone: continuing in a new one with the conversation so far.", "warning");
        }
    }

    /// <summary>
    /// A chat's later turns resume its CLI session and send only what is new; the first turn, a chat whose history was cut back
    /// (the stored point is newer than the newest message) and a call that belongs to no chat start a session with the whole
    /// conversation. A side call (title, compaction) never touches a session.
    /// </summary>
    internal CliAttempt Plan(ModelRequest request, bool agentTurn)
    {
        if (!agentTurn || string.IsNullOrEmpty(request.SessionId)) return new CliAttempt(false, "", RenderPrompt(request), false);
        var maxId = MaxId(request);
        if (_sessions.Get(request.SessionId) is { LastSeenId: > 0 } stored && stored.LastSeenId <= maxId)
            return new CliAttempt(true, stored.CliId, RenderDelta(request, stored.LastSeenId), true);
        return new CliAttempt(false, Guid.NewGuid().ToString(), RenderPrompt(request), true);
    }

    private static long MaxId(ModelRequest request) => request.Messages.Count == 0 ? 0 : request.Messages.Max(m => m.Id);

    private async IAsyncEnumerable<ModelStreamEvent> RunAsync(ModelRequest request, ClaudeCliOptions o, bool agentTurn, CliAttempt attempt, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        // an agent's own turn gets the CLI's tools and its working folder; a title, a compaction summary or any other side call
        // through this model is text in, text out (a full CLI session for a title would work in the project with no permission prompts)
        var tools = !agentTurn || o.Tools.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : o.Tools;
        var cwd = agentTurn ? _cwdOf(request.SessionId) : null;
        var workDir = cwd is { Length: > 0 } && Directory.Exists(cwd) ? cwd : _workDir;
        if (agentTurn && request.Tools.Count > 0 && !attempt.Resume)
            yield return new StreamNotice(
                $"Claude CLI does not use NetPI's tools (the {request.Tools.Count} of this request): it works with its own" +
                (tools.Length == 0 ? " (none enabled)." : $" in {workDir}; its tool calls show in the chat."), "info");

        // every call is a full Claude Code process: the ones beyond the limit wait (memory, and the subscription's rate limits)
        var (lease, waited) = await _gate.EnterAsync(() => Options().MaxConcurrent, ct).ConfigureAwait(false);
        using var gateLease = lease;
        if (waited) yield return new StreamNotice($"Claude CLI had to wait for a free slot ({o.MaxConcurrent} run at once: providers.claudeCli.maxConcurrent).", "info");

        // The system prompt goes through a file: NetPI's can outgrow the 32k command line of Windows.
        Directory.CreateDirectory(_workDir);
        var promptFile = Path.Combine(_workDir, $"system-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(promptFile, SystemPromptWithNote(request.SystemPrompt, tools.Length > 0), ct).ConfigureAwait(false);
        var asm = new MessageAssembler();
        var stream = new ClaudeCliStream(asm);
        if (_usage is not null && o.Usage) stream.OnRateLimit = _usage.Observe;
        var executable = ResolveExecutable(o.Executable);
        var recorded = false;
        try
        {
            var call = new CliCall(executable, BuildArgs(request, o, tools, promptFile, attempt), attempt.Prompt, workDir,
                TimeSpan.FromSeconds(o.TimeoutSeconds), _hiddenEnv());
            var warned = false;
            await foreach (var line in _runner(call, ct).ConfigureAwait(false))
            {
                stream.Handle(line);
                // the CLI took the session up: from here on it has the messages up to this one
                if (!recorded && attempt.Persist && stream.Started)
                {
                    recorded = true;
                    _sessions.Put(request.SessionId!, new CliSession(attempt.CliId, MaxId(request)));
                }
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
        {
            var message = "Claude CLI ended without a result line (the process stopped early)." +
                (stream.RanTools ? " It had already run tools, so the call is not repeated automatically: look at what it changed before sending the task again." : "") +
                (string.IsNullOrWhiteSpace(stream.Stderr) ? "" : $" It said: {stream.Stderr}");
            _log.LogWarning("claude-cli: {Message}", message);
            throw new ModelException(message, !stream.RanTools, null, "cli_incomplete");
        }
        foreach (var ev in asm.Drain()) yield return ev;
        var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _log.LogInformation("claude-cli: {Model} ({Purpose}{Resume}) in {WorkDir}: {Seconds:0.0}s, {Turns} turns, tools {Tools}", request.Model.Id, request.Purpose,
            attempt.Resume ? ", resumed" : "", workDir, ms / 1000.0, stream.Turns, tools.Length == 0 ? "none" : tools);
        yield return new StreamCompleted(asm.Build(Id, request.Model.Id, request.SessionId, ms));
    }

    /// <summary>
    /// The executable to start: a path as it is; a bare name looked up on PATH, then in the installer's own folder
    /// (<c>~/.local/bin</c>), which a NetPI that was started before the install does not have on its PATH.
    /// </summary>
    internal static string ResolveExecutable(string name)
    {
        if (name.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(name)) return name;
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* a malformed PATH entry */ }
        if (name.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude");
            if (File.Exists(local)) return local;
        }
        return name;
    }

    /// <summary>A crash leaves its system prompt file behind: the ones older than a day go.</summary>
    private void SweepStalePromptFiles()
    {
        try
        {
            if (!Directory.Exists(_workDir)) return;
            foreach (var f in Directory.EnumerateFiles(_workDir, "system-*.md"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(1)) File.Delete(f);
        }
        catch { /* best effort */ }
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
    internal static List<string> BuildArgs(ModelRequest request, ClaudeCliOptions o, string tools, string systemPromptFile, CliAttempt attempt)
    {
        var args = new List<string> { "-p", "--model", request.Model.Id, "--tools", tools };
        if (tools.Length > 0) args.AddRange(["--permission-mode", o.PermissionMode]);
        if (!o.Connectors) args.Add("--strict-mcp-config");
        // a chat's CLI session is kept (it is what the next turn resumes); anything else leaves no session behind
        if (!attempt.Persist) args.Add("--no-session-persistence");
        else args.AddRange(attempt.Resume ? ["--resume", attempt.CliId] : ["--session-id", attempt.CliId]);
        args.AddRange(["--disable-slash-commands", "--setting-sources", o.SettingSources,
            "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--system-prompt-file", systemPromptFile]);
        if (request.ReasoningEffort is { } e && ClaudeCliOptions.Efforts.Contains(e, StringComparer.OrdinalIgnoreCase))
            args.AddRange(["--effort", e.ToLowerInvariant()]);
        return args;
    }

    /// <summary>
    /// What is new to a resumed CLI session: the messages after the point it had (<paramref name="lastSeenId"/>), without its own
    /// answers and its own tools' results, which it already holds. An answer another model gave in between is passed on as such; a
    /// compaction summary is left out (the CLI has the real history). Nothing new at all (a retry): a nudge to carry on.
    /// </summary>
    internal static string RenderDelta(ModelRequest request, long lastSeenId)
    {
        var ranByCli = request.Messages.SelectMany(m => m.ToolCalls).Where(c => c.ProviderExecuted).Select(c => c.Id).ToHashSet();
        var blocks = new List<(string Label, string Text)>();
        foreach (var m in request.Messages)
        {
            if (m.Id <= lastSeenId || m.Compacted) continue;
            switch (m.Role)
            {
                case MessageRole.User or MessageRole.Notice:
                    if (!string.IsNullOrWhiteSpace(m.Text)) blocks.Add(("", m.Text));
                    break;
                case MessageRole.Assistant when m.Provider != "claude-cli":
                    if (!string.IsNullOrWhiteSpace(m.Text)) blocks.Add(("[An earlier answer, from another model]", m.Text));
                    break;
                case MessageRole.Tool:
                    foreach (var r in m.ToolResults.Where(r => !ranByCli.Contains(r.CallId)))
                        blocks.Add(($"[{r.Name} result]", r.Content));
                    break;
            }
        }
        if (blocks.Count == 0) return "Continue.";
        if (blocks.Count == 1 && blocks[0].Label.Length == 0) return blocks[0].Text;
        return string.Join("\n\n", blocks.Select(b => b.Label.Length == 0 ? b.Text : b.Label + "\n" + b.Text));
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

/// <summary>Limits how many CLI processes run at once; the calls beyond the limit wait in order. The limit is read at each entry, so a changed setting applies at once.</summary>
internal sealed class CallGate
{
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource> _waiters = new();
    private int _running;

    public int Running { get { lock (_lock) return _running; } }

    public async Task<(IDisposable Lease, bool Waited)> EnterAsync(Func<int> limit, CancellationToken ct)
    {
        TaskCompletionSource tcs;
        lock (_lock)
        {
            if (_running < limit()) { _running++; return (new Lease(this), false); }
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(tcs);
        }
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
            await tcs.Task.ConfigureAwait(false); // a releasing call hands its slot over: the count stays
        return (new Lease(this), true);
    }

    private void Release()
    {
        lock (_lock)
        {
            while (_waiters.Count > 0)
                if (_waiters.Dequeue().TrySetResult()) return;
            _running--;
        }
    }

    private sealed class Lease(CallGate gate) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }
}
