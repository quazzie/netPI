using NetPI.Tools.Tests;

// Usage: dotnet run --project tests/NetPI.Tools.Tests [-- <name filter>...]
var runner = new TestRunner();
WorkspaceToolTests.Register(runner);
FileTests.Register(runner);
ShellTests.Register(runner);
OpenTests.Register(runner);
LoadTests.Register(runner);
var code = await runner.RunAsync(args);
try { Directory.Delete(Path.Combine(Path.GetTempPath(), "netpi-tests"), recursive: true); } catch { }
return code;
