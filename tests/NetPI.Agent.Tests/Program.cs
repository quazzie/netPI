using NetPI.Agent.Tests;

// Usage: dotnet tests/NetPI.Agent.Tests/bin/Debug/NetPI.Agent.Tests.dll [-v] [name filter...]   (exit code 0 = all passed)
TestHost.Verbose = args.Contains("-v");
var filters = args.Where(a => a != "-v").ToArray();
var runner = new TestRunner();
LoopTests.Register(runner);
SubagentTests.Register(runner);
LaneTests.Register(runner);
ContextTests.Register(runner);
PersistenceTests.Register(runner);
UnloadTests.Register(runner);
GoalTests.Register(runner);
BudgetTests.Register(runner);
SessionToolsTests.Register(runner);
var code = await runner.RunAsync(filters);
try { Directory.Delete(Path.Combine(Path.GetTempPath(), "netpi-agent-tests"), recursive: true); } catch { }
return code;
