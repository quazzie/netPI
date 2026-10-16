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
        Help = "test { suite?: string, only?: string, changed?: boolean }. The project's .netpi/tests.json: " +
               "{ \"suites\": { \"<name>\": \"<command>\" }, \"changed\": \"<command>\", \"results\": \"<path>\", \"timeoutSeconds\"?: 1800 }. " +
               "changed: true runs the changed command; suite picks a suite (the only one when there is just one). only is a name filter: it is in the NETPI_TEST_ONLY environment variable of the command, " +
               "and replaces {only} in it (quoted) when the command has one; it may not contain quotes, backslashes, $, % or backticks. The command writes the results file " +
               "{ passed, failed, skipped, failures: [{ name, message, evidence? }], log? }; the answer is the counts and each failure with its message " +
               "and the path of its evidence. Without .netpi/tests.json the answer says how to add one.",
        Parameters = JsonNode.Parse("""
            {"type":"object","properties":{
              "suite":{"type":"string"},"only":{"type":"string"},"changed":{"type":"boolean"}}}
            """)!.AsObject(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext tool, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var root = Path.GetFullPath(tool.Cwd);
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

    private static string Names(TestConfig c) =>
        $"Suites: {(c.Suites.Count == 0 ? "(none)" : string.Join(", ", c.Suites.Keys))}{(c.Changed is not null ? "; or changed: true" : "")}";

    private static string TailOf(CommandOutcome o)
    {
        var lines = o.Tail.TrimEnd().Split('\n');
        var last = string.Join('\n', lines.TakeLast(15)).TrimEnd();
        return last.Length == 0 ? "" : "\nLast output:\n" + last;
    }
}
