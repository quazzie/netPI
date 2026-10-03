using System.Diagnostics;

namespace NetPI.Host.Tests;

/// <summary>
/// The runners of all five suites exit 0 when the filter selects no test at all, so the direct run AGENTS.md
/// documents for the fix loop — <c>dotnet tests/&lt;X&gt;.Tests/bin/&lt;Config&gt;/NetPI.&lt;X&gt;.Tests.dll [filter]</c> —
/// reports green for a typo or a renamed test. Exit 2 says "nothing was selected" without pretending to be a crash
/// (1 is a failed test), and scripts/test.ps1 already treats a non-zero exit with no FAIL line as a failure.
/// </summary>
public static class RunnerTests
{
    private const string NoSuchTest = "zz-no-such-test-zz";

    public static void Register(TestRunner r)
    {
        r.Add("tests: a filter that selects nothing exits 2, and one that selects a test still exits 0", NothingSelectedIsNotAPass);
        r.Add("tests: the suite dll itself exits 2 for a filter that matches nothing", SuiteDllExitsTwo);
        r.Add("tests: every suite runner carries the exit-2 rule, not just this copy", EveryRunnerHasTheRule);
        r.Add("tests: a skip is counted apart from the passes, says why, and does not fail the run", SkipIsNotAPass);
        r.Add("tests: every suite runner carries the skip rule, and none prints a bare skip line", EveryRunnerHasTheSkipRule);
        r.Add("tests: every suite loads the built plugins from artifacts/dev/app, never the installed app", EverySuiteLoadsTheDevTree);
        r.Add("tests: test.ps1 counts the skips, fails only when the filter matched nowhere, and names the suites", TestScriptHonesty);
    }

    /// <summary>The runner sources, and every test source, for the checks that are about all of them. The five
    /// suites share one runner (tests/Shared/Harness.cs); the Providers suite runs its check-style adapter over it.</summary>
    private static readonly string[] RunnerFiles =
    {
        "Shared/Harness.cs",
        "NetPI.Providers.Tests/TestRunner.cs",
    };

    private static async Task NothingSelectedIsNotAPass()
    {
        // A nested run prints its own summary; swallow it, or it lands in this suite's stdout and test.ps1 reads its
        // "No test matches the filter." line as a no-match of the suite itself.
        var real = Console.Out;
        Console.SetOut(new StringWriter());
        int emptyCode, runCode;
        try
        {
            var empty = new TestRunner();
            empty.Add("runner: inner", () => { });
            emptyCode = await empty.RunAsync([NoSuchTest]);

            var runs = new TestRunner();
            runs.Add("runner: inner", () => { });
            runCode = await runs.RunAsync(["runner:"]);
        }
        finally { Console.SetOut(real); }

        Check.Equal(TestRunner.NoTestSelected, emptyCode, "nothing selected is exit 2");
        Check.Equal(0, runCode, "a filter that selects a test still exits 0");
    }

    /// <summary>The documented direct run, on this suite's own dll: no test matches, so the process must fail.</summary>
    private static async Task SuiteDllExitsTwo()
    {
        var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        Check.True(dll is not null && File.Exists(dll), $"the suite dll is next to the runner: {dll}");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        var psi = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(dll!);
        psi.ArgumentList.Add(NoSuchTest);
        using var p = Process.Start(psi) ?? throw new AssertException("the suite dll did not start");
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Check.Equal(TestRunner.NoTestSelected, p.ExitCode, $"exit code of a run that selected nothing:\n{stdout}");
        Check.Contains(stdout, "No test matches the filter.");
    }

    /// <summary>Five copies of the same runner, each free to drift: the rule has to be in all of them.</summary>
    private static Task EveryRunnerHasTheRule()
    {
        foreach (var runner in RunnerFiles)
        {
            var path = Path.Combine(T.RepoRoot, "tests", runner);
            Check.True(File.Exists(path), $"{runner} exists");
            var source = File.ReadAllText(path);
            Check.Contains(source, "NoTestSelected", $"{runner} names the exit code for a selection that matched nothing");
            Check.Contains(source, "nothing ? NoTestSelected", $"{runner} returns it when nothing was selected");
            Check.NotContains(source, "return failed == 0 ? 0 : 1;", $"{runner} no longer returns success unconditionally");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// A test that could not run here (no git, no browser, nothing built) used to print a line and return, which the
    /// runner counted as a pass: an environment problem read as a green check. A skip is its own outcome now.
    /// </summary>
    private static async Task SkipIsNotAPass()
    {
        var real = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int code;
        try
        {
            var runs = new TestRunner();
            runs.Add("runner: passes", () => { });
            runs.Add("runner: skipped", () => Check.Skip("no git on PATH"));
            code = await runs.RunAsync(["runner:"]);
        }
        finally { Console.SetOut(real); }

        var output = captured.ToString();
        Check.Equal(0, code, "a skip is not a failure");
        Check.Contains(output, "SKIP  runner: skipped (no git on PATH)", "the line names the test and the reason");
        Check.Contains(output, "1 passed, 0 failed, 1 skipped, 2 total", "and the summary counts it apart from the passes");
    }

    /// <summary>The same rule in every runner, and no site left printing a skip by hand (it would pass again).</summary>
    private static Task EveryRunnerHasTheSkipRule()
    {
        foreach (var runner in RunnerFiles)
        {
            var source = File.ReadAllText(Path.Combine(T.RepoRoot, "tests", runner));
            Check.Contains(source, "SkipException", $"{runner} has a skip outcome of its own");
            Check.Contains(source, "catch (SkipException", $"{runner} catches it instead of counting the test as passed");
            Check.Contains(source, "SKIP  ", $"{runner} prints a line scripts/test.ps1 can read");
            Check.Contains(source, "skipped", $"{runner} counts the skips in its summary");
        }
        foreach (var file in Directory.EnumerateFiles(Path.Combine(T.RepoRoot, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            foreach (var line in File.ReadAllLines(file).Where(l => l.Contains("Console.WriteLine", StringComparison.Ordinal)))
            {
                // The runners' own summary line counts the skips; a test that prints one itself passes silently.
                Check.False(line.Contains("skipped", StringComparison.OrdinalIgnoreCase) && !line.Contains("passed", StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(file)} prints a skip by hand, which counts as a pass: {line.Trim()}");
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// AGENTS.md documents running a suite dll directly, and three of them fell back to artifacts/app: the installed
    /// build, not this checkout's. In a worktree there is no artifacts/app at all, so the fallback tested nothing.
    /// </summary>
    private static Task EverySuiteLoadsTheDevTree()
    {
        foreach (var file in new[] { "NetPI.Tools.Tests/LoadTests.cs", "NetPI.Agent.Tests/UnloadTests.cs", "NetPI.Providers.Tests/Program.cs" })
        {
            var source = File.ReadAllText(Path.Combine(T.RepoRoot, "tests", file));
            var fallback = source.Split('\n').FirstOrDefault(l => l.Contains("NETPI_APP_DIR", StringComparison.Ordinal) && l.Contains("custom ? custom", StringComparison.Ordinal));
            Check.True(fallback is not null, $"{file} still lets NETPI_APP_DIR name the app folder");
            Check.Contains(fallback, "dev", $"{file} falls back to artifacts/dev/app, the build output: {fallback?.Trim()}");
        }
        return Task.CompletedTask;
    }

    /// <summary>What the fix loop reads: a skip is printed, a filter that matched nowhere fails, and the re-run names
    /// the suites that failed. Checked in the script, as BuildTests does for build.ps1.</summary>
    private static void TestScriptHonesty()
    {
        var ps = File.ReadAllText(Path.Combine(T.RepoRoot, "scripts", "test.ps1"));
        Check.Contains(ps, "'(\\d+) skipped'", "the skip count is read out of the runner's summary");
        Check.Contains(ps, "Skipped (something was missing", "and printed, so a suite that skips half of itself is visible");
        Check.Contains(ps, "no test matched the filter in any suite", "a filter that matches nothing in one suite is not a failure");
        Check.Contains(ps, "$emptySuites.Count -eq $results.Count", "it fails only when no suite matched anything");
        Check.Contains(ps, "$suiteArgs -Only $quoted", "the re-run command names the suites that failed");
        Check.NotContains(ps, "-v q | Out-Null", "the build's own output is kept: a failed build has to say why");
    }
}