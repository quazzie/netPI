using System.Globalization;
using NetPI.E2E;

// NetPI end-to-end suite. Usage:
//   dotnet tests/NetPI.E2E/bin/Debug/NetPI.E2E.dll [options] [name filter...]
//     --port N        netpi-server port (default 7470)
//     --mock-port N   MockLlm port (default 7471)
//     --app DIR       run this app folder instead of a fresh copy of artifacts/app
//     --speed X       mock stream speed factor (default 1)
//     --no-ui         skip the Playwright UI smoke test
//     --keep          keep the temp folder (server.log, home, projects)
//     --verbose       echo server log lines and mock requests
//     --list          list the tests and exit
var o = new E2EOptions();
var list = false;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--mock-port": o.MockPort = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--app": o.AppDir = Next(); break;
        case "--speed": o.MockSpeed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--no-ui": o.Ui = false; break;
        case "--keep": o.Keep = true; break;
        case "--verbose" or "-v": o.Verbose = true; break;
        case "--list": list = true; break;
        default:
            if (args[i].StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine($"Unknown option {args[i]}"); return 2; }
            o.Filters.Add(args[i]);
            break;
    }
}

Env? env = null;
var runner = new TestRunner();
void RegisterAll(Env e)
{
    CoreTests.Register(runner, e);
    ControlTests.Register(runner, e);
    HookTests.Register(runner, e);
    SchedulerTests.Register(runner, e);
    AdvancedTests.Register(runner, e);
    RealismTests.Register(runner, e);
    ReloadTests.Register(runner, e);
    if (o.Ui) UiTests.Register(runner, e);
    ShutdownTests.Register(runner, e);
}

if (list)
{
    RegisterAll(null!);
    foreach (var n in runner.Names) Console.WriteLine(n);
    return 0;
}

try
{
    Console.WriteLine("NetPI E2E: starting MockLlm and netpi-server…");
    env = await Env.StartAsync(o);
    RegisterAll(env);
    var code = await runner.RunAsync(o.Filters);
    if (code != 0) Console.WriteLine($"Server log: {env.ServerLog} (use --keep to keep it)");
    return code;
}
catch (Exception ex)
{
    Console.Error.WriteLine("E2E setup failed: " + ex);
    if (env is not null && File.Exists(env.ServerLog)) Console.Error.WriteLine(string.Join("\n", File.ReadLines(env.ServerLog).TakeLast(40)));
    return 1;
}
finally
{
    if (env is not null) await env.DisposeAsync();
}
