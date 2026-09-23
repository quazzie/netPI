using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NetPI.E2E;

// ------------------------------------------------------------------ mini test framework (NuGet is not available)

public sealed class AssertException(string message) : Exception(message);

public static class Check
{
    private static int _total;

    public static int Total => Volatile.Read(ref _total);

    private static void Count() => Interlocked.Increment(ref _total);

    public static void True(bool condition, string message = "expected true", [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        Count();
        if (!condition) throw new AssertException($"{message} ({expr})");
    }

    public static void False(bool condition, string message = "expected false", [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        Count();
        if (condition) throw new AssertException($"{message} ({expr})");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        Count();
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{message ?? "not equal"}\n      expected: {Show(expected)}\n      actual:   {Show(actual)}");
    }

    public static void Contains(string? haystack, string needle, string? message = null)
    {
        Count();
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{message ?? "substring not found"}: {Show(needle)}\n      in: {Show(haystack)}");
    }

    public static void NotContains(string? haystack, string needle, string? message = null)
    {
        Count();
        if (haystack is not null && haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{message ?? "unexpected substring"}: {Show(needle)}\n      in: {Show(haystack)}");
    }

    public static string Show(object? o)
    {
        var s = o is JsonElement je ? je.GetRawText() : o?.ToString() ?? "null";
        s = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length > 1200 ? s[..1200] + "…" : s;
    }
}

public sealed class TestRunner
{
    private readonly List<(string Name, Func<Task> Body, int TimeoutSeconds)> _tests = [];

    public void Add(string name, Func<Task> body, int timeoutSeconds = 90) => _tests.Add((name, body, timeoutSeconds));

    public IReadOnlyList<string> Names => _tests.Select(t => t.Name).ToList();

    public async Task<int> RunAsync(IReadOnlyList<string> filters)
    {
        var selected = _tests.Where(t => filters.Count == 0 || filters.Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        int passed = 0, failed = 0;
        var failures = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var (name, body, timeout) in selected)
        {
            var sw = Stopwatch.StartNew();
            var checksBefore = Check.Total;
            try
            {
                var task = Task.Run(body);
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeout))) != task)
                    throw new AssertException($"test timed out after {timeout}s");
                await task;
                passed++;
                Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds}ms, {Check.Total - checksBefore} checks)");
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
        Console.WriteLine($"{passed} passed, {failed} failed, {selected.Count} total, {Check.Total} checks in {total.Elapsed.TotalSeconds:0.0}s");
        if (failed > 0)
        {
            Console.WriteLine("Failures:");
            foreach (var f in failures) Console.WriteLine("  - " + f.Split('\n')[0]);
        }
        return failed == 0 ? 0 : 1;
    }
}

public static class Wait
{
    /// <summary>Poll until <paramref name="condition"/> holds (throws on timeout).</summary>
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 15_000, int pollMs = 25)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new AssertException($"timed out after {timeoutMs}ms waiting for: {what}");
            await Task.Delay(pollMs);
        }
    }

    public static async Task<T> UntilAsync<T>(Func<Task<T?>> probe, string what, int timeoutMs = 15_000, int pollMs = 50) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (await probe() is { } v) return v;
            if (sw.ElapsedMilliseconds > timeoutMs) throw new AssertException($"timed out after {timeoutMs}ms waiting for: {what}");
            await Task.Delay(pollMs);
        }
    }
}

/// <summary>JsonElement conveniences.</summary>
public static class J
{
    public static string? S(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    public static long L(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    public static bool B(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static JsonElement P(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static bool Has(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public static IEnumerable<JsonElement> Arr(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) => e.P(name).Arr();
}
