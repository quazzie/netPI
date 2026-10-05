using NetPI.Agent.Tests;

// Usage: dotnet tests/NetPI.Agent.Tests/bin/Debug/NetPI.Agent.Tests.dll [-v] [name filter...]   (exit code 0 = all passed)
if (args.FirstOrDefault() == "--mcp-fixture") return await NetPI.Aux.Tests.McpFixture.RunAsync(args.Skip(1).ToArray());
TestHost.Verbose = args.Contains("-v");
var filters = args.Where(a => a != "-v").ToArray();
var runner = new TestRunner { Name = "Agent", RunOnThreadPool = true };
DeferredToolsTests.Register(runner);
LoopTests.Register(runner);
SubagentTests.Register(runner);
SubagentWorkspaceTests.Register(runner);
SchedulerTests.Register(runner);
ResourceSchedulerTests.Register(runner);
AgentReworkTests.Register(runner);
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
PlanTests.Register(runner);
GuardrailsTests.Register(runner);
LoopsTests.Register(runner);
RpcReadOnlyTests.Register(runner);
CoordinatorTests.Register(runner);
var code = await runner.RunAsync(filters);
// No blanket delete of the shared root: each TestHost removes its own directory when it is disposed,
// and a shared delete would take a concurrent run's files with it.
return code;
