using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NetPI.Providers.Tests;

/// <summary>Tiny assertion/test harness (no test framework available offline).</summary>
internal sealed class TestRunner
{
    private int _passedChecks, _failedChecks, _passedTests, _failedTests;
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
        Console.WriteLine($"Tests: {_passedTests} passed, {_failedTests} failed. Checks: {_passedChecks} passed, {_failedChecks} failed.");
        foreach (var f in _failures) Console.WriteLine("  - " + f);
        return _failedTests == 0 ? 0 : 1;
    }
}
