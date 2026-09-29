using NetPI.Aux.Tests;

// Usage: dotnet tests/NetPI.Aux.Tests/bin/Debug/NetPI.Aux.Tests.dll [name filter…]   (exit code 0 = all passed)
var runner = new TestRunner();
RetryTests.Register(runner);
NudgeTests.Register(runner);
ToolRepairTests.Register(runner);
CompactionTests.Register(runner);
IdeasTests.Register(runner);
IdeasStorageTests.Register(runner);
TodoTests.Register(runner);
WebTests.Register(runner);
MediaTests.Register(runner);
DecideTests.Register(runner);
LoopTests.Register(runner);
SshTests.Register(runner);
PanelTests.Register(runner);
InspectTests.Register(runner);
LoadTests.Register(runner);
var code = await runner.RunAsync(args);
try { Directory.Delete(Path.Combine(Path.GetTempPath(), "netpi-aux-tests"), recursive: true); } catch { }
return code;
