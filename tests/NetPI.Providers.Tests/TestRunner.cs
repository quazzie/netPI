using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NetPI.Providers.Tests;

/// <summary>Tiny assertion/test harness (no test framework available offline). Filters select tests whose name contains
/// any of them (case-insensitive), like the other suites.</summary>
internal sealed class TestRunner(string[] filters)
{
    /// <summary>Exit code when the filter selected no test at all: a broken selection, not a pass (idea-jw74xi). The
    /// documented direct run is <c>dotnet &lt;suite&gt;.dll [filter]</c>, and it must not report green for a typo or a
    /// renamed test. scripts/test.ps1 maps a non-zero exit with no FAIL line to a failure.</summary>
    public const int NoTestSelected = 2;

    private int _passedChecks, _failedChecks, _passedTests, _failedTests, _skippedTests;
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
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected <{Show(expected)}> got <{Show(actual)}>", line);

    private static string Show<T>(T v) => v is null ? "null" : v.ToString()!.Replace("\n", "\\n");

    public async Task Run(string name, Func<Task> body)
    {
        if (filters.Length > 0 && !filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
        {
            _skippedTests++;
            return;
        }
        _current = name;
        var before = _failedChecks;
        var sw = Stopwatch.StartNew();
        try
        {
            await body().WaitAsync(TimeSpan.FromSeconds(30));
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
        var skipped = _skippedTests > 0 ? $" ({_skippedTests} skipped by the filter)" : "";
        Console.WriteLine($"Tests: {_passedTests} passed, {_failedTests} failed{skipped}. Checks: {_passedChecks} passed, {_failedChecks} failed.");
        var nothing = _passedTests + _failedTests == 0;
        if (nothing) Console.WriteLine("No test matches the filter.");
        foreach (var f in _failures) Console.WriteLine("  - " + f);
        return _failedTests > 0 ? 1 : nothing ? NoTestSelected : 0;
    }
}
