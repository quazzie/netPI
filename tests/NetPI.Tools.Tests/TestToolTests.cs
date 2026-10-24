using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using NetPI.Tools.Test;

namespace NetPI.Tools.Tests;

/// <summary>The <c>test</c> tool: the project's .netpi/tests.json, a fake command (node) that writes a results file, and what the tool makes of it.</summary>
public static class TestToolTests
{
    // The fake test command: argv[2] says what it does; it reads the filter and the results path from the environment, as the real scripts do.
    private const string FakeCommand = """
        import fs from 'node:fs';
        const mode = process.argv[2];
        const only = process.env.NETPI_TEST_ONLY || '';
        const results = process.env.NETPI_TEST_RESULTS;
        const write = o => fs.writeFileSync(results, JSON.stringify(o));
        if (mode === 'pass') write({ passed: 12, failed: 0, skipped: 1, failures: [] });
        else if (mode === 'fail') write({ passed: 3, failed: 2, skipped: 0, log: 'logs/run.txt', failures: [
          { name: 'alpha: adds', message: 'expected 4\n   got 5', evidence: 'logs/alpha.txt' },
          { name: 'beta: removes', message: 'x'.repeat(2000) }] });
        else if (mode === 'only') write({ passed: 1, failed: 0, skipped: 0, failures: [], log: 'only=' + only + ';arg=' + (process.argv[3] || '') });
        else if (mode === 'silent') { console.log('building…'); console.error('boom: no results'); process.exit(3); }
        else if (mode === 'hang') setTimeout(() => {}, 60000);
        else if (mode === 'junk') fs.writeFileSync(results, 'not json');
        """;

    public static void Register(TestRunner t)
    {
        t.Add("test tool: without .netpi/tests.json it says how to add one; a broken config says what is wrong", NoConfig);
        t.Add("test tool: runs a suite and reports the counts, and each failure with its message and evidence", RunsSuites);
        t.Add("test tool: only reaches the command (environment and {only}); unsafe characters are refused; a missing suite is named", OnlyAndSuites);
        t.Add("test tool: a command that writes no results (or junk, or a stale file) is an error with its exit code and last output; a hang is stopped at the timeout", Failures);
        t.Add("test tool: the real scripts/test.ps1 writes the results file the tool reads: a pass, a failure with its assertion, a filter that matches nothing", RealScript);
    }

    private static bool NodeAvailable() => Available("node");

    private static bool Available(string exe)
    {
        try
        {
            using var node = Process.Start(new ProcessStartInfo(exe, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (node is null) return false;
            node.StandardOutput.ReadToEnd();
            return node.WaitForExit(15_000) && node.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }

    private static string Project(string config)
    {
        var dir = T.TempDir("test-tool");
        File.WriteAllText(Path.Combine(dir, "fake.mjs"), FakeCommand);
        Directory.CreateDirectory(Path.Combine(dir, ".netpi"));
        File.WriteAllText(Path.Combine(dir, ".netpi", "tests.json"), config);
        return dir;
    }

    private static Task<ToolResult> Run(string dir, object args) =>
        new TestTool().ExecuteAsync(T.Ctx(dir, (ModelInfo?)null), T.Args(args), CancellationToken.None);

    private const string Results = "out/results.json";

    private static string Config(string extra = "") => """
        { "suites": { "unit": "node fake.mjs pass", "bad": "node fake.mjs fail", "narrow": "node fake.mjs only {only}", "silent": "node fake.mjs silent",
                      "junk": "node fake.mjs junk", "hang": "node fake.mjs hang" },
          "changed": "node fake.mjs pass", "results": "out/results.json"
        """ + extra + "}";

    private static async Task NoConfig()
    {
        var dir = T.TempDir("test-tool-none");
        var none = await Run(dir, new { });
        Check.True(none.IsError, none.Content);
        Check.Contains(none.Content, ".netpi/tests.json");
        Check.Contains(none.Content, "\"suites\"");
        Check.Contains(none.Content, "\"results\"");

        var broken = Project("{ not json");
        Check.Contains((await Run(broken, new { })).Content, "not valid JSON");
        var noResults = Project("""{ "suites": { "a": "echo hi" } }""");
        Check.Contains((await Run(noResults, new { })).Content, "no results");
        foreach (var outside in new[] { "../r.json", "a/../../r.json", Path.GetTempPath().Replace('\\', '/') + "r.json" })
        {
            var escaping = Project("{ \"suites\": { \"a\": \"echo hi\" }, \"results\": \"" + outside + "\" }");
            var refused = await Run(escaping, new { });
            Check.True(refused.IsError && refused.Content.Contains("inside the project folder"), outside + ": " + refused.Content);
        }
        var noSuites = Project("""{ "results": "r.json" }""");
        Check.Contains((await Run(noSuites, new { })).Content, "no suites");
        await Task.CompletedTask;
    }

    private static async Task RunsSuites()
    {
        if (!NodeAvailable()) Check.Skip("node is not on PATH");
        var dir = Project(Config());
        var ok = await Run(dir, new { suite = "unit" });
        Check.False(ok.IsError, ok.Content);
        Check.Contains(ok.Content, "unit: 12 passed, 0 failed, 1 skipped");
        var d = T.D(ok);
        Check.Equal(12, d.Int("passed"));
        Check.Equal(0, d.Int("exitCode"));

        var bad = await Run(dir, new { suite = "BAD" });
        Check.False(bad.IsError, "the run worked; the tests failed: " + bad.Content);
        Check.Contains(bad.Content, "bad: 3 passed, 2 failed, 0 skipped");
        Check.Contains(bad.Content, "- alpha: adds: expected 4 got 5", "the message on one line");
        Check.Contains(bad.Content, "evidence: logs/alpha.txt");
        Check.Contains(bad.Content, "- beta: removes: xxx");
        Check.Contains(bad.Content, "…", "a long message is cut");
        Check.Contains(bad.Content, "log: logs/run.txt");
        Check.True(bad.Content.Length < 2500, "compact: " + bad.Content.Length);

        var changed = await Run(dir, new { changed = true });
        Check.Contains(changed.Content, "changed: 12 passed");
        // one suite and no name: that one
        var single = Project("""{ "suites": { "only-one": "node fake.mjs pass" }, "results": "r.json" }""");
        Check.Contains((await Run(single, new { })).Content, "only-one: 12 passed");

        // cwd: another checkout (an agent's own worktree) is tested by its own config, from a chat whose folder is elsewhere
        var elsewhere = T.TempDir("test-tool-chat");
        var viaCwd = await Run(elsewhere, new { suite = "unit", cwd = dir });
        Check.Contains(viaCwd.Content, "unit: 12 passed", "the worktree's suite ran");
        Check.True(File.Exists(Path.Combine(dir, "out", "results.json")), "its results are the worktree's");
        Check.Contains((await Run(elsewhere, new { cwd = "no-such-dir" })).Content, "does not exist");
    }

    private static async Task OnlyAndSuites()
    {
        if (!NodeAvailable()) Check.Skip("node is not on PATH");
        var dir = Project(Config());
        var narrow = await Run(dir, new { suite = "narrow", only = "lsp: references" });
        Check.False(narrow.IsError, narrow.Content);
        Check.Contains(narrow.Content, "only=lsp: references;arg=lsp: references", "in the environment and in {only}");
        var plain = await Run(dir, new { suite = "unit", only = "a name" });
        Check.False(plain.IsError, "a command without {only} still gets the environment variable: " + plain.Content);

        foreach (var evil in new[] { "x\" & calc", "$(whoami)", "a`b", "100%", "a\\b" })
        {
            var refused = await Run(dir, new { suite = "narrow", only = evil });
            Check.True(refused.IsError && refused.Content.Contains("may not contain"), $"{evil}: {refused.Content}");
        }
        var which = await Run(dir, new { });
        Check.True(which.IsError && which.Content.Contains("Which suite?") && which.Content.Contains("unit") && which.Content.Contains("changed: true"), which.Content);
        var missing = await Run(dir, new { suite = "nope" });
        Check.True(missing.IsError && missing.Content.Contains("No suite \"nope\""), missing.Content);
        var noChanged = await Run(Project("""{ "suites": { "a": "node fake.mjs pass" }, "results": "r.json" }"""), new { changed = true });
        Check.True(noChanged.IsError && noChanged.Content.Contains("no \"changed\""), noChanged.Content);
    }

    private static async Task Failures()
    {
        if (!NodeAvailable()) Check.Skip("node is not on PATH");
        var dir = Project(Config());
        Directory.CreateDirectory(Path.Combine(dir, "out"));
        File.WriteAllText(Path.Combine(dir, "out", "results.json"), """{ "passed": 99, "failed": 0, "skipped": 0, "failures": [] }""");
        var silent = await Run(dir, new { suite = "silent" });
        Check.True(silent.IsError, "a stale results file is not this run's: " + silent.Content);
        Check.Contains(silent.Content, "exited with 3");
        Check.Contains(silent.Content, "was not written");
        Check.Contains(silent.Content, "boom: no results", "the last output");
        Check.False(silent.Content.Contains("99 passed"));

        var junk = await Run(dir, new { suite = "junk" });
        Check.True(junk.IsError && junk.Content.Contains("not valid JSON"), junk.Content);

        var hang = Project(Config(", \"timeoutSeconds\": 2"));
        var sw = Stopwatch.StartNew();
        var stopped = await Run(hang, new { suite = "hang" });
        Check.True(stopped.IsError && stopped.Content.Contains("ran out of time after 2 s"), stopped.Content);
        Check.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"stopped at the deadline ({sw.Elapsed.TotalSeconds:0.0} s)");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        await Check.ThrowsAsync<OperationCanceledException>(() => new TestTool().ExecuteAsync(T.Ctx(dir, (ModelInfo?)null), T.Args(new { suite = "hang" }), cts.Token));
    }

    // The other tests pin the tool against a fake command; this one pins the writer side, the PowerShell layer the C# runners
    // cannot see (TAP counting, the matched-nothing rule, the file's shape): the repository's own script, run in a copy that
    // holds one web/test file, so it neither runs the real suites nor overwrites the checkout's results file.
    private static async Task RealScript()
    {
        if (!NodeAvailable() || !Available("pwsh")) Check.Skip("node or pwsh is not on PATH");
        var dir = Project("""{ "suites": { "ui": "pwsh -NoProfile -File scripts/test.ps1 -Suite UI" }, "results": "artifacts/testlogs/last-results.json" }""");
        Directory.CreateDirectory(Path.Combine(dir, "scripts"));
        foreach (var script in new[] { "test.ps1", "checkout.ps1" })
            File.Copy(Path.Combine(NetPI.TestShared.T.RepoRoot, "scripts", script), Path.Combine(dir, "scripts", script));
        Directory.CreateDirectory(Path.Combine(dir, "web", "test"));
        File.WriteAllText(Path.Combine(dir, "web", "test", "a.test.js"), """
            import { test } from 'node:test';
            import assert from 'node:assert/strict';
            test('alpha passes', () => {});
            test('beta fails', () => assert.equal(1, 2));
            """);

        var ok = await Run(dir, new { only = "alpha" });
        Check.False(ok.IsError, ok.Content);
        Check.Contains(ok.Content, "ui: 1 passed, 0 failed, 0 skipped");

        var bad = await Run(dir, new { only = "beta" });
        Check.False(bad.IsError, bad.Content);
        Check.Contains(bad.Content, "ui: 0 passed, 1 failed");
        Check.Contains(bad.Content, "- [UI] beta fails: Expected values to be strictly equal: 1 !== 2", "the failure with its assertion");
        Check.Contains(bad.Content, "evidence: artifacts");

        // a selection that runs nothing is a failure, not a green run with no tests
        var none = await Run(dir, new { only = "zzz-no-such-test" });
        Check.Contains(none.Content, "1 failed");
        Check.Contains(none.Content, "no test matched the filter");
    }
}
