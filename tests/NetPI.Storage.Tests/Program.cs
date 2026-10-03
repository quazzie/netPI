using NetPI.Storage.Tests;

// The storage conformance suite: the storage port in executable form. Every test here is a scenario the port's
// doc comments promise, and it runs against every provider registered in Providers.All — the memory provider
// today, the sqlite one next, and whatever engine someone writes later. Nothing in this project names SQL or an
// engine, because a provider that cannot pass these tests without special cases has found a difference the port
// has to settle.
//
//   dotnet build tests/NetPI.Storage.Tests
//   dotnet tests/NetPI.Storage.Tests/bin/Debug/NetPI.Storage.Tests.dll [name filter...]
var runner = new TestRunner { TimeoutSeconds = 120 };
SessionTests.Register(runner);
MessageTests.Register(runner);
CollectionTests.Register(runner);
StoreTests.Register(runner);
var code = await runner.RunAsync(args);
T.Cleanup();
return code;