using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.E2E;

public enum Outcome { Passed, Failed, TimedOut, NotRun }

public sealed class TestResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Outcome Outcome { get; init; }
    public long Ms { get; init; }
    public int Checks { get; init; }
    public int Shard { get; init; }
    public int Attempt { get; init; } = 1;
    public string? Message { get; init; }
    public string? Artifact { get; init; }
    public string? Reason { get; init; }
    /// <summary>What the server answered when asked after this test failed (null for a pass): "responsive", or what is wrong with it.</summary>
    public string? Server { get; init; }
    /// <summary>What the test left running when it returned (agents it never waited for), if anything.</summary>
    public string? Leak { get; init; }
    /// <summary>The leak could not be cleaned up (an agent that would not stop, or a server that could not be asked): the server is not reused.</summary>
    public bool LeakUnclean { get; init; }
}

/// <summary>What one shard runs against: the tests bound to its own server, and how to explain a failure on it.</summary>
public sealed class ShardContext : IAsyncDisposable
{
    public required TestRunner Runner { get; init; }
    public Func<Task<DiagMark>> Mark { get; init; } = () => Task.FromResult(default(DiagMark));
    public Func<DiagMark, Task<string>> Diagnose { get; init; } = _ => Task.FromResult("");
    /// <summary>Asks the server whether it is alive and where it is stuck, once a test has failed.</summary>
    public Func<Task<ServerTriage>> Triage { get; init; } = () => Task.FromResult(ServerTriage.Unknown);
    /// <summary>Called when a test has returned: waits for what it left running to finish, stops what will not, reports the leak.</summary>
    public Func<Task<SettleResult>> Settle { get; init; } = () => Task.FromResult(SettleResult.Quiet);
    public Func<ValueTask> Close { get; init; } = () => ValueTask.CompletedTask;
    public IReadOnlyDictionary<string, long> Setup { get; init; } = new Dictionary<string, long>();

    public ValueTask DisposeAsync() => Close();
}

public sealed class RunReport
{
    public required List<TestResult> Results { get; init; }
    public required JsonObject Json { get; init; }
    public int ExitCode { get; init; }
    public string OutDir { get; init; } = "";
}

/// <summary>
/// Runs the selected tests: splits them over shards (each a server + mock of its own) by how long they took last time, runs
/// every shard's tests in registration order, and contains a failure: a test that did not pass (failed or timed out) is asked
/// "is your server still alive?" and its server is stopped, so the rest of the shard starts on a fresh one and a wedged server
/// cannot turn one failure into a dozen. What it saw is left behind: a failure file per failed test, report.json, the timing
/// history and the list of tests that are failing right now.
/// </summary>
public sealed class Orchestrator
{
    private readonly Lock _print = new();

    public required E2EOptions Options { get; init; }
    /// <summary>Starts the server + mock of shard <c>i</c> and registers the tests against it.</summary>
    public required Func<int, Task<ShardContext>> StartShard { get; init; }
    public string[] CommandLine { get; init; } = [];
    public string Dll { get; init; } = typeof(Orchestrator).Assembly.Location;

    private sealed record Work(TestCase Case, int Attempt);

    public static int AutoMaxShards => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    // ------------------------------------------------------------------ scheduling

    public static int ShardCount(int workItems, double estimatedMs, int requested)
    {
        if (workItems <= 0) return 1;
        if (requested > 0) return Math.Min(requested, workItems);
        return Math.Clamp((int)Math.Ceiling(estimatedMs / 12_000.0), 1, Math.Min(AutoMaxShards, workItems));
    }

    /// <summary>Longest first onto the least loaded shard; each shard then runs its tests in registration order.</summary>
    public static List<List<T>> Partition<T>(IReadOnlyList<T> items, Func<T, double> cost, Func<T, int> order, int shards)
    {
        var lists = Enumerable.Range(0, shards).Select(_ => new List<T>()).ToList();
        var load = new double[shards];
        foreach (var item in items.OrderByDescending(cost).ThenBy(order))
        {
            var target = Array.IndexOf(load, load.Min());
            lists[target].Add(item);
            load[target] += cost(item);
        }
        foreach (var l in lists) l.Sort((a, b) => order(a).CompareTo(order(b)));
        return lists;
    }

    // ------------------------------------------------------------------ the run

    public async Task<RunReport> RunAsync(IReadOnlyList<TestCase> selected)
    {
        var wall = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(Options.OutDir);
        var timings = Timings.Load(Options.StateDir);

        var work = new List<Work>();
        for (var attempt = 1; attempt <= Math.Max(1, Options.Repeat); attempt++)
            foreach (var c in selected) work.Add(new Work(c, attempt));
        double Cost(Work w) => timings.TryGetValue(w.Case.Id, out var ms) ? ms : Catalog.DefaultEstimateMs(w.Case);
        var shards = ShardCount(work.Count, work.Sum(Cost), Options.Parallel);
        var lists = Partition(work, Cost, w => w.Case.Order * 10_000 + w.Attempt, shards);

        Console.WriteLine($"NetPI E2E: {selected.Count} test(s){(Options.Repeat > 1 ? $" x{Options.Repeat}" : "")} on {shards} shard(s), results in {Options.OutDir}");

        var shardInfo = new ShardStats[shards];
        var outputs = await Task.WhenAll(lists.Select((l, i) => RunShardAsync(i, l, shardInfo)));
        var results = outputs.SelectMany(r => r).OrderBy(r => selected.ToList().FindIndex(c => c.Id == r.Id)).ThenBy(r => r.Attempt).ToList();
        wall.Stop();

        var report = BuildReport(results, selected, shardInfo, wall.Elapsed, startedAt);
        Print(results, shardInfo, wall.Elapsed, report.ExitCode);
        try { File.WriteAllText(Path.Combine(Options.OutDir, "report.json"), report.Json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Console.WriteLine($"  could not write report.json: {ex.Message}"); }
        Timings.Save(Options.StateDir, timings, results);
        FailingState.Update(Options.StateDir, results, Path.GetFileName(Options.OutDir));
        PruneRunDirs(Options.OutDir, Options.StateDir);
        return report;
    }

    private sealed class ShardStats
    {
        public int Index;
        public int Tests;
        public int Starts;
        public long SetupMs, TestsMs, TeardownMs;
        public Dictionary<string, long> Phases = [];
        public string? SetupError;
    }

    private async Task<List<TestResult>> RunShardAsync(int index, List<Work> list, ShardStats[] stats)
    {
        var st = stats[index] = new ShardStats { Index = index, Tests = list.Count };
        var results = new List<TestResult>();
        var queue = new Queue<Work>(list);
        ShardContext? ctx = null;
        try
        {
            while (queue.Count > 0)
            {
                if (ctx is null)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        ctx = await StartShard(index);
                        st.Starts++;
                        foreach (var (k, v) in ctx.Setup) st.Phases[k] = st.Phases.GetValueOrDefault(k) + v;
                    }
                    catch (Exception ex)
                    {
                        st.SetupMs += sw.ElapsedMilliseconds;
                        st.SetupError = ex.Message.Split('\n')[0];
                        var why = "setup of shard " + (index + 1) + " failed: " + ex;
                        while (queue.Count > 0)
                        {
                            var w = queue.Dequeue();
                            results.Add(new TestResult { Id = w.Case.Id, Name = w.Case.Name, Outcome = Outcome.NotRun, Shard = index, Attempt = w.Attempt, Reason = why });
                        }
                        Locked(() => Console.WriteLine($"  SETUP FAILED (shard {index + 1}): {ex.Message.Split('\n')[0]}"));
                        break;
                    }
                    st.SetupMs += sw.ElapsedMilliseconds;
                }

                var next = queue.Dequeue();
                var t = Stopwatch.StartNew();
                var r = await RunOneAsync(ctx!, next, index, stats.Length);
                st.TestsMs += t.ElapsedMilliseconds;
                results.Add(r);
                // A failed test leaves a server in a state nobody knows: a timed-out body may still be running against it, a wedged
                // server answers nothing, a leaked agent is still at work. None of that may reach the next test's fixture (one hang
                // once turned into six "failures" on the same shard, each waiting out its 30 s timeout): stop the server and give the
                // rest of the shard a fresh one. A test that passed but left something running that would not stop does the same.
                // --fresh does this every time.
                if (r.Outcome != Outcome.Passed || r.LeakUnclean || Options.Fresh)
                {
                    var td = Stopwatch.StartNew();
                    await CloseQuietly(ctx);
                    st.TeardownMs += td.ElapsedMilliseconds;
                    ctx = null;
                }
            }
        }
        finally
        {
            if (ctx is not null)
            {
                var td = Stopwatch.StartNew();
                await CloseQuietly(ctx);
                st.TeardownMs += td.ElapsedMilliseconds;
            }
        }
        return results;
    }

    private static async Task CloseQuietly(ShardContext ctx)
    {
        try { await ctx.DisposeAsync(); } catch { }
    }

    private void Locked(Action a)
    {
        lock (_print) a();
    }

    private async Task<TestResult> RunOneAsync(ShardContext ctx, Work w, int shard, int shards)
    {
        var bound = ctx.Runner.Cases.First(c => c.Id == w.Case.Id);
        var counter = new int[1];
        var buffer = new StringBuilder();
        var mark = default(DiagMark);
        try { mark = await ctx.Mark(); } catch { }
        var timeout = TimeSpan.FromSeconds(Math.Max(1, bound.TimeoutSeconds * Options.TimeoutScale));
        var sw = Stopwatch.StartNew();
        var body = Task.Run(async () =>
        {
            Check.Attach(counter);
            RoutedConsole.Attach(buffer);
            await bound.Body();
        });
        Outcome outcome;
        string? message = null;
        try
        {
            if (await Task.WhenAny(body, Task.Delay(timeout)) != body)
            {
                outcome = Outcome.TimedOut;
                message = $"test timed out after {timeout.TotalSeconds:0}s (its server is stopped so the body cannot touch the next test's fixture)";
                _ = body.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            else
            {
                await body;
                outcome = Outcome.Passed;
            }
        }
        catch (Exception ex)
        {
            outcome = Outcome.Failed;
            message = ex is AssertException ? ex.Message : ex.ToString();
        }
        var ms = sw.ElapsedMilliseconds;
        string? artifact = null, server = null, leak = null;
        var unclean = false;
        if (outcome != Outcome.Passed)
        {
            // first the server (what is wrong with it is the most useful thing to know), then what it logged and said
            ServerTriage triage;
            try { triage = await ctx.Triage(); } catch (Exception ex) { triage = ServerTriage.Of("could not be checked: " + ex.Message); }
            server = triage.Summary;
            string diag;
            try { diag = await ctx.Diagnose(mark); } catch (Exception ex) { diag = "--- diagnostics failed\n" + ex.Message + "\n"; }
            artifact = WriteFailureFile(bound, w.Attempt, outcome, ms, shard, message!, buffer.ToString(), diag, triage);
        }
        else
        {
            // the server stays for the next test only if the test left nothing of its own running on it
            try
            {
                var settled = await ctx.Settle();
                leak = settled.Leak;
                unclean = !settled.Clean;
            }
            catch (Exception ex) { leak = "could not check for leftovers: " + ex.Message.Split('\n')[0]; unclean = true; }
        }
        var result = new TestResult
        {
            Id = bound.Id, Name = bound.Name, Outcome = outcome, Ms = ms, Checks = counter[0], Shard = shard, Attempt = w.Attempt,
            Message = message, Artifact = artifact, Server = server, Leak = leak, LeakUnclean = unclean,
        };
        PrintOne(result, buffer.ToString(), shards);
        return result;
    }

    private string? WriteFailureFile(TestCase c, int attempt, Outcome outcome, long ms, int shard, string message, string output, string diag, ServerTriage triage)
    {
        try
        {
            var dir = Path.Combine(Options.OutDir, "failures");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, attempt > 1 || Options.Repeat > 1 ? $"{c.Id}-{attempt}.txt" : $"{c.Id}.txt");
            // A section with nothing in it reads as "nothing happened" and says nothing: every one says what it has instead.
            if (output.Trim().Length == 0) output = "(the test printed nothing)\n";
            if (diag.Trim().Length == 0) diag = "--- diagnostics\n(none were captured for this test: the runner that produced this file has no server to ask)\n";
            var text = $"{c.Id}\n{c.Name}\n{outcome} after {ms} ms on shard {shard + 1} (attempt {attempt})\n\n" +
                       $"--- server state (asked right after the failure)\n{triage.Describe()}\n\n" +
                       $"--- message\n{message}\n\n--- test output\n{output}\n{diag}";
            File.WriteAllText(file, text);
            var empty = Evidence.EmptySections(text);
            if (empty.Count > 0) Locked(() => Console.WriteLine($"  evidence incomplete for {c.Id}: empty section(s) {string.Join(", ", empty)} (a bug in the runner)"));
            return file;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ output

    private void PrintOne(TestResult r, string output, int shards)
    {
        lock (_print)
        {
            if (output.Length > 0) Console.Write(output.EndsWith('\n') ? output : output + "\n");
            var tag = shards > 1 ? $"[{r.Shard + 1}] " : "";
            var rep = Options.Repeat > 1 ? $" #{r.Attempt}" : "";
            if (r.Outcome == Outcome.Passed)
            {
                Console.WriteLine($"  PASS  {tag}{r.Id}{rep}  {r.Name} ({r.Ms}ms, {r.Checks} checks)");
                if (r.Leak is not null) Console.WriteLine($"        LEAK: the test {r.Leak}");
                return;
            }
            var lines = (r.Message ?? "").Split('\n');
            var head = string.Join("\n        ", lines.Take(14));
            Console.WriteLine($"  {(r.Outcome == Outcome.TimedOut ? "TIME" : "FAIL")}  {tag}{r.Id}{rep}  {r.Name} ({r.Ms}ms)\n        {head}");
            if (lines.Length > 14) Console.WriteLine($"        … {lines.Length - 14} more lines");
            if (r.Server is not null && r.Server != "responsive" && r.Server != "not checked") Console.WriteLine($"        server: {r.Server}");
            if (r.Artifact is not null) Console.WriteLine($"        details: {r.Artifact}");
        }
    }

    private void Print(List<TestResult> results, ShardStats[] shardInfo, TimeSpan wall, int exitCode)
    {
        var passed = results.Count(r => r.Outcome == Outcome.Passed);
        var failed = results.Count(r => r.Outcome is Outcome.Failed or Outcome.TimedOut);
        var notRun = results.Count(r => r.Outcome == Outcome.NotRun);
        var setup = shardInfo.Max(s => s.SetupMs) / 1000.0;
        var tests = shardInfo.Max(s => s.TestsMs) / 1000.0;
        var down = shardInfo.Max(s => s.TeardownMs) / 1000.0;
        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed{(notRun > 0 ? $", {notRun} not run" : "")}, {results.Count} total, {Check.Total} checks in {wall.TotalSeconds:0.0}s " +
                          $"(setup {setup:0.0}s + tests {tests:0.0}s + teardown {down:0.0}s, {shardInfo.Length} shard(s))");
        var slow = results.Where(r => r.Outcome != Outcome.NotRun).OrderByDescending(r => r.Ms).Take(5).ToList();
        if (slow.Count > 0 && results.Count > 5) Console.WriteLine("slowest: " + string.Join(", ", slow.Select(r => $"{r.Id} {r.Ms / 1000.0:0.0}s")));

        foreach (var r in results.Where(r => r.Leak is not null))
            Console.WriteLine($"LEAK   {r.Id}: {r.Leak}. A test ends with nothing of its own running: the next test shares the server.");

        var flaky = results.GroupBy(r => r.Id).Where(g => g.Count() > 1 && g.Any(r => r.Outcome == Outcome.Passed) && g.Any(r => r.Outcome is Outcome.Failed or Outcome.TimedOut)).ToList();
        foreach (var g in flaky)
            Console.WriteLine($"FLAKY  {g.Key}: failed {g.Count(r => r.Outcome is Outcome.Failed or Outcome.TimedOut)} of {g.Count()} runs. Its failure files hold the evidence; this is a bug in the test or the code, not weather.");

        var bad = results.Where(r => r.Outcome is Outcome.Failed or Outcome.TimedOut).ToList();
        if (bad.Count > 0)
        {
            Console.WriteLine($"\nFAILED ({bad.Select(b => b.Id).Distinct().Count()}):");
            foreach (var b in bad.DistinctBy(x => x.Id))
                Console.WriteLine($"  {b.Id}: {(b.Message ?? "").Split('\n')[0]}");
            var ids = bad.Select(b => b.Id).Distinct().ToList();
            // scripts/e2e.ps1 sets NETPI_E2E_RERUN to its own command line ({ids} = comma-separated); a direct run gets the dotnet one
            var template = Environment.GetEnvironmentVariable("NETPI_E2E_RERUN");
            Console.WriteLine("\nRerun just these (seconds, not the whole run):\n  " + (string.IsNullOrEmpty(template)
                ? $"dotnet \"{Dll}\" {string.Join(" ", ids)}" : template.Replace("{ids}", string.Join(",", ids))));
            Console.WriteLine($"Evidence per failure: {Path.Combine(Options.OutDir, "failures")}");
        }
        if (notRun > 0)
        {
            Console.WriteLine($"\nNOT RUN ({notRun}): {string.Join(", ", results.Where(r => r.Outcome == Outcome.NotRun).Select(r => r.Id).Distinct())}");
            foreach (var s in shardInfo.Where(s => s.SetupError is not null)) Console.WriteLine($"  shard {s.Index + 1} setup: {s.SetupError}");
        }
        if (exitCode == 0) Console.WriteLine("All green.");
    }

    private RunReport BuildReport(List<TestResult> results, IReadOnlyList<TestCase> selected, ShardStats[] shardInfo, TimeSpan wall, DateTimeOffset startedAt)
    {
        var anySetupFailure = shardInfo.Any(s => s.SetupError is not null);
        var anyFailure = results.Any(r => r.Outcome is Outcome.Failed or Outcome.TimedOut);
        var exit = anySetupFailure ? 3 : anyFailure ? 1 : 0;
        var json = new JsonObject
        {
            ["schema"] = 1,
            ["startedAt"] = startedAt.ToString("o"),
            ["revision"] = Revision(),
            ["command"] = new JsonArray(CommandLine.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            ["cores"] = Environment.ProcessorCount,
            ["selected"] = new JsonArray(selected.Select(c => (JsonNode)JsonValue.Create(c.Id)!).ToArray()),
            ["repeat"] = Options.Repeat,
            ["fresh"] = Options.Fresh,
            ["wallMs"] = (long)wall.TotalMilliseconds,
            ["exitCode"] = exit,
            ["counts"] = new JsonObject
            {
                ["passed"] = results.Count(r => r.Outcome == Outcome.Passed),
                ["failed"] = results.Count(r => r.Outcome == Outcome.Failed),
                ["timedOut"] = results.Count(r => r.Outcome == Outcome.TimedOut),
                ["notRun"] = results.Count(r => r.Outcome == Outcome.NotRun),
                ["checks"] = Check.Total,
            },
            ["shards"] = new JsonArray(shardInfo.Select(s => (JsonNode)new JsonObject
            {
                ["index"] = s.Index + 1, ["tests"] = s.Tests, ["serverStarts"] = s.Starts,
                ["setupMs"] = s.SetupMs, ["testsMs"] = s.TestsMs, ["teardownMs"] = s.TeardownMs,
                ["setupPhasesMs"] = new JsonObject(s.Phases.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),
                ["setupError"] = s.SetupError,
            }).ToArray()),
            ["tests"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["name"] = r.Name, ["outcome"] = r.Outcome.ToString(), ["ms"] = r.Ms, ["checks"] = r.Checks,
                ["shard"] = r.Shard + 1, ["attempt"] = r.Attempt, ["message"] = r.Message, ["artifact"] = r.Artifact, ["reason"] = r.Reason,
                ["server"] = r.Server, ["leak"] = r.Leak,
            }).ToArray()),
        };
        return new RunReport { Results = results, Json = json, ExitCode = exit, OutDir = Options.OutDir };
    }

    private static string Revision()
    {
        try
        {
            string Git(params string[] a)
            {
                var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Env.FindRepoRoot() };
                foreach (var x in a) psi.ArgumentList.Add(x);
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return o;
            }
            var sha = Git("rev-parse", "--short", "HEAD");
            return Git("status", "--porcelain").Length > 0 ? sha + "+dirty" : sha;
        }
        catch { return "unknown"; }
    }

    /// <summary>Keeps the 20 newest run folders in the state dir and touches nothing else: a run that was told to write somewhere
    /// of its own (<c>--out</c>) never has its neighbours deleted.</summary>
    private static void PruneRunDirs(string outDir, string stateDir)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(outDir));
            if (stateDir.Length == 0 || parent is null || !Directory.Exists(parent)) return;
            if (!string.Equals(Path.GetFullPath(parent), Path.GetFullPath(stateDir), StringComparison.OrdinalIgnoreCase)) return;
            var runs = Directory.GetDirectories(parent).Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^\d{8}-\d{6}-[0-9a-f]{4}$"))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(20);
            foreach (var d in runs) try { Directory.Delete(d, recursive: true); } catch { }
        }
        catch { }
    }
}

/// <summary>How long each test took the last time it passed: what the scheduler splits the next run by.</summary>
public static class Timings
{
    public static Dictionary<string, double> Load(string stateDir)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(stateDir, "timings.json");
            if (stateDir.Length > 0 && File.Exists(path))
                foreach (var p in JsonNode.Parse(File.ReadAllText(path))!.AsObject()) map[p.Key] = p.Value!.GetValue<double>();
        }
        catch { }
        return map;
    }

    public static void Save(string stateDir, Dictionary<string, double> map, IEnumerable<TestResult> results)
    {
        if (stateDir.Length == 0) return;
        try
        {
            foreach (var g in results.Where(r => r.Outcome == Outcome.Passed).GroupBy(r => r.Id)) map[g.Key] = g.Average(r => r.Ms);
            Directory.CreateDirectory(stateDir);
            var o = new JsonObject();
            foreach (var p in map.OrderBy(p => p.Key, StringComparer.Ordinal)) o[p.Key] = Math.Round(p.Value);
            File.WriteAllText(Path.Combine(stateDir, "timings.json"), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>
/// The tests that failed and have not passed since, across runs: a rerun of a few tests must not make a failure from the
/// big run forget itself. A pass removes a test from the list, a failure adds it, a test that did not run stays as it was.
/// </summary>
public static class FailingState
{
    public static Dictionary<string, (string Message, string Run)> Load(string stateDir)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(stateDir, "failing.json");
            if (stateDir.Length > 0 && File.Exists(path))
                foreach (var e in JsonNode.Parse(File.ReadAllText(path))!["failing"]!.AsArray())
                    map[e!["id"]!.GetValue<string>()] = (e["message"]?.GetValue<string>() ?? "", e["run"]?.GetValue<string>() ?? "");
        }
        catch { }
        return map;
    }

    public static void Update(string stateDir, IEnumerable<TestResult> results, string run)
    {
        if (stateDir.Length == 0) return;
        try
        {
            var map = Load(stateDir);
            foreach (var g in results.GroupBy(r => r.Id))
            {
                var bad = g.FirstOrDefault(r => r.Outcome is Outcome.Failed or Outcome.TimedOut);
                if (bad is not null) map[g.Key] = ((bad.Message ?? "").Split('\n')[0], run);
                else if (g.Any(r => r.Outcome == Outcome.Passed)) map.Remove(g.Key);
            }
            Directory.CreateDirectory(stateDir);
            var arr = new JsonArray(map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Key, ["message"] = p.Value.Message, ["run"] = p.Value.Run,
            }).ToArray());
            File.WriteAllText(Path.Combine(stateDir, "failing.json"), new JsonObject { ["failing"] = arr }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
