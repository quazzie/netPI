using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NetPI.Host.Tests;

public sealed class AssertException(string message) : Exception(message);

public static class Check
{
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

    public static T Throws<T>(Action action, string? message = null) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"{message ?? "wrong exception"}: expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertException($"{message ?? "no exception"}: expected {typeof(T).Name}");
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action, string? message = null) where T : Exception
    {
        try { await action(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"{message ?? "wrong exception"}: expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertException($"{message ?? "no exception"}: expected {typeof(T).Name}");
    }

    public static string Show(object? o)
    {
        var s = o?.ToString() ?? "null";
        s = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length > 800 ? s[..800] + "…" : s;
    }
}

public static class Wait
{
    /// <summary>Poll until <paramref name="condition"/> holds or fail after <paramref name="timeoutMs"/>.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new AssertException($"timed out after {timeoutMs} ms waiting for: {what}");
            await Task.Delay(25);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new AssertException($"timed out after {timeoutMs} ms waiting for: {what}");
            await Task.Delay(25);
        }
    }
}

public static class T
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "netpi-host-tests", Environment.ProcessId.ToString());

    public static string TempDir(string name)
    {
        var dir = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Cleanup()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }

    public static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "NetPI.slnx"))) d = d.Parent;
            return d?.FullName ?? throw new AssertException("repository root not found");
        }
    }

    public static void CopyDir(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(source)) CopyDir(d, Path.Combine(target, Path.GetFileName(d)));
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
        int passed = 0, failed = 0;
        var failures = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var (name, body) in selected)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var task = body();
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(120))) != task)
                    throw new AssertException("test timed out after 120s");
                await task;
                passed++;
                Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds}ms)");
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
        Console.WriteLine($"{passed} passed, {failed} failed, {selected.Count} total in {total.Elapsed.TotalSeconds:0.0}s");
        if (failed > 0)
        {
            Console.WriteLine("Failures:");
            foreach (var f in failures) Console.WriteLine("  - " + f.Split('\n')[0]);
        }
        return failed > 0 ? 1 : nothing ? NoTestSelected : 0;
    }
}
