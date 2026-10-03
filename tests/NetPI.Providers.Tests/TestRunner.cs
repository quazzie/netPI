using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace NetPI.Providers.Tests;

// The check-style runner: a failing Check does not throw, so one test collects every failure it has, and the
// summary counts checks as well as tests. The shared harness (tests/Shared/Harness.cs) owns what this shares
// with the other suites — the skip outcome, the value rendering and the exit-code contract; its call sites
// (t.Check / t.Eq / t.Skip, 350+ of them) are the runner's own API and stay put.
internal sealed class TestRunner(string[] filters)
{
    /// <summary>The shared exit-code contract: a filter that selects no test at all is a broken selection, not a
    /// pass (idea-jw74xi). scripts/test.ps1 maps a non-zero exit with no failure to a crash.</summary>
    public const int NoTestSelected = NetPI.TestShared.TestRunner.NoTestSelected;

    private int _passedChecks, _failedChecks, _passedTests, _failedTests, _skippedTests, _unselectedTests;
    private readonly List<string> _failures = [];
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
        if (filters.Length > 0 && !filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
        {
            _unselectedTests++;
            return;
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
            Console.WriteLine($"SKIP  {name}  ({ex.Message})");
            return;
        }
        catch (Exception ex)
        {
            _failedChecks++;
            _failures.Add($"{name}: unexpected {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"    EXCEPTION {ex}");
        }
        var ok = _failedChecks == before;
        if (ok) _passedTests++; else _failedTests++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  ({sw.ElapsedMilliseconds} ms)");
    }

    public int Summary()
    {
        Console.WriteLine();
        var unselected = _unselectedTests > 0 ? $" ({_unselectedTests} not selected by the filter)" : "";
        Console.WriteLine($"Tests: {_passedTests} passed, {_failedTests} failed, {_skippedTests} skipped{unselected}. Checks: {_passedChecks} passed, {_failedChecks} failed.");
        var nothing = _passedTests + _failedTests + _skippedTests == 0;
        if (nothing) Console.WriteLine("No test matches the filter.");
        foreach (var f in _failures) Console.WriteLine("  - " + f);
        return _failedTests > 0 ? 1 : nothing ? NoTestSelected : 0;
    }
}
