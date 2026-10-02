using NetPI.Host.Tests;

// NetPI host kernel tests. Usage: NetPI.Host.Tests [name filter...]   (NETPI_TEST_LOGS=1 shows host logs)
if (args.FirstOrDefault() == "--mcp-fixture") return await NetPI.Aux.Tests.McpFixture.RunAsync(args.Skip(1).ToArray());
var runner = new TestRunner();
McpLifecycleTests.Register(runner);
SqliteTests.Register(runner);
SettingsTests.Register(runner);
EventBusTests.Register(runner);
LogSinkTests.Register(runner);
RegistryTests.Register(runner);
CoreRpcTests.Register(runner);
SessionStoreTests.Register(runner);
ContextCacheTests.Register(runner);
ModelCatalogTests.Register(runner);
ServerTests.Register(runner);
PluginTests.Register(runner);
PluginIndependenceTests.Register(runner);
BackupTests.Register(runner);
BuildTests.Register(runner);
RunnerTests.Register(runner);
SessionStateTests.Register(runner);
ReviewBackupTests.Register(runner);
var code = await runner.RunAsync(args);
T.Cleanup();
return code;
