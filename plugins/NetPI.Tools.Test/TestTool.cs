using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Test;

/// <summary>
/// <c>test { suite?, only?, changed? }</c>: runs the project's tests as <c>.netpi/tests.json</c> says and reads the
/// results file the command leaves, so a chat gets the counts and the failures instead of a console log.
/// </summary>
internal sealed class TestTool : IAgentTool
{
    private const int MaxFailuresShown = 20;
    private const int MaxMessageChars = 600;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "test",
        Label = "Tests",
        Category = "code",
        ReadOnly = false,
        SummaryArg = "suite",
        Description = "Run the project's tests (configured in .netpi/tests.json): a suite, only some tests, or the tests the changed files can affect; " +
                      "answers with the counts and the failures.",
        Help = "test { suite?: string, only?: string, changed?: boolean, cwd?: string }. cwd: the checkout to test (a worktree of yours; default the chat's folder). A relative cwd resolves against the chat's folder; when that does not exist, a name that is the chat's own folder resolves to it (a chat in its own worktree), and a worktree name resolves against the project folder — the nearest ancestor of the chat's folder that holds a .worktrees directory. The project's .netpi/tests.json: " +
               "{ \"suites\": { \"<name>\": \"<command>\" }, \"changed\": \"<command>\", \"results\": \"<path>\", \"timeoutSeconds\"?: 1800 }. " +
               "changed: true runs the changed command; suite picks a suite (the only one when there is just one). only is a name filter: it is in the NETPI_TEST_ONLY environment variable of the command, " +
               "and replaces {only} in it (quoted) when the command has one; it may not contain quotes, backslashes, $, % or backticks. The command writes the results file " +
               "{ passed, failed, skipped, failures: [{ name, message, evidence? }], log? }; the answer is the counts and each failure with its message " +
               "and the path of its evidence. Without .netpi/tests.json the answer says how to add one.",
        Parameters = JsonNode.Parse("""
            {"type":"object","properties":{
              "suite":{"type":"string"},"only":{"type":"string"},"changed":{"type":"boolean"},
              "cwd":{"type":"string","description":"the checkout to test, e.g. your worktree (default: the chat's folder)"}}}
            """)!.AsObject(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext tool, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        // A worktree of the agent's own is a checkout of its own: its tests are the ones its changes need (the bash and pwsh
        // tools take a cwd for the same reason). A chat already in the named worktree passes its path again (AGENTS.md's
        // hint) — the doubled path does not exist, so the name resolves to the folder the chat is in.
        var (root, cwdProblem) = ResolveCwd(tool.Cwd, a.Str("cwd"));
        if (root is null) return ToolResult.Error(cwdProblem!);
        var (config, error) = TestConfig.Load(root);
        if (config is null) return ToolResult.Error(error!);

        var only = a.Str("only")?.Trim();
        if (only is { Length: 0 }) only = null;
        if (only is not null && only.IndexOfAny(['"', '`', '$', '%', '\\', '\r', '\n', '\0']) >= 0)
            return ToolResult.Error("only may not contain quotes, backslashes, $, % or backticks (it goes into the command line). Use a plain substring of the test name.");

        string command, label;
        if (a.Bool("changed") == true)
        {
            if (config.Changed is null) return ToolResult.Error($"{TestConfig.RelativePath} has no \"changed\" command. {TestConfig.HowToAdd}");
            (command, label) = (config.Changed, "changed");
        }
        else
        {
            var name = a.Str("suite")?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                if (config.Suites.Count == 1) name = config.Suites.Keys.First();
                else return ToolResult.Error($"Which suite? {Names(config)}.");
            }
            if (!config.Suites.TryGetValue(name, out var found)) return ToolResult.Error($"No suite \"{name}\". {Names(config)}.");
            (command, label) = (found, config.Suites.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)));
        }
        command = command.Replace("{only}", only is null ? "" : "\"" + only + "\"", StringComparison.Ordinal);

        var results = Path.GetFullPath(Path.Combine(root, config.Results!));
        try { Directory.CreateDirectory(Path.GetDirectoryName(results)!); if (File.Exists(results)) File.Delete(results); } // so a file that is there afterwards is this run's
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ToolResult.Error($"The old results file {results} could not be removed: {ex.Message}"); }

        var env = new Dictionary<string, string> { ["NETPI_TEST_ONLY"] = only ?? "", ["NETPI_TEST_RESULTS"] = results };
        var outcome = await TestRun.RunAsync(command, root, env, TimeSpan.FromSeconds(config.TimeoutSeconds), tool.Output, ct).ConfigureAwait(false);
        if (outcome.Cancelled) throw new OperationCanceledException(ct);
        if (outcome.StartError is not null) return ToolResult.Error($"The test command could not be started: {outcome.StartError}");
        var seconds = outcome.Elapsed.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        if (outcome.TimedOut)
            return ToolResult.Error($"{label}: the command ran out of time after {config.TimeoutSeconds} s and was stopped (timeoutSeconds in {TestConfig.RelativePath}).{TailOf(outcome)}");

        var (read, problem) = TestRun.ReadResults(results);
        if (read is null)
            return ToolResult.Error($"{label}: the command exited with {outcome.Exit} in {seconds} s, and {problem}. The command has to write it (see {TestConfig.RelativePath}).{TailOf(outcome)}",
                new { suite = label, exitCode = outcome.Exit, results });

        var text = new StringBuilder();
        text.Append($"{label}: {read.Passed} passed, {read.Failed} failed, {read.Skipped} skipped in {seconds} s");
        if (outcome.Exit != 0 && read.Failed == 0) text.Append($" (the command exited with {outcome.Exit})");
        text.Append('.');
        foreach (var f in read.Failures.Take(MaxFailuresShown))
        {
            var message = string.Join(' ', f.Message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (message.Length > MaxMessageChars) message = message[..MaxMessageChars] + "…";
            text.Append("\n- ").Append(f.Name);
            if (message.Length > 0) text.Append(": ").Append(message);
            if (!string.IsNullOrEmpty(f.Evidence)) text.Append("\n  evidence: ").Append(f.Evidence);
        }
        if (read.Failures.Count > MaxFailuresShown) text.Append($"\n… {read.Failures.Count - MaxFailuresShown} more failures in {results}.");
        if (read.Failed > read.Failures.Count && read.Failures.Count == 0) text.Append(" The results file lists no failures; see the log.");
        if (!string.IsNullOrEmpty(read.Log)) text.Append("\nlog: ").Append(read.Log);
        return ToolResult.Ok(text.ToString(), new
        {
            suite = label, exitCode = outcome.Exit, seconds = outcome.Elapsed.TotalSeconds, passed = read.Passed, failed = read.Failed, skipped = read.Skipped,
            failures = read.Failures.Select(f => new { name = f.Name, message = f.Message, evidence = f.Evidence }).ToArray(), log = read.Log, results,
        });
    }

    private static (string? root, string? error) ResolveCwd(string chatCwd, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return (Path.GetFullPath(chatCwd), null);
        if (Path.IsPathRooted(cwd)) // absolute: unchanged
        {
            var absolute = Path.GetFullPath(cwd);
            return Directory.Exists(absolute) ? (absolute, null) : (null, $"cwd {absolute} does not exist.");
        }

        var combined = Path.GetFullPath(Path.Combine(chatCwd, cwd));
        var tried = new List<string> { combined };
        if (Directory.Exists(combined)) return (combined, null);

        var chat = Path.GetFullPath(chatCwd);
        if (IsSuffixOf(chat, cwd) && Add(tried, chat) && Directory.Exists(chat)) return (chat, null); // the chat is already in the named folder

        var projectRoot = ProjectRootOf(chat); // the nearest ancestor that holds .worktrees: a sibling worktree by its name
        if (projectRoot is not null)
        {
            var fromRoot = Path.GetFullPath(Path.Combine(projectRoot, cwd));
            if (Add(tried, fromRoot) && Directory.Exists(fromRoot)) return (fromRoot, null);
        }
        return (null, $"cwd {cwd} does not exist. Tried: {string.Join("; ", tried)}.");
    }

    private static bool Add(List<string> tried, string candidate)
    {
        if (tried.Any(t => string.Equals(t, candidate, PathComparison))) return false;
        tried.Add(candidate);
        return true;
    }

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Does <paramref name="chat"/> end with <paramref name="cwd"/> as path segments (normalized, the platform's case rules)?</summary>
    private static bool IsSuffixOf(string chat, string cwd)
    {
        var chatSegments = chat.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(s => s.Length > 0).ToArray();
        var segments = new List<string>();
        foreach (var part in cwd.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
            segments.Add(part);
        }
        if (segments.Count == 0 || segments.Count > chatSegments.Length) return false;
        for (var i = 0; i < segments.Count; i++)
            if (!string.Equals(chatSegments[chatSegments.Length - segments.Count + i], segments[i], PathComparison)) return false;
        return true;
    }

    /// <summary>The nearest ancestor of <paramref name="dir"/> (itself included) that contains a <c>.worktrees</c> folder —
    /// for a worktree chat, the project folder. Null when none: the name has no project root to try.</summary>
    private static string? ProjectRootOf(string dir)
    {
        var current = new DirectoryInfo(dir);
        while (current is not null)
        {
            try
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".worktrees"))) return current.FullName;
            }
            catch { /* a folder we cannot read is not the project root */ }
            current = current.Parent;
        }
        return null;
    }

    private static string Names(TestConfig c) =>
        $"Suites: {(c.Suites.Count == 0 ? "(none)" : string.Join(", ", c.Suites.Keys))}{(c.Changed is not null ? "; or changed: true" : "")}";

    private static string TailOf(CommandOutcome o)
    {
        var lines = o.Tail.TrimEnd().Split('\n');
        var last = string.Join('\n', lines.TakeLast(15)).TrimEnd();
        return last.Length == 0 ? "" : "\nLast output:\n" + last;
    }
}
