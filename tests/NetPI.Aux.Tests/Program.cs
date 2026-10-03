using NetPI.Aux.Tests;

// Usage: dotnet tests/NetPI.Aux.Tests/bin/Debug/NetPI.Aux.Tests.dll [name filter…]   (exit code 0 = all passed)
if (args.FirstOrDefault() == "--mcp-fixture") return await McpFixture.RunAsync(args.Skip(1).ToArray());
var runner = new TestRunner { Name = "Aux" };
McpTests.Register(runner);
McpResourceTests.Register(runner);
McpHttpTests.Register(runner);
McpLifecycleTests.Register(runner);
RetryTests.Register(runner);
NudgeTests.Register(runner);
ToolRepairTests.Register(runner);
CompactionTests.Register(runner);
WorkspaceTests.Register(runner);
WorkspaceStoreTests.Register(runner);
IdeasTests.Register(runner);
IdeasStorageTests.Register(runner);
IdeasSnapshotTests.Register(runner);
IdeasCheckTests.Register(runner);
IdeasVerifyQueueTests.Register(runner);
IdeasCommitTests.Register(runner);
IdeasNoticeTests.Register(runner);
TodoTests.Register(runner);
WebTests.Register(runner);
BrowserTests.Register(runner);
WindowsTests.Register(runner);
MediaTests.Register(runner);
DecideTests.Register(runner);
DecisionReworkTests.Register(runner);
LoopTests.Register(runner);
SshTests.Register(runner);
PanelTests.Register(runner);
InspectTests.Register(runner);
RpcReadOnlyTests.Register(runner);
ReviewStorageTests.Register(runner);
var code = await runner.RunAsync(args);
// only this run's root, so a concurrent run of the same suite keeps its files
try { Directory.Delete(NetPI.Aux.Tests.T.TestRoot, recursive: true); } catch { }
return code;
