using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace NetPI.Agent.Tests;

// ------------------------------------------------------------------ mini test framework

public sealed class AssertException(string message) : Exception(message);

/// <summary>A test that could not run here (no git, no browser, nothing built): a skip ends the test,
/// is counted apart from the passes and prints a SKIP line scripts/test.ps1 counts. Not a pass, and
/// not a failure either: it says what was missing (idea-r4e8rf).</summary>
public sealed class SkipException(string reason) : Exception(reason);

public static class Check
{
    /// <summary>End the test as skipped, with the reason it could not run.</summary>
    [DoesNotReturn]
    public static void Skip(string reason) => throw new SkipException(reason);

    public static void True(bool condition, string message = "expected true", [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition) throw new AssertException($"{message} ({expr})");
    }

    public static void False(bool condition, string message = "expected false", [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (condition) throw new AssertException($"{message} ({expr})");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{message ?? "not equal"}\n      expected: {Show(expected)}\n      actual:   {Show(actual)}");
    }

    public static void Contains(string? haystack, string needle, string? message = null)
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{message ?? "substring not found"}: {Show(needle)}\n      in: {Show(haystack)}");
    }

    public static void NotContains(string? haystack, string needle, string? message = null)
    {
        if (haystack is not null && haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{message ?? "unexpected substring"}: {Show(needle)}\n      in: {Show(haystack)}");
    }

    /// <summary>Runs <paramref name="body"/> and returns the <typeparamref name="T"/> it throws.</summary>
    public static async Task<T> ThrowsAsync<T>(Func<Task> body, string? message = null) where T : Exception
    {
        try { await body(); }
        catch (T ex) { return ex; }
        throw new AssertException($"{message ?? "no exception"}: expected {typeof(T).Name}");
    }

    public static string Show(object? o)
    {
        var s = o?.ToString() ?? "null";
        s = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length > 800 ? s[..800] + "…" : s;
    }
}

public sealed class TestRunner
{
    /// <summary>Exit code when the filter selected no test at all: a broken selection, not a pass (idea-jw74xi). The
    /// documented direct run is <c>dotnet &lt;suite&gt;.dll [filter]</c>, and it must not report green for a typo or a
    /// renamed test. scripts/test.ps1 maps a non-zero exit with no FAIL line to a failure.</summary>
    public const int NoTestSelected = 2;

    private readonly List<(string Name, Func<Task> Body)> _tests = [];

    public void Add(string name, Func<Task> body) => _tests.Add((name, body));
    public void Add(string name, Action body) => _tests.Add((name, () => { body(); return Task.CompletedTask; }));

    public async Task<int> RunAsync(string[] filters)
    {
        var selected = _tests.Where(t => filters.Length == 0 || filters.Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        int passed = 0, failed = 0, skipped = 0;
        var failures = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var (name, body) in selected)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var task = Task.Run(body);
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60))) != task)
                    throw new AssertException("test timed out after 60s");
                await task;
                passed++;
                Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds}ms)");
            }
            catch (SkipException ex)
            {
                skipped++;
                Console.WriteLine($"  SKIP  {name} ({ex.Message})");
            }
            catch (Exception ex)
            {
                failed++;
                var msg = ex is AssertException ? ex.Message : ex.ToString();
                failures.Add($"{name}: {msg}");
                Console.WriteLine($"  FAIL  {name} ({sw.ElapsedMilliseconds}ms)\n        {msg.Replace("\n", "\n        ")}");
            }
        }
        Console.WriteLine();
        var nothing = selected.Count == 0;
        if (nothing) Console.WriteLine("No test matches the filter.");
        Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped, {selected.Count} total in {total.Elapsed.TotalSeconds:0.0}s");
        if (failed > 0)
        {
            Console.WriteLine("Failures:");
            foreach (var f in failures) Console.WriteLine("  - " + f.Split('\n')[0]);
        }
        return failed > 0 ? 1 : nothing ? NoTestSelected : 0;
    }
}

public static class Wait
{
    /// <summary>Poll until <paramref name="condition"/> holds (throws on timeout).</summary>
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new AssertException($"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }
}
