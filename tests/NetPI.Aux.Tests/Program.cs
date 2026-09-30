using NetPI.Aux.Tests;

// Usage: dotnet tests/NetPI.Aux.Tests/bin/Debug/NetPI.Aux.Tests.dll [name filter…]   (exit code 0 = all passed)
if (args.FirstOrDefault() == "--mcp-fixture") return await McpFixture.RunAsync(args.Skip(1).ToArray());
var runner = new TestRunner();
McpTests.Register(runner);
McpHttpTests.Register(runner);
McpLifecycleTests.Register(runner);
RetryTests.Register(runner);
NudgeTests.Register(runner);
ToolRepairTests.Register(runner);
CompactionTests.Register(runner);
IdeasTests.Register(runner);
IdeasStorageTests.Register(runner);
IdeasMigrationTests.Register(runner);
IdeasCheckTests.Register(runner);
IdeasCommitTests.Register(runner);
IdeasNoticeTests.Register(runner);
TodoTests.Register(runner);
WebTests.Register(runner);
MediaTests.Register(runner);
DecideTests.Register(runner);
LoopTests.Register(runner);
SshTests.Register(runner);
PanelTests.Register(runner);
InspectTests.Register(runner);
RpcReadOnlyTests.Register(runner);
LoadTests.Register(runner);
ReviewStorageTests.Register(runner);
var code = await runner.RunAsync(args);
try { Directory.Delete(Path.Combine(Path.GetTempPath(), "netpi-aux-tests"), recursive: true); } catch { }
return code;
