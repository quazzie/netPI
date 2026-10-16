using NetPI.Aux.Tests;

// Usage: dotnet tests/NetPI.Aux.Tests/bin/Debug/NetPI.Aux.Tests.dll [name filter…]   (exit code 0 = all passed)
if (args.FirstOrDefault() == "--mcp-fixture") return await McpFixture.RunAsync(args.Skip(1).ToArray());
if (args.FirstOrDefault() == "--hook-echo") return WebhookHooksTests.Echo(args.Skip(1).ToArray());   // a command hook's test program: records its arguments and stdin
if (args.FirstOrDefault() == "--hook-sleep") { Thread.Sleep(60_000); return 0; }
if (args.FirstOrDefault() == "--hook-orphan") { WebhookHooksTests.Orphan(args[1]); return 0; }   // exits 0 and leaves a child holding stdout
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
IdeasNoticeTests.Register(runner);
TodoTests.Register(runner);
CallToolReadersTests.Register(runner);
WebTests.Register(runner);
BrowserTests.Register(runner);
WindowsTests.Register(runner);
MediaTests.Register(runner);
DecideTests.Register(runner);
EmbeddingsTests.Register(runner);
DecisionReworkTests.Register(runner);
LoopTests.Register(runner);
SshTests.Register(runner);
SchedulesTests.Register(runner);
SchedulesToolTests.Register(runner);
WebhooksTests.Register(runner);
WebhookHooksTests.Register(runner);
PlanStoreTests.Register(runner);
SkillsCacheTests.Register(runner);
PanelTests.Register(runner);
InspectTests.Register(runner);
RpcReadOnlyTests.Register(runner);
ReviewStorageTests.Register(runner);
var code = await runner.RunAsync(args);
// only this run's root, so a concurrent run of the same suite keeps its files
try { Directory.Delete(NetPI.Aux.Tests.T.TestRoot, recursive: true); } catch { }
return code;
