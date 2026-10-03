using NetPI.Tools.Tests;

// Usage: dotnet run --project tests/NetPI.Tools.Tests [-- <name filter>...]
var runner = new TestRunner { TimeoutSeconds = 90 };
WorkspaceToolTests.Register(runner);
FileTests.Register(runner);
ShellTests.Register(runner);
OpenTests.Register(runner);
LoadTests.Register(runner);
var code = await runner.RunAsync(args);
// only this run's root, so a concurrent run of the same suite keeps its files
try { Directory.Delete(NetPI.Tools.Tests.T.TestRoot, recursive: true); } catch { }
return code;
