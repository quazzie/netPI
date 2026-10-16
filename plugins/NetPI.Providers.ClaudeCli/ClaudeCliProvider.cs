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
    /// <summary>The names of NetPI's registered tools (null: every counterpart counts as registered).</summary>
    private readonly Func<IReadOnlyCollection<string>>? _knownTools;
    /// <summary>
    /// Where the system prompt files go, and the folder a side call (title, summary) runs in. The plugin puts it in NetPI's
    /// home, which is the owner's alone: in a shared temp folder another local user could swap a prompt file between its
    /// write and the CLI's read (idea-irqqkj). Without a home (tests) it is the temp folder.
    /// </summary>
    private readonly string _workDir;

    public ClaudeCliProvider(Func<JsonObject?> config, ILogger? logger = null, Func<string?, string?>? cwdOf = null,
        Func<IReadOnlyCollection<string>>? hiddenEnv = null, ClaudeUsageTracker? usage = null)
        : this(config, logger, ProcessRunner.Run, cwdOf, hiddenEnv, usage) { }

    internal ClaudeCliProvider(Func<JsonObject?> config, ILogger? logger, Func<string?, string?>? cwdOf,
        Func<IReadOnlyCollection<string>>? hiddenEnv, ClaudeUsageTracker? usage, ICliSessions sessions, string? workDir = null,
        Func<IReadOnlyCollection<string>>? knownTools = null)
        : this(config, logger, ProcessRunner.Run, cwdOf, hiddenEnv, usage, sessions, workDir, knownTools) { }

    internal ClaudeCliProvider(Func<JsonObject?> config, ILogger? logger, CliRunner runner, Func<string?, string?>? cwdOf = null,
        Func<IReadOnlyCollection<string>>? hiddenEnv = null, ClaudeUsageTracker? usage = null, ICliSessions? sessions = null, string? workDir = null,
        Func<IReadOnlyCollection<string>>? knownTools = null)
    {
        _knownTools = knownTools;
        _sessions = sessions ?? new MemoryCliSessions();
        _config = config;
        _log = logger ?? NullLogger.Instance;
        _runner = runner;
        _cwdOf = cwdOf ?? (_ => null);
        _hiddenEnv = hiddenEnv ?? (() => []);
        _usage = usage;
        _workDir = workDir ?? Path.Combine(Path.GetTempPath(), "netpi-claude-cli");
        SweepStalePromptFiles();
    }

    /// <summary>Where the prompt files are written and side calls run.</summary>
    internal string WorkDir => _workDir;
    internal ICliSessions Sessions => _sessions;

    /// <summary>netpi.mjs beside the app (shipped with it), for the prompt note on reaching NetPI; null when it is not there.</summary>
    internal string? RpcScript { get; init; }
    /// <summary>NetPI's home, which netpi.mjs finds the server through (its server.json).</summary>
    internal string? Home { get; init; }

    /// <summary>The ids of every Claude CLI provider: an answer one of them gave is this CLI's own (it holds that history).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> KnownIds = new(StringComparer.Ordinal) { ["claude-cli"] = 0 };
    private string _id = "claude-cli";
    public string Id { get => _id; init { _id = value; KnownIds[value] = 0; } }
    public string DisplayName { get; init; } = "Claude CLI";
    public bool IsLocal => false;

    internal ClaudeCliOptions Options()
    {
        try { return ClaudeCliOptions.Parse(_config()); }
        catch (Exception ex) { _log.LogWarning(ex, "claude-cli: could not read settings"); return ClaudeCliOptions.Parse(null); }
    }

    private string? _listed;
    // what the CLI said about each model's limits (its result line), over the table below
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Window, int MaxOutput)> _limits = new();

    /// <summary>Fires when a call taught the provider another limit than the one it listed (the plugin tells the catalog).</summary>
    public Action? OnModelsLearned { get; set; }

    /// <summary>What is known of a model before the CLI has said: the Claude 5 models have a 1M window, the earlier ones 200k.</summary>
    internal static (int Window, int MaxOutput) DefaultLimits(string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id, @"^claude-[a-z]+-5(-|$)") ? (1_000_000, 128_000) : (200_000, 64_000);

    private (int Window, int MaxOutput) LimitsOf(string id) => _limits.TryGetValue(id, out var l) ? l : DefaultLimits(id);

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
            DisplayName = DisplayName + ": " + id,
            ContextWindow = LimitsOf(id).Window,
            MaxOutputTokens = LimitsOf(id).MaxOutput,
            ManagesOwnContext = true,
            RunsOwnTools = true,
            Concurrency = o.MaxConcurrent,
            InputModalities = ["text", "image"],
            Reasoning = new ReasoningInfo { Supported = true, Efforts = [.. ClaudeCliOptions.Efforts], Default = "medium" },
            StallTimeoutSeconds = o.StallTimeoutSeconds,
            Status = "available",
            IsLocal = false,
        }).ToList();
        return Task.FromResult(list);
    }

    /// <summary>One try of a call: resume the CLI session of this chat, or start one (the whole conversation as the prompt).</summary>
    internal sealed record CliAttempt(bool Resume, string CliId, string Prompt, bool Persist, IReadOnlyList<ImagePart>? Images = null)
    {
        /// <summary>What goes on stdin: the prompt as text, or with pictures one stream-json user message (text and image blocks).</summary>
        public string Stdin => Images is not { Count: > 0 } ? Prompt : new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonNode?[] { new JsonObject { ["type"] = "text", ["text"] = Prompt.Length == 0 ? "(see the attached image)" : Prompt } }
                    .Concat(Images.Select(i => (JsonNode?)new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = i.MediaType, ["data"] = i.Data },
                    })).ToArray()),
            },
        }.ToJsonString();
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var o = Options();
        if (!o.Enabled) throw new ModelException("Claude CLI: provider is disabled in settings (its `enabled` setting).", false, null, "provider_disabled");
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
            attempt = new CliAttempt(false, Guid.NewGuid().ToString(), RenderPrompt(request), true, UserImages(request, 0));
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
        if (!agentTurn || string.IsNullOrEmpty(request.SessionId)) return new CliAttempt(false, "", RenderPrompt(request), false, UserImages(request, 0));
        var maxId = MaxId(request);
        if (_sessions.Get(request.SessionId) is { LastSeenId: > 0 } stored && stored.LastSeenId <= maxId)
            return new CliAttempt(true, stored.CliId, RenderDelta(request, stored.LastSeenId), true, UserImages(request, stored.LastSeenId));
        return new CliAttempt(false, Guid.NewGuid().ToString(), RenderPrompt(request), true, UserImages(request, 0));
    }

    private static long MaxId(ModelRequest request) => request.Messages.Count == 0 ? 0 : request.Messages.Max(m => m.Id);

    private async IAsyncEnumerable<ModelStreamEvent> RunAsync(ModelRequest request, ClaudeCliOptions o, bool agentTurn, CliAttempt attempt, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        // an agent's own turn gets the CLI's tools and its working folder; a title, a compaction summary or any other side call
        // through this model is text in, text out (a full CLI session for a title would work in the project with no permission prompts)
        var (tools, disallowed, policy) = agentTurn ? ToolPolicy(request, o, _knownTools?.Invoke()) : NoTools;
        var cwd = agentTurn ? _cwdOf(request.SessionId) : null;
        var workDir = cwd is { Length: > 0 } && Directory.Exists(cwd) ? cwd : _workDir;
        if (agentTurn && request.Tools.Count > 0 && !attempt.Resume)
            yield return new StreamNotice(
                $"Claude CLI does not use NetPI's tools (the {request.Tools.Count} of this request): it works with its own" +
                (tools.Length == 0 ? " (none enabled)." : $" in {workDir}; its tool calls show in the chat."), "info");
        if (agentTurn && policy is not null) yield return new StreamNotice($"Claude CLI works {policy} in this turn.", "info");

        // every call is a full Claude Code process: the ones beyond the limit wait (memory, and the subscription's rate limits)
        var (lease, waited) = await _gate.EnterAsync(() => Options().MaxConcurrent, ct).ConfigureAwait(false);
        using var gateLease = lease;
        if (waited) yield return new StreamNotice($"Claude CLI had to wait for a free slot ({o.MaxConcurrent} run at once: the provider's maxConcurrent).", "info");

        // The system prompt goes through a file: NetPI's can outgrow the 32k command line of Windows.
        var executable = ResolveExecutable(o.Executable); // before the file: nothing between writing it and the try that deletes it may throw
        Directory.CreateDirectory(_workDir);
        var promptFile = Path.Combine(_workDir, $"system-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(promptFile,
            SystemPromptWithNote(request.SystemPrompt, tools.Length > 0, HasShell(tools, disallowed) ? RpcCommand : null), ct).ConfigureAwait(false);
        var asm = new MessageAssembler();
        var stream = new ClaudeCliStream(asm) { ModelId = request.Model.Id };
        if (_usage is not null && o.Usage) stream.OnRateLimit = _usage.Observe;
        var recorded = false;
        try
        {
            var call = new CliCall(executable, BuildArgs(request, o, tools, promptFile, attempt, disallowed), attempt.Stdin, workDir,
                TimeSpan.FromSeconds(o.TimeoutSeconds), _hiddenEnv(), o.ConfigDir);
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
        if (stream.Limits is { } learned && LimitsOf(request.Model.Id) != learned)
        {
            _limits[request.Model.Id] = learned;
            try { OnModelsLearned?.Invoke(); } catch { /* a catalog refresh is never worth a failed call */ }
        }
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

    /// <summary>
    /// NetPI's system prompt describes its tools; the model is told which tools it really has, or it writes the others' calls as
    /// text. With a shell it is also told how it reaches NetPI itself (<paramref name="rpc"/>: the netpi.mjs command line), for
    /// what only NetPI holds, so a session in another project does not rebuild an MCP client or read a server's URL to call it.
    /// </summary>
    internal static string SystemPromptWithNote(string? system, bool cliTools, string? rpc = null)
    {
        var note = cliTools ? CliToolsNote : NoToolsNote;
        if (cliTools && rpc is not null) note += "\n\n" + RpcNote(rpc);
        return string.IsNullOrWhiteSpace(system) ? note : system.TrimEnd() + "\n\n" + note;
    }

    /// <summary>How a CLI with a shell reaches NetPI: its RPC command line, and the methods for what only NetPI holds.</summary>
    internal static string RpcNote(string command) =>
        "# NetPI from your shell\n" +
        $"NetPI itself answers on its RPC: `{command} <method> --params-file <file.json> --compact` (key=value pairs work instead of the file; " +
        $"add --write for a method that changes something; `{command} methods` lists them). Use it for what only NetPI holds: the ideas " +
        "backlog (ideas.list, ideas.get, ideas.add, ideas.update), memory.search, and the MCP servers NetPI is connected to (mcp.tools; " +
        "mcp.read for a read-only tool, mcp.call --write for any other). Call those servers through NetPI, never with their URL or a client " +
        "of your own.";

    /// <summary>The netpi.mjs command line beside the app (<see cref="RpcScript"/>), with NetPI's home: null without the script.</summary>
    internal string? RpcCommand => RpcScript is { } script
        ? $"node \"{script.Replace('\\', '/')}\"" + (Home is { } home ? $" --home \"{home.Replace('\\', '/')}\"" : "")
        : null;

    /// <summary>Whether the CLI keeps its Bash in this run: the RPC note is only of use with a shell.</summary>
    internal static bool HasShell(string tools, IReadOnlyList<string> disallowed) =>
        tools.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? !disallowed.Contains("Bash", StringComparer.OrdinalIgnoreCase)
            : tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("Bash", StringComparer.OrdinalIgnoreCase);

    /// <summary>A side call's tools: none.</summary>
    private static readonly (string Tools, IReadOnlyList<string> Disallowed, string? Note) NoTools = ("", [], null);

    /// <summary>
    /// The CLI's own tools and the NetPI tools each one stands in for: a run keeps a CLI tool only while NetPI offers it a
    /// counterpart (or has none registered, so it has no say), the same rule its own tools follow (idea-hghbmg).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string[]> Counterparts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Bash"] = ["bash", "pwsh"], ["BashOutput"] = ["bash", "pwsh"], ["KillShell"] = ["bash", "pwsh"], ["KillBash"] = ["bash", "pwsh"],
        ["Edit"] = ["edit"], ["MultiEdit"] = ["edit"], ["Write"] = ["write"], ["NotebookEdit"] = ["edit", "write"],
        ["Read"] = ["read"], ["Grep"] = ["grep"], ["Glob"] = ["find", "ls"],
        ["WebFetch"] = ["web_fetch"], ["WebSearch"] = ["web_search"],
        ["Task"] = ["agent_spawn"], ["TodoWrite"] = ["todo_write"],
    };

    /// <summary>The CLI tools a run that may only read keeps (plan mode, a research subagent of a chat in it).</summary>
    internal static readonly string[] ReadingTools = ["Read", "Grep", "Glob", "WebFetch", "WebSearch", "TodoWrite"];

    /// <summary>
    /// The CLI tools of an agent turn (idea-hghbmg): what <c>--tools</c> gets, what <c>--disallowedTools</c> takes away, and a line saying
    /// what was held back (null when nothing was). The configured set (<c>default</c>: every tool of the CLI) loses each tool whose
    /// NetPI counterpart this run is not offered although NetPI has it — a chat with bash switched off, a profile without edit, a
    /// subagent handed read-only tools; and a run that may only read (<see cref="ModelRequest.ReadOnlyTools"/>: plan mode) keeps the
    /// reading ones alone, whatever else it is offered. <paramref name="known"/>: NetPI's registered tools (null: all counterparts).
    /// </summary>
    internal static (string Tools, IReadOnlyList<string> Disallowed, string? Note) ToolPolicy(ModelRequest request, ClaudeCliOptions o, IReadOnlyCollection<string>? known)
    {
        if (o.Tools.Equals("none", StringComparison.OrdinalIgnoreCase)) return NoTools;
        var offered = request.Tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = known is null ? null : new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        bool Kept(string cli) => !Counterparts.TryGetValue(cli, out var netpi)
            || netpi.Any(offered.Contains)
            || (registered is not null && !netpi.Any(registered.Contains));
        var configured = o.Tools.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? null
            : o.Tools.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (request.ReadOnlyTools)
        {
            var reading = (configured ?? [.. ReadingTools]).Where(t => ReadingTools.Contains(t, StringComparer.OrdinalIgnoreCase) && Kept(t)).ToList();
            return (string.Join(",", reading), [], "read-only (plan mode): " + (reading.Count == 0 ? "no tools" : string.Join(", ", reading)));
        }
        if (configured is not null)
        {
            var kept = configured.Where(Kept).ToList();
            var dropped = configured.Except(kept, StringComparer.OrdinalIgnoreCase).ToList();
            return (string.Join(",", kept), [], dropped.Count == 0 ? null : $"without {string.Join(", ", dropped)} (this run is not given their NetPI counterparts)");
        }
        var off = Counterparts.Keys.Where(t => !Kept(t)).ToList();
        var shown = off.Where(t => t is not ("BashOutput" or "KillShell" or "KillBash" or "MultiEdit")).ToList();
        return ("default", off, off.Count == 0 ? null : $"without {string.Join(", ", shown)} (this run is not given their NetPI counterparts)");
    }

    /// <summary>The CLI arguments: no saved session, no skills; its tools and permissions as the settings and the run say; the system prompt is read from a file.</summary>
    internal static List<string> BuildArgs(ModelRequest request, ClaudeCliOptions o, string tools, string systemPromptFile, CliAttempt attempt,
        IReadOnlyList<string>? disallowed = null)
    {
        var args = new List<string> { "-p", "--model", request.Model.Id, "--tools", tools };
        if (tools.Length > 0) args.AddRange(["--permission-mode", o.PermissionMode]);
        if (tools.Length > 0 && disallowed is { Count: > 0 }) args.AddRange(["--disallowedTools", string.Join(",", disallowed)]);
        if (!o.Connectors) args.Add("--strict-mcp-config");
        // a chat's CLI session is kept (it is what the next turn resumes); anything else leaves no session behind
        if (!attempt.Persist) args.Add("--no-session-persistence");
        else args.AddRange(attempt.Resume ? ["--resume", attempt.CliId] : ["--session-id", attempt.CliId]);
        args.AddRange(["--disable-slash-commands", "--setting-sources", o.SettingSources,
            "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--system-prompt-file", systemPromptFile]);
        if (attempt.Images is { Count: > 0 }) args.AddRange(["--input-format", "stream-json"]); // pictures need the structured input
        if (request.ReasoningEffort is { } e && ClaudeCliOptions.Efforts.Contains(e, StringComparer.OrdinalIgnoreCase))
            args.AddRange(["--effort", e.ToLowerInvariant()]);
        return args;
    }

    /// <summary>Most pictures one call carries (the newest): a long chat does not resend every screenshot it ever had.</summary>
    internal const int MaxImages = 8;

    /// <summary>The pictures the user attached in the messages a call renders (after <paramref name="lastSeenId"/>), the newest <see cref="MaxImages"/>.</summary>
    internal static List<ImagePart>? UserImages(ModelRequest request, long lastSeenId)
    {
        var all = request.Messages.Where(m => (lastSeenId == 0 || m.Id > lastSeenId) && !m.Compacted && m.Role == MessageRole.User)
            .SelectMany(m => m.Parts.OfType<ImagePart>()).Where(i => i.Data.Length > 0).ToList();
        return all.Count == 0 ? null : all.Skip(Math.Max(0, all.Count - MaxImages)).ToList();
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
                case MessageRole.Assistant when (m.Provider is null || !KnownIds.ContainsKey(m.Provider)):
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
