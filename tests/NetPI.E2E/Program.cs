using System.Globalization;
using NetPI.E2E;

// NetPI end-to-end suite. Usage:
//   dotnet tests/NetPI.E2E/bin/Release/NetPI.E2E.dll [options] [test id | name filter]...
//
//   selection (decided before anything starts; a filter or tag that matches nothing is an error, exit 2)
//     <filter>        an exact test id, else a substring of a test's id or name; several are OR-ed. None = every test
//     --tag a,b       add every test with one of these tags (an area such as `retry`, or `smoke`, `lifecycle`, `ui`…)
//     --smoke         shorthand for --tag smoke: one representative test per boundary
//     --skip-tag a,b  leave out tests with these tags
//     --no-ui         shorthand for --skip-tag ui
//     --list          print the selected tests (id, tags, last duration, name) and exit; starts nothing
//   running
//     --parallel N    shards (a server + mock of their own) running tests at once; `auto` (default) = as many as the
//                     selection is worth (about one per 12 s of work, at most 4)
//     --repeat N      run every selected test N times (how often does the flaky one fail?)
//     --fresh         a fresh server for every test, so no test can see what another left behind
//     --timeout-scale X   multiply every test's timeout (a busy machine)
//     --speed X       mock stream speed factor (default 1)
//     --app DIR       run this app folder instead of a fresh copy of the build output (artifacts/dev/app)
//     --port N / --mock-port N   fixed ports (shard i uses N+i); default: free ports
//   results
//     --out DIR       this run's folder: report.json, failures/<id>.txt, screenshots/ (default artifacts/e2elogs/<run>)
//     --state-dir DIR where the failing list and the timing history persist (default artifacts/e2elogs); --no-state = nowhere
//     --failed        run the tests that failed and have not passed since (across runs)
//     --keep          keep each server's work folder (server.log, home, projects, app copy)
//     --verbose       echo server log lines and mock requests
//     --self-test     check the runner itself (selection, scheduling, timeout containment, reports); starts no server
//
// Exit codes: 0 all passed, 1 a test failed or timed out, 2 bad command line / empty selection, 3 a server could not be
// started (the rest of that shard is reported as not run), 4 the runner itself failed.
if (args.FirstOrDefault() == "--mcp-fixture") return await NetPI.Aux.Tests.McpFixture.RunAsync(args.Skip(1).ToArray());

var o = new E2EOptions();
var list = false;
var selfTest = false;
var failedOnly = false;
var noState = false;
string? stateDir = null, outDir = null;
try
{
    for (var i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        List<string> Csv() => Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        switch (args[i])
        {
            case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--mock-port": o.MockPort = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--app": o.AppDir = Next(); break;
            case "--speed": o.MockSpeed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--no-ui": o.SkipTags.Add("ui"); break;
            case "--keep": o.Keep = true; break;
            case "--verbose" or "-v": o.Verbose = true; break;
            case "--list": list = true; break;
            case "--tag": o.Tags.AddRange(Csv()); break;
            case "--skip-tag": o.SkipTags.AddRange(Csv()); break;
            case "--smoke": o.Tags.Add("smoke"); break;
            case "--parallel": { var v = Next(); o.Parallel = v == "auto" ? 0 : int.Parse(v, CultureInfo.InvariantCulture); break; }
            case "--repeat": o.Repeat = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
            case "--fresh": o.Fresh = true; break;
            case "--timeout-scale": o.TimeoutScale = double.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--out": outDir = Next(); break;
            case "--state-dir": stateDir = Next(); break;
            case "--no-state": noState = true; break;
            case "--failed": failedOnly = true; break;
            case "--self-test": selfTest = true; break;
            default:
                if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option {args[i]}");
                o.Filters.Add(args[i]);
                break;
        }
    }
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

RoutedConsole.Install();
if (selfTest) return await RunnerSelfTests.RunAsync();

var repo = Env.FindRepoRoot();
o.StateDir = noState ? "" : Path.GetFullPath(stateDir ?? Path.Combine(repo, "artifacts", "e2elogs"));
o.OutDir = Path.GetFullPath(outDir ?? Path.Combine(o.StateDir.Length > 0 ? o.StateDir : Path.Combine(Path.GetTempPath(), "netpi-e2e-out"),
    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4]));

var catalog = Suite.RegisterAll(null).Cases;
if (failedOnly)
{
    var failing = FailingState.Load(o.StateDir).Keys.Where(id => catalog.Any(c => c.Id == id)).ToList();
    if (failing.Count == 0)
    {
        Console.WriteLine(o.StateDir.Length == 0 ? "--failed needs the state dir (drop --no-state)." : "No test is failing: nothing failed in the last runs, or it passed since.");
        return o.StateDir.Length == 0 ? 2 : 0;
    }
    o.Filters.AddRange(failing);
}

var selection = Selection.Resolve(catalog, o.Filters, o.Tags, o.SkipTags);
if (selection.Errors.Count > 0)
{
    foreach (var e in selection.Errors) Console.Error.WriteLine("error: " + e);
    Console.Error.WriteLine("`--list` shows every test id and tag.");
    return 2;
}

if (list)
{
    var timings = Timings.Load(o.StateDir);
    foreach (var c in selection.Selected)
        Console.WriteLine($"{c.Id,-36} {string.Join(",", c.Tags.Skip(1)),-22} {(timings.TryGetValue(c.Id, out var ms) ? ms / 1000 : Catalog.DefaultEstimateMs(c) / 1000.0),5:0.0}s  {c.Name}");
    Console.WriteLine($"{selection.Selected.Count} of {catalog.Count} tests");
    return 0;
}

try
{
    var orchestrator = new Orchestrator
    {
        Options = o,
        CommandLine = args,
        StartShard = async shard =>
        {
            var env = await Env.StartAsync(o.ForServer(o.Port == 0 ? 0 : o.Port + shard, o.MockPort == 0 ? 0 : o.MockPort + shard));
            return new ShardContext
            {
                Runner = Suite.RegisterAll(env),
                Mark = env.MarkAsync,
                Diagnose = env.DiagnoseAsync,
                Close = env.DisposeAsync,
                Setup = env.Setup,
            };
        },
    };
    return (await orchestrator.RunAsync(selection.Selected)).ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine("E2E runner failed: " + ex);
    return 4;
}
