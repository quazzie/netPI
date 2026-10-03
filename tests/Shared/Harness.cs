using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NetPI.TestShared;

// The one test framework every suite compiles in (linked, like McpFixture): a second copy of any of these
// drifts, and each suite's runner has to print the same lines and the same result file scripts/test.ps1 reads.

// Not sealed: the E2E client derives RpcTimeoutException from it.
public class AssertException(string message) : Exception(message);

/// <summary>A test that could not run here (no git, no browser, nothing built): a skip ends the test,
/// is counted apart from the passes and prints a SKIP line scripts/test.ps1 counts. Not a pass, and
/// not a failure either: it says what was missing (idea-r4e8rf).</summary>
public sealed class SkipException(string reason) : Exception(reason);

public static class Check
{
    private static int _total;
    // The check count the E2E shards read after a body: with shards several bodies run at once, and each flow owns its counter.
    private static readonly AsyncLocal<int[]?> Scope = new();

    /// <summary>Every check since the process started (the E2E summary prints it).</summary>
    public static int Total => Volatile.Read(ref _total);

    /// <summary>Count this flow's checks into <paramref name="counter"/> (the runner reads it after the body).</summary>
    public static void Attach(int[] counter) => Scope.Value = counter;

    private static void Count()
    {
        Interlocked.Increment(ref _total);
        if (Scope.Value is { } c) Interlocked.Increment(ref c[0]);
    }

    /// <summary>End the test as skipped, with the reason it could not run.</summary>
    [DoesNotReturn]
    public static void Skip(string reason) => throw new SkipException(reason);

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

    public static void Differs<T>(T unexpected, T actual, string? message = null)
    {
        Count();
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new AssertException($"{message ?? "values are equal"}: {Show(actual)}");
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

    /// <summary>The exception a synchronous body threw. A different exception is a failure that names both (so a
    /// test that expects the wrong thing does not read as the right thing's stack trace).</summary>
    public static TEx Throws<TEx>(Action body, string? message = null) where TEx : Exception
    {
        try { body(); }
        catch (TEx ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"{message ?? "wrong exception"}: expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertException($"{message ?? "no exception"}: expected {typeof(TEx).Name}");
    }

    public static async Task<TEx> ThrowsAsync<TEx>(Func<Task> body, string? message = null) where TEx : Exception
    {
        try { await body(); }
        catch (TEx ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"{message ?? "wrong exception"}: expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertException($"{message ?? "no exception"}: expected {typeof(TEx).Name}");
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
    /// <summary>Exit code when the filter selected no test at all: a broken selection, not a pass (idea-jw74xi). The
    /// documented direct run is <c>dotnet &lt;suite&gt;.dll [filter]</c>, and it must not report green for a typo or a
    /// renamed test. scripts/test.ps1 maps a non-zero exit with no FAIL line to a failure.</summary>
    public const int NoTestSelected = 2;

    /// <summary>What the suite is called in the result file (scripts/test.ps1 groups its report by it).</summary>
    public string Name { get; init; } = "suite";

    /// <summary>Per-test timeout. The suites that start real servers give theirs more room.</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Run the body on a thread pool thread (the Agent suite): a body that blocks synchronously then times
    /// out the test instead of hanging the runner.</summary>
    public bool RunOnThreadPool { get; init; }

    private readonly List<(string Name, Func<Task> Body)> _tests = [];

    public void Add(string name, Func<Task> body) => _tests.Add((name, body));
    public void Add(string name, Action body) => _tests.Add((name, () => { body(); return Task.CompletedTask; }));

    /// <summary>Split a command line into filters and a shard. <c>--shard i/n</c> (1-based, the value may be a
    /// second argument, as a command line gives it) selects the i-th of n equal parts of the selected tests —
    /// the clean way to run one suite on several machines.</summary>
    public static (string[] Filters, (int Index, int Count)? Shard) ParseArgs(string[] args)
    {
        string? spec = null;
        var filters = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--shard")
            {
                if (i + 1 >= args.Length) continue;
                i++;
                spec = args[i];
            }
            else if (a.StartsWith("--shard ", StringComparison.Ordinal)) spec = a["--shard ".Length..].Trim();
            else filters.Add(a);
        }
        (int Index, int Count)? shard = null;
        if (spec is { Length: > 0 } && spec.Split('/') is { Length: 2 } parts
            && int.TryParse(parts[0], out var index) && int.TryParse(parts[1], out var count)
            && count > 0 && index >= 1 && index <= count)
            shard = (index, count);
        return (filters.ToArray(), shard);
    }

    public async Task<int> RunAsync(string[] filters)
    {
        var (f, shard) = ParseArgs(filters);
        var selected = _tests.Where(t => f.Length == 0 || f.Any(x => t.Name.Contains(x, StringComparison.OrdinalIgnoreCase))).ToList();
        if (shard is { } s) selected = selected.Where((t, i) => i % s.Count == s.Index - 1).ToList();
        int passed = 0, failed = 0, skipped = 0;
        var failures = new List<string>();
        var results = new List<(string Name, string Result, long Ms)>();
        var total = Stopwatch.StartNew();
        foreach (var (name, body) in selected)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                Func<Task> run = RunOnThreadPool ? () => Task.Run(body) : body;
                var task = run();
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds))) != task)
                    throw new AssertException($"test timed out after {TimeoutSeconds}s");
                await task;
                passed++;
                results.Add((name, "passed", sw.ElapsedMilliseconds));
                Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds}ms)");
            }
            catch (SkipException ex)
            {
                skipped++;
                results.Add((name, "skipped", sw.ElapsedMilliseconds));
                Console.WriteLine($"  SKIP  {name} ({ex.Message})");
            }
            catch (Exception ex)
            {
                failed++;
                var msg = ex is AssertException ? ex.Message : ex.ToString();
                failures.Add($"{name}: {msg}");
                results.Add((name, "failed", sw.ElapsedMilliseconds));
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
            foreach (var fl in failures) Console.WriteLine("  - " + fl.Split('\n')[0]);
        }
        WriteResult(passed, failed, skipped, selected.Count, total.Elapsed, results);
        return failed > 0 ? 1 : nothing ? NoTestSelected : 0;
    }

    /// <summary>When NETPI_TEST_RESULT names a file, write the run there as JSON: the one result contract
    /// scripts/test.ps1 reads instead of scraping console lines (the providers' check-style summary used to
    /// carry no body time, so the process-gap check was blind to that suite).</summary>
    private void WriteResult(int passed, int failed, int skipped, int total, TimeSpan elapsed, List<(string Name, string Result, long Ms)> results)
    {
        var file = Environment.GetEnvironmentVariable("NETPI_TEST_RESULT");
        if (file is not { Length: > 0 }) return;
        File.WriteAllText(file, JsonSerializer.SerializeToElement(new
        {
            suite = Name,
            passed, failed, skipped, total,
            bodySeconds = Math.Round(elapsed.TotalSeconds, 2),
            matchedNothing = total == 0,
            tests = results.Select(r => new { name = r.Name, result = r.Result, ms = r.Ms }),
        }).GetRawText());
    }
}

public static class Wait
{
    /// <summary>Poll until <paramref name="condition"/> holds (throws on timeout).</summary>
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 10_000, int pollMs = 25)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new AssertException($"timed out after {timeoutMs}ms waiting for: {what}");
            await Task.Delay(pollMs);
        }
    }

    public static async Task Until(Func<Task<bool>> condition, string what, int timeoutMs = 10_000, int pollMs = 25)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new AssertException($"timed out after {timeoutMs}ms waiting for: {what}");
            await Task.Delay(pollMs);
        }
    }

    /// <summary>Probe until it answers non-null (the E2E style, where the wait doubles as a query).</summary>
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

public static class T
{
    /// <summary>Root for this run's temporary files. Scoped to the process (or to <c>NETPI_TEST_ROOT</c>, which
    /// scripts/test.ps1 sets per suite) so two runs of the same suite cannot delete each other's directories.</summary>
    public static string TestRoot { get; } = Environment.GetEnvironmentVariable("NETPI_TEST_ROOT") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Path.GetTempPath(), "netpi-tests", Environment.ProcessId.ToString());

    public static string TempDir(string prefix)
    {
        var dir = Path.Combine(TestRoot, prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Delete this run's root. Best effort: a suite may still hold a file open.</summary>
    public static void Cleanup()
    {
        try { Directory.Delete(TestRoot, recursive: true); } catch { }
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

/// <summary>Git for tests that need real repositories (the helper every suite used to copy). A git that is not
/// on PATH answers false/null instead of throwing, so a test skips instead of failing on a machine without it.</summary>
public static class TestGit
{
    /// <summary>Git is on PATH.</summary>
    public static bool Available() => Out(Path.GetTempPath(), "--version") is not null;

    /// <summary>Run git in <paramref name="cwd"/>. True on exit 0; false when git itself is missing.</summary>
    public static bool Run(string cwd, params string[] args)
    {
        var p = Start(cwd, args);
        if (p is null) return false;
        using (p)
        {
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
    }

    /// <summary>Run git and return its trimmed stdout, or null when it failed (or git is missing).</summary>
    public static string? Out(string cwd, params string[] args)
    {
        var p = Start(cwd, args);
        if (p is null) return null;
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? stdout.Trim() : null;
        }
    }

    public static async Task<bool> RunAsync(string cwd, params string[] args)
    {
        var p = Start(cwd, args);
        if (p is null) return false;
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            await stdout;
            await stderr;
            return p.ExitCode == 0;
        }
    }

    public static async Task<string?> OutAsync(string cwd, params string[] args)
    {
        var p = Start(cwd, args);
        if (p is null) return null;
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            await stdout;
            await stderr;
            return p.ExitCode == 0 ? (await stdout).Trim() : null;
        }
    }

    private static Process? Start(string cwd, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try { return Process.Start(psi); }
        catch (Win32Exception) { return null; }
    }
}
