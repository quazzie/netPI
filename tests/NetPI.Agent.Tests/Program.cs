using NetPI.Agent.Tests;

// Usage: dotnet tests/NetPI.Agent.Tests/bin/Debug/NetPI.Agent.Tests.dll [-v] [name filter...]   (exit code 0 = all passed)
if (args.FirstOrDefault() == "--mcp-fixture") return await NetPI.Aux.Tests.McpFixture.RunAsync(args.Skip(1).ToArray());
TestHost.Verbose = args.Contains("-v");
var filters = args.Where(a => a != "-v").ToArray();
var runner = new TestRunner();
DeferredToolsTests.Register(runner);
LoopTests.Register(runner);
SubagentTests.Register(runner);
SchedulerTests.Register(runner);
ContextTests.Register(runner);
PersistenceTests.Register(runner);
UnloadTests.Register(runner);
GoalTests.Register(runner);
BudgetTests.Register(runner);
ReservationTests.Register(runner);
SessionToolsTests.Register(runner);
ProfilesTests.Register(runner);
SkillsTests.Register(runner);
AskTests.Register(runner);
GuardrailsTests.Register(runner);
LoopsTests.Register(runner);
var code = await runner.RunAsync(filters);
try { Directory.Delete(Path.Combine(Path.GetTempPath(), "netpi-agent-tests"), recursive: true); } catch { }
return code;
