using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.E2E;

/// <summary>
/// The runner's own tests (<c>--self-test</c>): selection, scheduling, containment of a timed-out test, the report and the
/// failing list. No server, no browser: they run in a few seconds, and they are what makes the E2E runner safe to change.
/// </summary>
public static class RunnerSelfTests
{
    public static async Task<int> RunAsync()
    {
        var tests = new List<(string Name, Func<Task> Body)>
        {
            ("catalog: ids are unique, well formed, and every tag entry names a real test", Catalog_IsConsistent),
            ("selection: exact id, id/name substring, tag, skip-tag", Selection_Modes),
            ("selection: a typo or an empty selection is an error, with a suggestion", Selection_Errors),
            ("scheduling: shards follow the estimated work; the longest tests spread out; each shard keeps registration order", Scheduling),
            ("ports: two asks never get the same port", Ports_AreDistinct),
            ("output: console lines of concurrent tests stay with their test", Output_IsRouted),
            ("containment: a timed-out body cannot touch the next test's fixture", Containment_TimedOutBodyIsCut),
            ("report: outcomes, exit codes, all failures at once, evidence files", Report_AndExitCodes),
            ("setup failure: the shard's tests are reported as not run, exit 3", SetupFailure_IsNotRun),
            ("repeat: a test that fails sometimes is reported as flaky, with its failure file per attempt", Repeat_FindsFlaky),
            ("failing list: a failure is remembered until it passes; a rerun of others does not forget it", FailingList_Persists),
            ("areas.json: every tag, id and path it names exists, and every plugin folder is mapped", Areas_AreConsistent),
        };
        var failed = 0;
        foreach (var (name, body) in tests)
        {
            try { await body(); Console.WriteLine($"  PASS  {name}"); }
            catch (Exception ex) { failed++; Console.WriteLine($"  FAIL  {name}\n        {(ex is AssertException ? ex.Message : ex.ToString()).Replace("\n", "\n        ")}"); }
        }
        Console.WriteLine($"\n{tests.Count - failed} passed, {failed} failed (runner self-test)");
        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ helpers

    private static List<TestCase> Cases(params (string Id, string Name)[] items)
    {
        var r = new TestRunner();
        foreach (var (id, name) in items) r.Add(id, name, () => Task.CompletedTask);
        return r.Cases.ToList();
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "netpi-e2e-selftest", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Runs the orchestrator silently (its console output is captured) over fake shards; the fakes come from <paramref name="start"/>.</summary>
    private static async Task<(RunReport Report, string Output, string Out, string State)> Run(IReadOnlyList<TestCase> selected, Func<int, Task<ShardContext>> start,
        Action<E2EOptions>? configure = null)
    {
        var root = TempDir();
        var o = new E2EOptions { OutDir = Path.Combine(root, "out"), StateDir = Path.Combine(root, "state"), Parallel = 1 };
        configure?.Invoke(o);
        var buffer = new StringBuilder();
        RoutedConsole.Attach(buffer);
        try
        {
            var report = await new Orchestrator { Options = o, StartShard = start, CommandLine = ["self-test"] }.RunAsync(selected);
            return (report, buffer.ToString(), o.OutDir, o.StateDir);
        }
        finally { RoutedConsole.Attach(null); }
    }

    private static ShardContext Shard(TestRunner r, Action? onClose = null) => new() { Runner = r, Close = () => { onClose?.Invoke(); return ValueTask.CompletedTask; } };

    // ------------------------------------------------------------------ tests

    private static Task Catalog_IsConsistent()
    {
        var all = Suite.RegisterAll(null).Cases;
        Check.True(all.Count > 40, $"the suite has tests ({all.Count})");
        foreach (var c in all)
            Check.True(Regex.IsMatch(c.Id, @"^[a-z0-9]+(\.[a-z0-9-]+)+$"), $"id '{c.Id}' is area.name in lower case");
        Check.Equal(all.Count, all.Select(c => c.Name).Distinct().Count(), "test names are unique");
        foreach (var id in Catalog.ExtraIds) Check.True(all.Any(c => c.Id == id), $"Catalog lists tags for '{id}', which is not a test");
        Check.True(all.Count(c => c.Tags.Contains("smoke")) is >= 5 and <= 14, "smoke stays a short list");
        Check.True(all.Where(c => c.Tags.Contains("ui")).All(c => c.Tags.Contains("needs-node")), "every ui test is marked as needing node");
        var dup = new TestRunner();
        dup.Add("a.b", "x", () => Task.CompletedTask);
        try { dup.Add("a.b", "y", () => Task.CompletedTask); throw new AssertException("a duplicate id was accepted"); }
        catch (InvalidOperationException) { }
        return Task.CompletedTask;
    }

    private static Task Selection_Modes()
    {
        var all = Cases(("retry.drop", "retry: dropped connection"), ("retry.503", "retry: HTTP 503"), ("chat.stream", "chat: streams"),
            ("ui.smoke", "ui: browser"), ("slots.three-chats", "slots: three chats, retry not involved"));
        string[] None = [];
        Check.Equal("chat.stream", string.Join(",", Selection.Resolve(all, ["chat.stream"], None, None).Selected.Select(c => c.Id)), "exact id");
        Check.Equal("retry.drop", string.Join(",", Selection.Resolve(all, ["retry.drop"], None, None).Selected.Select(c => c.Id)), "an exact id does not also pull in look-alikes");
        Check.Equal("retry.drop,retry.503", string.Join(",", Selection.Resolve(all, ["retry"], None, None).Selected.Select(c => c.Id)), "a substring of ids: a sentence that merely mentions retry is not pulled in");
        Check.Equal("slots.three-chats", string.Join(",", Selection.Resolve(all, ["not involved"], None, None).Selected.Select(c => c.Id)), "names are searched when no id matches");
        Check.Equal("retry.drop,retry.503", string.Join(",", Selection.Resolve(all, [], ["retry"], None).Selected.Select(c => c.Id)), "tag = area");
        Check.Equal("retry.drop,chat.stream", string.Join(",", Selection.Resolve(all, ["chat.stream", "DROPPED"], None, None).Selected.Select(c => c.Id)), "filters are OR-ed, case-insensitive");
        Check.Equal("retry.drop,retry.503,chat.stream,slots.three-chats", string.Join(",", Selection.Resolve(all, [], None, ["ui"]).Selected.Select(c => c.Id)), "no selection = all, minus skipped tags");
        Check.Equal(5, Selection.Resolve(all, [], None, None).Selected.Count, "no filter = everything");
        return Task.CompletedTask;
    }

    private static Task Selection_Errors()
    {
        var all = Cases(("retry.drop", "retry: dropped connection"), ("chat.stream", "chat: streams"), ("ui.smoke", "ui: browser"));
        string[] None = [];
        var typo = Selection.Resolve(all, ["chat.strem"], None, None);
        Check.Equal(0, typo.Selected.Count, "a typo selects nothing");
        Check.Contains(string.Join("\n", typo.Errors), "matches no test", "and says so");
        Check.Contains(string.Join("\n", typo.Errors), "chat.stream", "with the test it probably meant");
        Check.True(Selection.Resolve(all, [], ["nope"], None).Errors.Count == 1, "an unknown tag is an error");
        Check.True(Selection.Resolve(all, ["ui.smoke"], None, ["ui"]).Errors.Count == 1, "everything skipped is an error, not a green empty run");
        Check.True(Selection.Resolve(all, [], None, ["nope"]).Errors.Count == 1, "an unknown skip tag is an error");
        Check.True(Selection.Resolve(all, ["retry.drop", "zzzzzz"], None, None).Errors.Count == 1, "one bad filter among good ones still fails");
        return Task.CompletedTask;
    }

    private static Task Scheduling()
    {
        Check.Equal(1, Orchestrator.ShardCount(5, 6_000, 0), "little work stays on one server");
        Check.True(Orchestrator.ShardCount(60, 125_000, 0) <= Orchestrator.AutoMaxShards, "auto never exceeds the cap");
        Check.Equal(Math.Min(2, Orchestrator.AutoMaxShards), Orchestrator.ShardCount(2, 500_000, 0), "never more shards than tests");
        Check.Equal(3, Orchestrator.ShardCount(10, 1_000, 3), "--parallel is taken as asked");
        Check.Equal(2, Orchestrator.ShardCount(2, 1_000, 8), "...but not beyond the number of tests");
        var items = new (string Id, int Order, double Ms)[] { ("a", 0, 25_000), ("b", 1, 7_000), ("c", 2, 6_000), ("d", 3, 5_000), ("e", 4, 4_000), ("f", 5, 4_000), ("g", 6, 3_000) };
        var lists = Orchestrator.Partition(items, x => x.Ms, x => x.Order, 3);
        Check.Equal(7, lists.Sum(l => l.Count), "every test is placed exactly once");
        Check.True(lists.Single(l => l.Any(x => x.Id == "a")).Count == 1, "the long test has a shard to itself when the rest fit beside it");
        foreach (var l in lists) Check.True(l.Select(x => x.Order).SequenceEqual(l.Select(x => x.Order).Order()), "a shard keeps registration order");
        Check.True(lists.Max(l => l.Sum(x => x.Ms)) <= 25_000, "the heaviest shard is no heavier than the longest test here");
        return Task.CompletedTask;
    }

    private static Task Ports_AreDistinct()
    {
        var a = Ports.Pick(6);
        var b = Ports.Pick(6);
        Check.Equal(12, a.Concat(b).Distinct().Count(), "twelve asks, twelve different ports");
        return Task.CompletedTask;
    }

    private static async Task Output_IsRouted()
    {
        RoutedConsole.Install();
        var b1 = new StringBuilder();
        var b2 = new StringBuilder();
        async Task Body(StringBuilder b, string tag)
        {
            RoutedConsole.Attach(b);
            for (var i = 0; i < 20; i++) { Console.WriteLine($"{tag}{i}"); await Task.Delay(1); }
        }
        await Task.WhenAll(Task.Run(() => Body(b1, "a")), Task.Run(() => Body(b2, "b")));
        Check.True(b1.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).All(l => l.StartsWith('a')) && b1.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 20, "test a's lines are all its own");
        Check.True(b2.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).All(l => l.StartsWith('b')) && b2.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 20, "test b's lines are all its own");
    }

    private sealed class Fixture
    {
        public int Generation;
        public readonly List<string> Events = [];
        public volatile bool Closed;
    }

    private static async Task Containment_TimedOutBodyIsCut()
    {
        // test A never finishes in time and keeps writing into "its" fixture afterwards; test B must run on another fixture,
        // started after the first was closed, and must never see A's late write.
        var fixtures = new List<Fixture>();
        var dry = Cases(("a.slow", "A"), ("b.next", "B"));
        var seenByB = new List<string>();
        var oldClosedWhenBStarted = false;
        var (report, _, _, _) = await Run(dry, async shard =>
        {
            await Task.Yield();
            var fx = new Fixture { Generation = fixtures.Count + 1 };
            lock (fixtures) fixtures.Add(fx);
            var r = new TestRunner();
            r.Add("a.slow", "A", async () =>
            {
                fx.Events.Add("A-start");
                await Task.Delay(2500);
                fx.Events.Add("A-late-write");
            }, timeoutSeconds: 1);
            r.Add("b.next", "B", async () =>
            {
                fx.Events.Add("B-start");
                oldClosedWhenBStarted = fixtures.Count == 2 && fixtures[0].Closed;
                await Task.Delay(3200); // long enough for A's late write to happen while B runs
                seenByB.AddRange(fx.Events);
            }, timeoutSeconds: 10);
            return Shard(r, () => fx.Closed = true);
        });
        Check.Equal(Outcome.TimedOut, report.Results[0].Outcome, "A timed out");
        Check.Equal(Outcome.Passed, report.Results[1].Outcome, "B still ran");
        Check.Equal(2, fixtures.Count, "B got a fresh fixture");
        Check.True(oldClosedWhenBStarted, "the old fixture was closed before B started");
        Check.Equal("B-start", string.Join(",", seenByB), "B's fixture saw nothing of A, not even A's late write");
        Check.Contains(string.Join(",", fixtures[0].Events), "A-late-write", "A's late write went to the closed fixture");
        Check.Equal(1, report.ExitCode, "a timeout fails the run");
        Check.True(report.Results[0].Artifact is not null && File.Exists(report.Results[0].Artifact!), "the timeout left a failure file");
    }

    private static async Task Report_AndExitCodes()
    {
        var dry = Cases(("a.ok", "ok"), ("b.bad1", "bad one"), ("c.bad2", "bad two"), ("d.ok", "ok too"));
        ShardContext Start()
        {
            var r = new TestRunner();
            r.Add("a.ok", "ok", () => { Check.True(true); Check.True(true); return Task.CompletedTask; });
            r.Add("b.bad1", "bad one", () => throw new AssertException("first problem"));
            r.Add("c.bad2", "bad two", () => { Check.Equal(1, 2, "second problem"); return Task.CompletedTask; });
            r.Add("d.ok", "ok too", () => { Console.WriteLine("  some output of d"); return Task.CompletedTask; });
            return Shard(r);
        }
        var (report, output, outDir, _) = await Run(dry, _ => Task.FromResult(Start()));
        Check.Equal(1, report.ExitCode, "failures give exit 1");
        Check.Equal(2, report.Results.Count(r => r.Outcome == Outcome.Passed), "two passed");
        Check.Equal(2, report.Results.Count(r => r.Outcome == Outcome.Failed), "both failures are reported, not only the first");
        Check.Equal(2, report.Results[0].Checks, "checks are counted per test");
        Check.Contains(output, "some output of d", "a test's console lines are printed");
        Check.Contains(output, "Rerun just these", "the rerun command is printed");
        Check.Contains(output, "b.bad1 c.bad2", "...and names exactly the failing ids");
        Check.True(File.Exists(Path.Combine(outDir, "failures", "b.bad1.txt")) && File.Exists(Path.Combine(outDir, "failures", "c.bad2.txt")), "a failure file per failed test");
        Check.False(File.Exists(Path.Combine(outDir, "failures", "a.ok.txt")), "none for a passing test");
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "report.json")))!;
        Check.Equal(1, json["exitCode"]!.GetValue<int>(), "report.json carries the exit code");
        Check.Equal(2, json["counts"]!["failed"]!.GetValue<int>(), "...and the counts");
        Check.Equal(4, json["tests"]!.AsArray().Count, "...and every test");
        Check.True(json["wallMs"]!.GetValue<long>() >= 0 && json["shards"]!.AsArray().Count == 1, "...and the phase timings per shard");
    }

    private static async Task SetupFailure_IsNotRun()
    {
        var dry = Cases(("a.one", "one"), ("b.two", "two"));
        var (report, output, _, _) = await Run(dry, _ => throw new InvalidOperationException("netpi-server exited with code 1"));
        Check.Equal(3, report.ExitCode, "a server that would not start is exit 3");
        Check.True(report.Results.All(r => r.Outcome == Outcome.NotRun), "nothing ran, and it says so");
        Check.Contains(output, "NOT RUN (2)", "the summary names them");
        Check.False(report.Results.Any(r => r.Outcome is Outcome.Passed or Outcome.Failed), "no test is reported as passed or failed");
    }

    private static async Task Repeat_FindsFlaky()
    {
        var dry = Cases(("a.sometimes", "sometimes"), ("b.always", "always ok"));
        var runs = 0;
        var (report, output, outDir, _) = await Run(dry, _ =>
        {
            var r = new TestRunner();
            r.Add("a.sometimes", "sometimes", () => Interlocked.Increment(ref runs) % 3 == 0 ? throw new AssertException("one in three") : Task.CompletedTask);
            r.Add("b.always", "always ok", () => Task.CompletedTask);
            return Task.FromResult(Shard(r));
        }, o => o.Repeat = 6);
        Check.Equal(12, report.Results.Count, "every test ran six times");
        Check.Equal(2, report.Results.Count(r => r.Id == "a.sometimes" && r.Outcome == Outcome.Failed), "two of six failed");
        Check.Contains(output, "FLAKY  a.sometimes: failed 2 of 6", "the flake is named with its rate");
        Check.True(Directory.GetFiles(Path.Combine(outDir, "failures")).Length == 2, "each failing attempt has its own evidence file");
        Check.Equal(1, report.ExitCode, "a flake fails the run");
    }

    private static Task Areas_AreConsistent()
    {
        var repo = Env.FindRepoRoot();
        var rules = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "tests", "NetPI.E2E", "areas.json")))!["rules"]!.AsArray();
        var cases = Suite.RegisterAll(null).Cases;
        var tags = cases.SelectMany(c => c.Tags).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            var path = rule!["path"]!.GetValue<string>();
            Check.True(Directory.Exists(Path.Combine(repo, path)) || File.Exists(Path.Combine(repo, path)), $"areas.json: '{path}' does not exist");
            var any = false;
            foreach (var t in rule["tags"]?.AsArray() ?? [])
            {
                any = true;
                Check.True(tags.Contains(t!.GetValue<string>()), $"areas.json: '{path}' names the tag '{t}', which no test has");
            }
            foreach (var id in rule["ids"]?.AsArray() ?? [])
            {
                any = true;
                Check.True(cases.Any(c => c.Id == id!.GetValue<string>()), $"areas.json: '{path}' names the test '{id}', which does not exist");
            }
            Check.True(any || rule["all"] is not null || rule["ignore"] is not null, $"areas.json: the rule for '{path}' selects nothing");
        }
        var paths = rules.Select(r => r!["path"]!.GetValue<string>()).ToList();
        foreach (var dir in Directory.GetDirectories(Path.Combine(repo, "plugins")).Select(Path.GetFileName))
            Check.True(paths.Contains($"plugins/{dir}/"), $"areas.json: the plugin '{dir}' has no rule: say which E2E tests cover it (or that none can)");
        foreach (var dir in Directory.GetDirectories(Path.Combine(repo, "src")).Select(Path.GetFileName))
            Check.True(paths.Contains($"src/{dir}/"), $"areas.json: src/{dir} has no rule");
        return Task.CompletedTask;
    }

    private static Task FailingList_Persists()
    {
        var state = TempDir();
        TestResult R(string id, Outcome o) => new() { Id = id, Name = id, Outcome = o, Message = "boom " + id };
        FailingState.Update(state, [R("a.x", Outcome.Failed), R("b.y", Outcome.Failed), R("c.z", Outcome.Passed)], "run1");
        Check.Equal("a.x,b.y", string.Join(",", FailingState.Load(state).Keys.Order()), "failures are recorded");
        FailingState.Update(state, [R("a.x", Outcome.Passed)], "run2");
        Check.Equal("b.y", string.Join(",", FailingState.Load(state).Keys), "a pass removes only that test; the one that was not rerun stays");
        FailingState.Update(state, [R("b.y", Outcome.NotRun)], "run3");
        Check.Equal("b.y", string.Join(",", FailingState.Load(state).Keys), "a test that did not run keeps its state");
        FailingState.Update(state, [R("b.y", Outcome.TimedOut)], "run4");
        Check.Equal("run4", FailingState.Load(state)["b.y"].Run, "a repeated failure points at the latest run");
        return Task.CompletedTask;
    }
}
