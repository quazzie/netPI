using NetPI.Tools.Tests;

// Usage: dotnet run --project tests/NetPI.Tools.Tests [-- <name filter>...]
// the live language-server and embedding tests download and load a project / a model: they get longer when switched on
var runner = new TestRunner { Name = "Tools", TimeoutSeconds = Environment.GetEnvironmentVariable("NETPI_LSP_LIVE") == "1" || Environment.GetEnvironmentVariable("NETPI_CODESEARCH_LIVE") == "1" ? 900 : 90 };
ToolArgsTests.Register(runner);
WorkspaceToolTests.Register(runner);
FileTests.Register(runner);
ShellTests.Register(runner);
OpenTests.Register(runner);
LoadTests.Register(runner);
LspTests.Register(runner);
TestToolTests.Register(runner);
CodeSearchTests.Register(runner);
var code = await runner.RunAsync(args);
// only this run's root, so a concurrent run of the same suite keeps its files
try { Directory.Delete(NetPI.Tools.Tests.T.TestRoot, recursive: true); } catch { }
return code;
