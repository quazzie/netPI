using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace NetPI.E2E;

// ------------------------------------------------------------------ mini test framework (NuGet is not available)

public class AssertException(string message) : Exception(message);

public static class Check
{
    private static int _total;
    // the test body that is running in this async flow: with shards several bodies run at once, and each owns its count
    private static readonly AsyncLocal<int[]?> Scope = new();

    public static int Total => Volatile.Read(ref _total);

    /// <summary>Count this flow's checks into <paramref name="counter"/> (the runner reads it after the body).</summary>
    public static void Attach(int[] counter) => Scope.Value = counter;

    private static void Count()
    {
        Interlocked.Increment(ref _total);
        if (Scope.Value is { } c) Interlocked.Increment(ref c[0]);
    }

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

/// <summary>One registered test. <see cref="Id"/> is stable (runs, reports and reruns name it); <see cref="Name"/> is the readable sentence.</summary>
public sealed record TestCase(string Id, string Name, Func<Task> Body, int TimeoutSeconds, int Order)
{
    /// <summary>The tags of the case: its area (the id up to the first dot) plus the ones <see cref="Catalog"/> lists for it.</summary>
    public IReadOnlyList<string> Tags => Catalog.TagsOf(Id);
}

/// <summary>The registry the <c>XTests.Register</c> methods fill. A runner is bound to one <see cref="Env"/> (or to none, to list).</summary>
public sealed class TestRunner
{
    private readonly List<TestCase> _tests = [];

    public void Add(string id, string name, Func<Task> body, int timeoutSeconds = 90)
    {
        if (_tests.Any(t => t.Id == id)) throw new InvalidOperationException($"duplicate test id '{id}'");
        _tests.Add(new TestCase(id, name, body, timeoutSeconds, _tests.Count));
    }

    public IReadOnlyList<TestCase> Cases => _tests;
}

/// <summary>
/// Console output of the test body that is running in this async flow goes to that test's buffer, so that shards running at
/// once do not interleave their lines: the runner prints a test's lines together with its result.
/// </summary>
public sealed class RoutedConsole(TextWriter inner) : TextWriter
{
    private static readonly AsyncLocal<StringBuilder?> Buffer = new();
    private readonly Lock _gate = new();

    public override Encoding Encoding => inner.Encoding;

    public static void Attach(StringBuilder? buffer) => Buffer.Value = buffer;

    public static void Install()
    {
        if (Console.Out is not RoutedConsole) Console.SetOut(new RoutedConsole(Console.Out));
    }

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (value is null) return;
        if (Buffer.Value is { } b) { lock (b) b.Append(value); return; }
        lock (_gate) { inner.Write(value); inner.Flush(); }
    }

    public override void WriteLine(string? value) => Write((value ?? "") + "\n");

    public override void WriteLine() => Write("\n");
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
