using NetPI.Host.Tests;

// NetPI host kernel tests. Usage: NetPI.Host.Tests [name filter...]   (NETPI_TEST_LOGS=1 shows host logs)
var runner = new TestRunner();
SqliteTests.Register(runner);
SettingsTests.Register(runner);
EventBusTests.Register(runner);
RegistryTests.Register(runner);
SessionStoreTests.Register(runner);
ModelCatalogTests.Register(runner);
ServerTests.Register(runner);
PluginTests.Register(runner);
BackupTests.Register(runner);
BuildTests.Register(runner);
ReviewBackupTests.Register(runner);
var code = await runner.RunAsync(args);
T.Cleanup();
return code;
