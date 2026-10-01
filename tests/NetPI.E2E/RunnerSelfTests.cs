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
        // the rerun hint reads this (scripts/e2e.ps1 sets it for its child): the tests decide it themselves, not the caller's session
        var inherited = Environment.GetEnvironmentVariable("NETPI_E2E_RERUN");
        Environment.SetEnvironmentVariable("NETPI_E2E_RERUN", null);
        try { return await RunTestsAsync(); }
        finally { Environment.SetEnvironmentVariable("NETPI_E2E_RERUN", inherited); }
    }

    private static async Task<int> RunTestsAsync()
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
            ("containment: a failed test's server is replaced, and the failure file says what was wrong with it", Containment_FailedTestGetsAFreshServer),
            ("triage: the verdict names where a server is stuck, and a slow answer is not a wedge", Triage_Verdicts),
            ("evidence: a failure file never has an empty section", Evidence_NeverEmpty),
            ("settle: what a test left running is reported, and an agent that will not stop costs the server", Settle_ReportsLeaks),
            ("report: outcomes, exit codes, all failures at once, evidence files", Report_AndExitCodes),
            ("rerun hint: the caller's command line when it gave one, else a dotnet command", RerunHint_FollowsTheCaller),
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

    private static async Task Containment_FailedTestGetsAFreshServer()
    {
        // Test A fails because its server is wedged (every later call on it would wait out its timeout). Test B must not meet that
        // server: it gets a new one, and A's failure file says what was wrong with the old one.
        var fixtures = new List<Fixture>();
        var dry = Cases(("a.hangs", "A"), ("b.next", "B"));
        var bRanOn = 0;
        var wedged = ServerTriage.Classify(new ServerVitals(true, null, 38, 410, 190, 0.4),
            [new Probe("health", false, 2000, "no answer in 2000 ms"), new Probe("app.info", false, 2000, "no answer in 2000 ms"),
             new Probe("sessions.list", false, 2000, "no answer in 2000 ms"), new Probe("ws app.info", false, 2000, "no answer in 2000 ms")]);
        var (report, output, outDir, _) = await Run(dry, async shard =>
        {
            await Task.Yield();
            var fx = new Fixture { Generation = fixtures.Count + 1 };
            lock (fixtures) fixtures.Add(fx);
            var r = new TestRunner();
            r.Add("a.hangs", "A", () => throw new AssertException("RPC projects.create timed out after 30000ms"));
            r.Add("b.next", "B", () => { bRanOn = fx.Generation; return Task.CompletedTask; });
            return new ShardContext
            {
                Runner = r, Close = () => { fx.Closed = true; return ValueTask.CompletedTask; },
                Triage = () => Task.FromResult(fx.Generation == 1 ? wedged : ServerTriage.Unknown),
            };
        });
        Check.Equal(2, fixtures.Count, "the shard started a second server after the failure");
        Check.True(fixtures[0].Closed, "...and stopped the first");
        Check.Equal(2, bRanOn, "B ran on the new one");
        Check.Equal(Outcome.Failed, report.Results[0].Outcome, "A failed");
        Check.Equal(Outcome.Passed, report.Results[1].Outcome, "B passed: it did not inherit A's wedge");
        var file = File.ReadAllText(report.Results[0].Artifact!);
        Check.Contains(file, "--- server state", "the failure file starts with the state of the server");
        Check.Contains(file, "wedged: the process is alive but does not even answer /api/health", "...and says it was wedged, not merely that an RPC timed out");
        Check.Contains(file, "probe app.info", "...with every probe");
        Check.Contains(output, "server: wedged", "the console says so as well");
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "report.json")))!;
        Check.Contains(json["tests"]![0]!["server"]!.GetValue<string>(), "wedged", "report.json carries the verdict");
    }

    private static Task Triage_Verdicts()
    {
        Probe Ok(string n, long ms = 3) => new(n, true, ms);
        Probe No(string n) => new(n, false, 2000, "no answer in 2000 ms");
        var alive = new ServerVitals(true, null, 40, 300, 200, 1);
        var spinning = new ServerVitals(true, null, 40, 300, 200, 95);

        var gone = ServerTriage.Classify(new ServerVitals(false, -1073741819), []);
        Check.False(gone.Healthy, "a dead server is not healthy");
        Check.Contains(gone.Verdict, "process is gone", "the process exited");
        Check.Contains(gone.Verdict, "-1073741819", "with its exit code");

        var frozen = ServerTriage.Classify(alive, [No("health"), No("app.info"), No("sessions.list"), No("ws app.info")]);
        Check.Contains(frozen.Verdict, "wedged", "nothing answers at all: the pool or the process");
        Check.Contains(frozen.Verdict, "idle", "idle CPU means blocked threads");
        Check.Contains(ServerTriage.Classify(spinning, [No("health"), No("app.info"), No("sessions.list")]).Verdict, "busy", "a busy CPU means a thread is working or spinning");
        Check.Contains(frozen.Verdict, "The thread pool has not started a queued work item", "and points at the watchdog's line in the server log");

        var rpc = ServerTriage.Classify(alive, [Ok("health"), No("app.info"), No("sessions.list")]);
        Check.Contains(rpc.Verdict, "RPC dispatch is stuck", "health works but a call that touches nothing does not");

        var db = ServerTriage.Classify(alive, [Ok("health"), Ok("app.info"), No("sessions.list"), Ok("ws app.info")]);
        Check.Contains(db.Verdict, "database path is blocked", "the one that reads the database is the one that hangs");
        Check.False(db.Healthy, "not healthy");

        var ws = ServerTriage.Classify(alive, [Ok("health"), Ok("app.info"), Ok("sessions.list"), No("ws app.info")]);
        Check.Contains(ws.Verdict, "WebSocket connection is dead", "only the test's own connection is dead");

        // The server was slow, not stuck: every probe answered, and an RPC that took 29 s is a slow RPC, not a wedged server.
        var slow = ServerTriage.Classify(alive, [Ok("health", 4), Ok("app.info", 6), Ok("sessions.list", 40), Ok("ws app.info", 7)]);
        Check.True(slow.Healthy, "answers to every probe: the server is fine");
        Check.Contains(slow.Verdict, "responsive", "and the verdict says the failure is the test's own");
        Check.Equal("responsive", slow.Summary, "one word for the console");
        Check.Contains(slow.Describe(), "probe sessions.list", "the file keeps every probe's timing");
        return Task.CompletedTask;
    }

    private static async Task Evidence_NeverEmpty()
    {
        // a runner with no diagnostics at all, and a test that prints nothing: the worst case for an empty file
        var dry = Cases(("a.bad", "A"));
        var (report, output, _, _) = await Run(dry, _ =>
        {
            var r = new TestRunner();
            r.Add("a.bad", "A", () => throw new AssertException("it failed"));
            return Task.FromResult(Shard(r));
        });
        var file = File.ReadAllText(report.Results[0].Artifact!);
        Check.Equal(0, Evidence.EmptySections(file).Count, "no section of the failure file is empty: " + string.Join(", ", Evidence.EmptySections(file)));
        Check.Contains(file, "(the test printed nothing)", "an empty test output says so");
        Check.NotContains(output, "evidence incomplete", "the runner did not have to complain");
        // and the check itself sees an empty section when there is one
        Check.Equal("test output, mock model server requests since the test started", string.Join(", ", Evidence.EmptySections(
            "head\n\n--- message\nboom\n\n--- test output\n\n--- mock model server requests since the test started (0, last 40)\n--- client events since the test started (1, last 40)\n#1 x\n")),
            "an empty section is found, with or without a blank line before the next");
    }

    private static async Task Settle_ReportsLeaks()
    {
        var fixtures = new List<Fixture>();
        var dry = Cases(("a.leaks", "A"), ("b.stuck", "B"), ("c.last", "C"));
        var (report, output, outDir, _) = await Run(dry, async shard =>
        {
            await Task.Yield();
            var fx = new Fixture { Generation = fixtures.Count + 1 };
            lock (fixtures) fixtures.Add(fx);
            var r = new TestRunner();
            r.Add("a.leaks", "A", () => Task.CompletedTask);
            r.Add("b.stuck", "B", () => Task.CompletedTask);
            r.Add("c.last", "C", () => Task.CompletedTask);
            var calls = 0;
            return new ShardContext
            {
                Runner = r, Close = () => { fx.Closed = true; return ValueTask.CompletedTask; },
                Settle = () => Task.FromResult(++calls switch
                {
                    1 when fx.Generation == 1 => new SettleResult("left 1 agent(s) running when it returned: main:running; they finished 700 ms later", true),
                    2 when fx.Generation == 1 => new SettleResult("left 1 agent(s) running when it returned: worker:running; still running 5000 ms later, and would not stop", false),
                    _ => SettleResult.Quiet,
                }),
            };
        });
        Check.True(report.Results.All(r => r.Outcome == Outcome.Passed), "the leaks do not fail the tests themselves");
        Check.Contains(report.Results[0].Leak ?? "", "main:running", "the first test's leak is recorded on it");
        Check.Contains(output, "LEAK: the test left 1 agent(s) running", "and printed under it");
        Check.Contains(output, "LEAK   a.leaks:", "and again in the summary");
        Check.Equal(2, fixtures.Count, "the server that could not be cleaned was replaced (after B), the one that was cleaned was kept (after A)");
        Check.Equal(null, report.Results[2].Leak, "a test that left nothing has no leak");
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "report.json")))!;
        Check.Contains(json["tests"]![1]!["leak"]!.GetValue<string>(), "would not stop", "report.json carries it");
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

    private static async Task RerunHint_FollowsTheCaller()
    {
        var dry = Cases(("a.bad", "bad"), ("b.bad", "bad too"));
        TestRunner Fails() { var r = new TestRunner(); r.Add("a.bad", "bad", () => throw new AssertException("x")); r.Add("b.bad", "bad too", () => throw new AssertException("y")); return r; }
        var (_, plain, _, _) = await Run(dry, _ => Task.FromResult(Shard(Fails())));
        Check.Contains(plain, "dotnet \"", "a direct run gets a dotnet command");
        Check.Contains(plain, "a.bad b.bad", "with the failing ids");
        Environment.SetEnvironmentVariable("NETPI_E2E_RERUN", "script -Only {ids} -SkipBuild");
        try
        {
            var (_, scripted, _, _) = await Run(dry, _ => Task.FromResult(Shard(Fails())));
            Check.Contains(scripted, "script -Only a.bad,b.bad -SkipBuild", "the caller's template, ids comma-separated");
            Check.NotContains(scripted, "dotnet \"", "and not the dotnet command");
        }
        finally { Environment.SetEnvironmentVariable("NETPI_E2E_RERUN", null); }
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
