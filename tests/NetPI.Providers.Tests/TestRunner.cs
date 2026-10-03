using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace NetPI.Providers.Tests;

// The check-style runner: a failing Check does not throw, so one test collects every failure it has, and the
// summary counts checks as well as tests. The shared harness (tests/Shared/Harness.cs) owns what this shares
// with the other suites — the skip outcome, the value rendering, the exit-code contract and the result file —;
// its call sites (t.Check / t.Eq / t.Skip, 350+ of them) are the runner's own API and stay put.
internal sealed class TestRunner(string[] args)
{
    /// <summary>The shared exit-code contract: a filter that selects no test at all is a broken selection, not a
    /// pass (idea-jw74xi). scripts/test.ps1 maps a non-zero exit with no failure to a crash.</summary>
    public const int NoTestSelected = NetPI.TestShared.TestRunner.NoTestSelected;

    // --shard i/n (the shared parse) splits the registration-order stream; the filters select by name.
    private readonly (string[] Filters, (int Index, int Count)? Shard) Parsed = NetPI.TestShared.TestRunner.ParseArgs(args);
    private long _shardSeen;
    private long _totalMs;

    private int _passedChecks, _failedChecks, _passedTests, _failedTests, _skippedTests, _unselectedTests;
    private readonly List<string> _failures = [];
    private readonly List<(string Name, string Result, long Ms)> _results = [];
    private string _current = "";

    public void Check(bool condition, string what, [CallerLineNumber] int line = 0)
    {
        if (condition) { _passedChecks++; return; }
        _failedChecks++;
        var msg = $"{_current}: {what} (line {line})";
        _failures.Add(msg);
        Console.WriteLine($"    FAIL {what} (line {line})");
    }

    public void Eq<T>(T expected, T actual, string what, [CallerLineNumber] int line = 0) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual),
            $"{what}: expected <{NetPI.TestShared.Check.Show(expected)}> got <{NetPI.TestShared.Check.Show(actual)}>", line);

    /// <summary>End the test as skipped, with the reason it could not run. The summary counts it apart from the
    /// passes, so a skipped test is never read as a green one (idea-r4e8rf).</summary>
    [DoesNotReturn]
    public void Skip(string reason) => throw new SkipException(reason);

    public async Task Run(string name, Func<Task> body)
    {
        var selected = Parsed.Filters.Length == 0 || Parsed.Filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
        if (!selected)
        {
            _unselectedTests++;
            return;
        }
        if (Parsed.Shard is { } s)
        {
            _shardSeen++;
            if (_shardSeen % s.Count != s.Index - 1) return;   // this run takes the i-th of n, in registration order
        }
        _current = name;
        var before = _failedChecks;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await body().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (SkipException ex)
        {
            _skippedTests++;
            _results.Add((name, "skipped", sw.ElapsedMilliseconds));
            Console.WriteLine($"SKIP  {name}  ({ex.Message})");
            return;
        }
        catch (Exception ex)
        {
            _failedChecks++;
            _failures.Add($"{name}: unexpected {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"    EXCEPTION {ex}");
        }
        sw.Stop();
        _totalMs += sw.ElapsedMilliseconds;
        var ok = _failedChecks == before;
        if (ok) { _passedTests++; _results.Add((name, "passed", sw.ElapsedMilliseconds)); }
        else { _failedTests++; _results.Add((name, "failed", sw.ElapsedMilliseconds)); }
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  ({sw.ElapsedMilliseconds} ms)");
    }

    public int Summary()
    {
        Console.WriteLine();
        var unselected = _unselectedTests > 0 ? $" ({_unselectedTests} not selected by the filter)" : "";
        Console.WriteLine($"Tests: {_passedTests} passed, {_failedTests} failed, {_skippedTests} skipped{unselected}. Checks: {_passedChecks} passed, {_failedChecks} failed.");
        var total = _passedTests + _failedTests + _skippedTests;
        var nothing = total == 0;
        if (nothing) Console.WriteLine("No test matches the filter.");
        foreach (var f in _failures) Console.WriteLine("  - " + f);
        WriteResult(total, nothing);
        return _failedTests > 0 ? 1 : nothing ? NoTestSelected : 0;
    }

    /// <summary>The same result file the other suites' runner writes, so scripts/test.ps1 reads every suite
    /// the same way (the check counts used to keep this suite's body time invisible to the process-gap check).</summary>
    private void WriteResult(int total, bool nothing)
    {
        var file = Environment.GetEnvironmentVariable("NETPI_TEST_RESULT");
        if (file is not { Length: > 0 }) return;
        File.WriteAllText(file, JsonSerializer.SerializeToElement(new
        {
            suite = "Providers",
            passed = _passedTests,
            failed = _failedTests,
            skipped = _skippedTests,
            total,
            bodySeconds = Math.Round(_totalMs / 1000.0, 2),
            matchedNothing = nothing,
            tests = _results.Select(r => new { name = r.Name, result = r.Result, ms = r.Ms }),
        }).GetRawText());
    }
}
