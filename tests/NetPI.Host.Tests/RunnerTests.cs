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
    }

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
        var runners = new[]
        {
            "NetPI.Providers.Tests/TestRunner.cs",
            "NetPI.Agent.Tests/Harness.cs",
            "NetPI.Aux.Tests/Harness.cs",
            "NetPI.Host.Tests/Harness.cs",
            "NetPI.Tools.Tests/Harness.cs",
        };
        foreach (var runner in runners)
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
}