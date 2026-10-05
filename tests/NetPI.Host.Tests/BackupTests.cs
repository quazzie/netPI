using System.Diagnostics;
using System.Text.Json.Nodes;
using NetPI.Backup;
using NetPI.Host.Logging;
using NetPI.Host.Storage.Sqlite;
using NetPI.Host.Plugins;

namespace NetPI.Host.Tests;

public static class BackupTests
{
    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new AssertException("timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    public static void Register(TestRunner r)
    {
        BackupIdeasRoundTrip(r);
        BackupIdeaImages(r);

        r.Add("backup: the snapshot views are registered read-only; creating one is not", async () =>
        {
            // The read-only paths (scripts/netpi.mjs, the diag tool's rpc action) call only what rpc.list marks
            // readOnly, so an unmarked view is one no tool can reach (idea-o934y1).
            var home = T.TempDir("backup-rpc");
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            var scope = new PluginScope("netpi.backup", kernel.Log);
            var ctx = new PluginContext(kernel, "netpi.backup", home, scope, default, () => "test");
            kernel.Settings.Set("backup.enabled", JsonValue.Create(false));
            var plugin = new BackupPlugin();
            await plugin.StartAsync(ctx, default);
            try
            {
                var all = kernel.Rpc.List().Where(m => m.PluginId == "netpi.backup").ToArray();
                // Every method the plugin registers is listed here, split by the flag it has to carry.
                Check.Equal("backup.list backup.verify", string.Join(" ", all.Where(m => m.ReadOnly).Select(m => m.Method).Order(StringComparer.Ordinal)), "the methods that only read");
                Check.Equal("backup.create", string.Join(" ", all.Where(m => !m.ReadOnly).Select(m => m.Method).Order(StringComparer.Ordinal)), "the methods that change something");
            }
            finally { await plugin.StopAsync(default); scope.DisposeAll(); }
        });

        r.Add("backup: WAL snapshot, retention, corruption check and offline restore", async () =>
        {
            var home = T.TempDir("backup");
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            var scope = new PluginScope("netpi.backup", kernel.Log);
            var ctx = new PluginContext(kernel, "netpi.backup", home, scope, default, () => "test");
            var plugin = new BackupPlugin();
            kernel.Settings.Set("backup.enabled", JsonValue.Create(false));
            kernel.Settings.Set("backup.keepCount", JsonValue.Create(1));
            await plugin.StartAsync(ctx, default);
            try
            {
                var session = kernel.Sessions.CreateSession(new SessionInfo { Title = "keep me" });
                kernel.Sessions.AppendMessage(session.Id, ChatMessage.UserText("snapshot content"));
                var manual = await plugin.CreateAsync(ctx, false, default);
                var old = await plugin.CreateAsync(ctx, true, default);
                var newest = await plugin.CreateAsync(ctx, true, default);
                var damaged = Path.Combine(home, "backups", "damaged");
                Directory.CreateDirectory(damaged);
                File.WriteAllText(Path.Combine(damaged, "manifest.json"), "null");
                Check.Equal(2, BackupPlugin.List(home).Count, "manual plus latest automatic; damaged manifest ignored");
                Check.False(Directory.Exists(old["path"]!.GetValue<string>()));
                var id = manual["id"]!.GetValue<string>();
                BackupPlugin.Verify(home, id);
                var checkFile = Path.Combine(home, "check.db");
                File.Copy(Path.Combine(manual["path"]!.GetValue<string>(), "netpi.db"), checkFile);
                using (var snapshot = new Database(checkFile))
                {
                    Check.Equal("ok", snapshot.Scalar<string>("PRAGMA integrity_check"));
                    Check.Equal(1L, snapshot.Scalar<long>("SELECT COUNT(*) FROM messages WHERE session_id=@id", new { id = session.Id }));
                }
                // An actual offline restore, then a normal host startup on the restored database.
                var restored = Path.Combine(home, "restored");
                var repo = FindRepo();
                async Task<int> Restore(string destination)
                {
                    var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                    info.ArgumentList.Add(Path.Combine(repo, "scripts", "restore-backup.mjs"));
                    info.ArgumentList.Add(manual["path"]!.GetValue<string>()); info.ArgumentList.Add(destination);
                    using var process = Process.Start(info)!;
                    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0) Console.WriteLine("    " + await error);
                    await output;
                    return process.ExitCode;
                }
                Check.Equal(0, await Restore(restored));
                await using (var recovered = HostKernel.Create(new NetPiServerOptions { Home = restored, ConsoleLogging = false }))
                {
                    Check.Equal("keep me", recovered.Sessions.GetSession(session.Id)!.Title);
                    Check.Equal("snapshot content", recovered.Sessions.GetMessages(session.Id).Single().Text);
                    Check.Equal(false, recovered.Settings.Get("backup.enabled", true));
                }
                Check.Equal(1, await Restore(restored), "never overwrite an existing home");
                File.AppendAllText(Path.Combine(manual["path"]!.GetValue<string>(), "settings.json"), "corrupt");
                Check.Throws<InvalidDataException>(() => BackupPlugin.Verify(home, id));
                var badDestination = Path.Combine(home, "must-not-exist");
                Check.Equal(1, await Restore(badDestination));
                Check.False(Directory.Exists(badDestination), "verify before touching destination");
                Check.Throws<RpcException>(() => BackupPlugin.Verify(home, "../escape"));
            }
            finally { await plugin.StopAsync(default); scope.DisposeAll(); }
        });

        r.Add("backup: a failing automatic backup backs off (doubling, capped) and comes back at the regular cadence", async () =>
        {
            var home = T.TempDir("backup-backoff");
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            var scope = new PluginScope("netpi.backup", kernel.Log);
            var ctx = new PluginContext(kernel, "netpi.backup", home, scope, default, () => "test");
            kernel.Settings.Set("backup.enabled", JsonValue.Create(true));
            // `backups` as a file: every attempt dies at the first Directory.CreateDirectory
            File.WriteAllText(Path.Combine(home, "backups"), "not a directory");
            // compressed cadence: a 300 ms retry base instead of an hour, a 50 ms check instead of a minute
            var plugin = new BackupPlugin(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20));
            var backups = Path.Combine(home, "backups");
            await plugin.StartAsync(ctx, default);
            try
            {
                List<LogEntry> failures() => kernel.LogSink.Recent(200).Where(l => l.Category == "plugin:netpi.backup" && l.Message.StartsWith("Automatic backup failed")).ToList();
                await WaitFor(() => failures().Count >= 3, "three failed attempts with growing waits");
                var f = failures();
                Check.Contains(f[0].Message, "00:00:00.300", "the first failure waits the retry base: " + f[0].Message);
                Check.Contains(f[1].Message, "00:00:00.600", "the second doubles it: " + f[1].Message);
                Check.True(f[1].Time - f[0].Time >= TimeSpan.FromMilliseconds(250), "the loop really waited the base, not the check cadence: " + (f[1].Time - f[0].Time));
                Check.True(f[2].Time - f[1].Time > f[1].Time - f[0].Time, "the wait keeps doubling: " + (f[2].Time - f[1].Time) + " after " + (f[1].Time - f[0].Time));

                // The path is fixed: the in-flight attempt (after the accumulated backoff) succeeds. The plugin logs it
                // — its own clock, the one the failures are stamped with — and only then is it out of the folder, which
                // is what lets the test break it again without racing the backup that is still hashing what it moved.
                File.Delete(Path.Combine(home, "backups"));
                Directory.CreateDirectory(backups);
                List<LogEntry> successes() => kernel.LogSink.Recent(200).Where(l => l.Category == "plugin:netpi.backup" && l.Message.StartsWith("Automatic backup created")).ToList();
                await WaitFor(() => successes().Count >= 1, "the automatic backup to succeed once the path is fixed");
                var success = successes()[^1].Time;

                // broken again: the failure after a success waits the base, not the accumulated backoff
                Directory.Delete(backups, true);
                File.WriteAllText(backups, "not a directory");
                var before = failures().Count;
                await WaitFor(() => failures().Count > before, "the next failure after the success");
                var after = failures()[before];
                Check.Contains(after.Message, "00:00:00.300", "the wait is the retry base again: " + after.Message);
                Check.True(after.Time - success < TimeSpan.FromMilliseconds(900),
                    "the backoff was reset by the success (" + (after.Time - success) + " later, not the accumulated backoff)");
            }
            finally { await plugin.StopAsync(default); scope.DisposeAll(); }
        });

        r.Add("backup: a copy that does not fit on the disk is refused before it starts", () =>
        {
            var root = T.TempDir("backup-space");
            Check.Throws<IOException>(() => BackupPlugin.EnsureFreeSpace(long.MaxValue, root), "an absurd size cannot fit");
            BackupPlugin.EnsureFreeSpace(1024, root);   // a size the disk can hold is allowed
            BackupPlugin.EnsureFreeSpace(0, root);      // a store that reports no size reserves nothing
        });

        r.Add("backup: a .pending-* left by a kill mid-backup is swept, the snapshots are not", () =>
        {
            var home = T.TempDir("backup-sweep");
            var root = Path.Combine(home, "backups");
            Directory.CreateDirectory(Path.Combine(root, ".pending-20200101-000000-aaaaaaaa"));
            Directory.CreateDirectory(Path.Combine(root, ".pending-20200101-000001-bbbbbbbb"));
            Directory.CreateDirectory(Path.Combine(root, "20200101-000002-keep"));
            BackupPlugin.SweepPending(home);
            Check.False(Directory.Exists(Path.Combine(root, ".pending-20200101-000000-aaaaaaaa")), "the stale pending one is gone");
            Check.False(Directory.Exists(Path.Combine(root, ".pending-20200101-000001-bbbbbbbb")));
            Check.True(Directory.Exists(Path.Combine(root, "20200101-000002-keep")), "a real snapshot is untouched");
        });

        r.Add("backup: a snapshot that fails to verify is deleted, a verified one stays", () =>
        {
            var home = T.TempDir("backup-verify");
            var root = Path.Combine(home, "backups");
            // a bad snapshot: the manifest names a file that is not there
            var bad = Path.Combine(root, "bad");
            Directory.CreateDirectory(bad);
            File.WriteAllText(Path.Combine(bad, "manifest.json"), new JsonObject
            {
                ["version"] = 1, ["id"] = "bad", ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["automatic"] = true,
                ["provider"] = "sqlite", ["files"] = new JsonObject { ["netpi.db"] = new string('0', 64) },
            }.ToJsonString());
            Check.Throws<InvalidDataException>(() => BackupPlugin.FinalizeSnapshot(home, "bad"));
            Check.False(Directory.Exists(bad), "the failed snapshot is deleted: it must not count as the latest");

            // a good snapshot verifies and stays
            var good = Path.Combine(root, "good");
            Directory.CreateDirectory(good);
            File.WriteAllText(Path.Combine(good, "netpi.db"), "the store's file");
            File.WriteAllText(Path.Combine(good, "settings.json"), "{}");
            var files = new JsonObject();
            foreach (var name in new[] { "netpi.db", "settings.json" })
                files[name] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(good, name)))).ToLowerInvariant();
            File.WriteAllText(Path.Combine(good, "manifest.json"), new JsonObject
            {
                ["version"] = 1, ["id"] = "good", ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["automatic"] = true,
                ["provider"] = "sqlite", ["files"] = files,
            }.ToJsonString());
            BackupPlugin.FinalizeSnapshot(home, "good");
            Check.True(Directory.Exists(good), "a verified snapshot stays");
        });

        r.Add("backup: a long copy runs in batches: the store's lock is free between them, and the copy is still consistent", async () =>
        {
            var home = T.TempDir("backup-batch");
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            // grow the store so a copy takes a while: 100 MB of plugin data
            var items = kernel.Storage.Plugins.For("test.batch").Collection("big", new CollectionSpec().Text("k"));
            var chunk = new string('x', 1_000_000);
            for (var i = 0; i < 100; i++)
                items.Put("k" + i, new JsonObject { ["k"] = "k" + i, ["blob"] = chunk });

            var staging = T.TempDir("backup-batch-snapshot");
            // hold the store's lock and start the copy: it cannot take its first batch until we let it
            Monitor.Enter(kernel.Storage.Lock);
            var copy = Task.Run(() => kernel.Storage.Snapshot.Write(staging));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!File.Exists(Path.Combine(staging, "netpi.db")) && !copy.IsCompleted && DateTime.UtcNow < deadline)
                Thread.Sleep(1);   // the batched copy opens its destination before it needs the lock
            Monitor.Exit(kernel.Storage.Lock);
            if (copy.IsCompleted) throw new AssertException("the copy finished before it was seen running: the store is not big enough for the test");
            // while the copy is still running, the store's lock must come back free: a batch is being copied, not the whole store.
            // The probe checks in userland the way the copy's own loop re-acquires the lock; a waiter blocked in the kernel
            // would lose the race to that re-acquisition, so this is what the test can see.
            var holding = false;
            while (!holding && !copy.IsCompleted)
            {
                if (Monitor.TryEnter(kernel.Storage.Lock, 0)) { holding = true; break; }
                Thread.SpinWait(8);
            }
            var sawFreeWhileRunning = false;
            if (holding)
            {
                Thread.Sleep(50);   // the copy, if still running, is a batch away and must wait for us
                sawFreeWhileRunning = !copy.IsCompleted;
                Monitor.Exit(kernel.Storage.Lock);
            }
            Check.True(sawFreeWhileRunning, "the store's lock was seen free between batches, while the copy was still running (a single-statement copy holds it for the whole length)");
            await copy.WaitAsync(TimeSpan.FromSeconds(30));

            // and the copy is a consistent store with everything that was in before it started
            using var recovered = new SqliteStorageProvider().Open(new StorageOpenOptions
            {
                Home = staging, Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Settings = kernel.Settings,
            });
            Check.Equal(100L, recovered.Plugins.For("test.batch").Collection("big", new CollectionSpec().Text("k")).Count(),
                "every document written before the copy is in it");
        });
    }

    /// <summary>
    /// A plugin's data lives in the storage provider, so it travels inside the provider's consistent snapshot: restoring a snapshot into a
    /// fresh home and reading the collection back is what shows it came back. A snapshot whose manifest names no provider is refused: it
    /// was written by a build that stored things differently, and only that build restores it.
    /// </summary>
    private static void BackupIdeasRoundTrip(TestRunner r)
    {
        r.Add("backup: a plugin's collections are in the snapshot, the manifest names the provider, and they come back on restore", async () =>
        {
            var home = T.TempDir("backup-data");
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            var scope = new PluginScope("netpi.backup", kernel.Log);
            var ctx = new PluginContext(kernel, "netpi.backup", home, scope, default, () => "test");
            var plugin = new BackupPlugin();
            kernel.Settings.Set("backup.enabled", JsonValue.Create(false));

            // Data as a plugin keeps it: a collection of documents with a declared index field.
            var items = kernel.Storage.Plugins.For("test.keeper").Collection("items", new CollectionSpec().Text("title"));
            items.Put("idea-keep01", new JsonObject { ["id"] = "idea-keep01", ["title"] = "Keep me", ["custom"] = new JsonObject { ["kept"] = true } });

            await plugin.StartAsync(ctx, default);
            try
            {
                var manual = await plugin.CreateAsync(ctx, false, default);
                var dir = manual["path"]!.GetValue<string>();
                Check.Equal(2, manual["files"]!.AsObject().Count, "the provider's file and the settings");
                Check.Equal("sqlite", manual["provider"]!.GetValue<string>(), "the manifest names the provider that reads the files");
                BackupPlugin.Verify(home, manual["id"]!.GetValue<string>());

                // Restore into a new home and read the collection back with a store opened on it.
                var restored = Path.Combine(home, "restored-data");
                var repo = FindRepo();
                var exit = await RunRestoreAsync(repo, dir, restored);
                Check.Equal(0, exit, "the snapshot restores");
                using (var storage = new SqliteStorageProvider().Open(new StorageOpenOptions
                {
                    Home = restored, Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Settings = kernel.Settings,
                }))
                {
                    var back = storage.Plugins.For("test.keeper").Collection("items", new CollectionSpec().Text("title")).Get("idea-keep01");
                    Check.Equal("Keep me", back?["title"]?.GetValue<string>());
                    Check.True(back?["custom"]?["kept"]?.GetValue<bool>() == true, "a field the plugin stores comes back as it was");
                }

                // A snapshot whose manifest names no provider is refused, by Verify and by the restore script.
                var old = Path.Combine(home, "backups", "20200101-000000-old");
                Directory.CreateDirectory(old);
                File.Copy(Path.Combine(dir, "netpi.db"), Path.Combine(old, "netpi.db"));
                File.Copy(Path.Combine(dir, "settings.json"), Path.Combine(old, "settings.json"));
                var hashes = new JsonObject();
                foreach (var name in new[] { "netpi.db", "settings.json" })
                    hashes[name] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(old, name)))).ToLowerInvariant();
                File.WriteAllText(Path.Combine(old, "manifest.json"), new JsonObject
                {
                    ["version"] = 1, ["id"] = "20200101-000000-old", ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["automatic"] = true, ["files"] = hashes,
                }.ToJsonString());
                Check.Throws<InvalidDataException>(() => BackupPlugin.Verify(home, "20200101-000000-old"), "no provider in the manifest");
                Check.True(await RunRestoreAsync(repo, old, Path.Combine(home, "restored-old")) != 0, "the restore script refuses it too");
            }
            finally { await plugin.StopAsync(default); scope.DisposeAll(); }
        });

        r.Add("backup: retention lets go of the idea images a snapshot carried, once the home has none", async () =>
        {
            var home = T.TempDir("backup-images");
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            var scope = new PluginScope("netpi.backup", kernel.Log);
            var ctx = new PluginContext(kernel, "netpi.backup", home, scope, default, () => "test");
            var plugin = new BackupPlugin();
            kernel.Settings.Set("backup.keepCount", JsonValue.Create(1));
            await plugin.StartAsync(ctx, default);
            try
            {
                // A snapshot taken while the home holds an idea image: the image travels in it as a file of its own.
                var images = Path.Combine(home, "idea-images");
                Directory.CreateDirectory(images);
                File.WriteAllBytes(Path.Combine(images, "pic.png"), [1, 2, 3]);
                var withImage = await plugin.CreateAsync(ctx, true, default);
                Check.True(withImage["files"]!.AsObject().ContainsKey("idea-images.pic.png"), "the snapshot carries the image");

                // The picture is deleted and a newer snapshot taken: the old one is past the count, and its one extra
                // file is an image the home no longer has — exactly what retention lets go of, not a reason to keep it.
                File.Delete(Path.Combine(images, "pic.png"));
                var after = await plugin.CreateAsync(ctx, true, default);
                Check.False(Directory.Exists(withImage["path"]!.GetValue<string>()),
                    "the image the home no longer has does not keep its snapshot");
                Check.True(Directory.Exists(after["path"]!.GetValue<string>()), "and the newer one stays");
            }
            finally { await plugin.StopAsync(default); scope.DisposeAll(); }
        });

        r.Add("backup: a snapshot holding a name that is not a file is kept whole, and the backup that meets it completes", async () =>
        {
            var home = T.TempDir("backup-guard");
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            var scope = new PluginScope("netpi.backup", kernel.Log);
            var ctx = new PluginContext(kernel, "netpi.backup", home, scope, default, () => "test");
            var plugin = new BackupPlugin();
            kernel.Settings.Set("backup.keepCount", JsonValue.Create(1));
            await plugin.StartAsync(ctx, default);
            try
            {
                var first = await plugin.CreateAsync(ctx, true, default);
                var path = first["path"]!.GetValue<string>();
                File.Delete(Path.Combine(path, "netpi.db"));
                Directory.CreateDirectory(Path.Combine(path, "netpi.db"));   // the store's own name, as a directory
                var second = await plugin.CreateAsync(ctx, true, default);
                Check.True(Directory.Exists(second["path"]!.GetValue<string>()), "the new snapshot lands");
                Check.True(Directory.Exists(Path.Combine(path, "netpi.db")),
                    "the old snapshot is kept whole (a half-deletion is not a deletion), not removed");
            }
            finally { await plugin.StopAsync(default); scope.DisposeAll(); }
        });

        r.Add("backup: retention keeps the newest of the automatic snapshots, the oldest goes first", async () =>
        {
            var home = T.TempDir("backup-order");
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            var scope = new PluginScope("netpi.backup", kernel.Log);
            var ctx = new PluginContext(kernel, "netpi.backup", home, scope, default, () => "test");
            var plugin = new BackupPlugin();
            kernel.Settings.Set("backup.keepCount", JsonValue.Create(2));
            await plugin.StartAsync(ctx, default);
            try
            {
                var a = await plugin.CreateAsync(ctx, true, default);
                var b = await plugin.CreateAsync(ctx, true, default);
                var c = await plugin.CreateAsync(ctx, true, default);
                Check.False(Directory.Exists(a["path"]!.GetValue<string>()), "the oldest is past the count");
                Check.True(Directory.Exists(b["path"]!.GetValue<string>()), "the middle one stays");
                Check.True(Directory.Exists(c["path"]!.GetValue<string>()), "and the newest one");
                Check.Equal(2, BackupPlugin.List(home).Count(), "two snapshots left");
            }
            finally { await plugin.StopAsync(default); scope.DisposeAll(); }
        });

        r.Add("backup: two backups at once both complete: the gate serializes them, and neither snapshot is damaged", async () =>
        {
            var home = T.TempDir("backup-concurrent");
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            var scope = new PluginScope("netpi.backup", kernel.Log);
            var ctx = new PluginContext(kernel, "netpi.backup", home, scope, default, () => "test");
            var plugin = new BackupPlugin();
            kernel.Settings.Set("backup.keepCount", JsonValue.Create(2));
            await plugin.StartAsync(ctx, default);
            try
            {
                var first = Task.Run(() => plugin.CreateAsync(ctx, true, default));
                var second = Task.Run(() => plugin.CreateAsync(ctx, true, default));
                var both = await Task.WhenAll(first, second);
                Check.True(both[0]["id"]!.GetValue<string>() != both[1]["id"]!.GetValue<string>(), "two distinct snapshots");
                foreach (var snapshot in both)
                {
                    BackupPlugin.Verify(home, snapshot["id"]!.GetValue<string>());
                    Check.True(Directory.Exists(snapshot["path"]!.GetValue<string>()), "it is still there");
                }
            }
            finally { await plugin.StopAsync(default); scope.DisposeAll(); }
        });

        r.Add("backup: the restore checks the folder against its manifest before it writes anything", async () =>
        {
            var repo = FindRepo();
            var home = T.TempDir("restore-folder");

            // A snapshot made by hand: the manifest names exactly the files in the folder, with their checksums.
            string Make(string folder, string manifestId, string? extraFile = null)
            {
                var path = Path.Combine(home, folder);
                Directory.CreateDirectory(path);
                var files = new JsonObject();
                foreach (var (name, content) in new[] { ("netpi.db", "the store"), ("settings.json", "{}") })
                {
                    File.WriteAllText(Path.Combine(path, name), content);
                    files[name] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
                }
                if (extraFile is not null) File.WriteAllText(Path.Combine(path, extraFile), "not in the manifest");
                File.WriteAllText(Path.Combine(path, "manifest.json"), new JsonObject
                {
                    ["version"] = 1, ["id"] = manifestId, ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["automatic"] = true,
                    ["provider"] = "memory", ["files"] = files,
                }.ToJsonString());
                return path;
            }
            async Task<(int Exit, string Error)> Restore(string snapshot, string destination)
            {
                var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add(Path.Combine(repo, "scripts", "restore-backup.mjs"));
                info.ArgumentList.Add(snapshot);
                info.ArgumentList.Add(destination);
                using var process = Process.Start(info)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var stderr = await error;
                await output;
                return (process.ExitCode, stderr.Trim());
            }

            // The complete snapshot: the manifest is the folder's own, and it names every file in it. It restores.
            var ok = Make("ok", "ok");
            var (exit0, _) = await Restore(ok, Path.Combine(home, "restored-ok"));
            Check.Equal(0, exit0, "a snapshot that is what its manifest says restores");
            Check.True(File.Exists(Path.Combine(home, "restored-ok", "netpi.db")), "with its files");

            // A folder that is not the one its manifest describes: a mix-up is refused before anything is written.
            var mixed = Make("mixed", "another");
            var (exit1, err1) = await Restore(mixed, Path.Combine(home, "restored-mixed"));
            Check.Equal(1, exit1, "a manifest that is not the folder's own is refused: " + err1);
            Check.Contains(err1, "does not match", err1);
            Check.False(Directory.Exists(Path.Combine(home, "restored-mixed")), "and nothing was written");

            // A file in the folder that the manifest does not name: not part of the snapshot, and the restore says so.
            var stray = Make("stray", "stray", extraFile: "evil.bin");
            var (exit2, err2) = await Restore(stray, Path.Combine(home, "restored-stray"));
            Check.Equal(1, exit2, "a file nobody names is refused: " + err2);
            Check.Contains(err2, "evil.bin", err2);
            Check.False(Directory.Exists(Path.Combine(home, "restored-stray")), "and nothing was written");
        });
    }

    /// <summary>
    /// The idea images are files under the home, not rows in the store, so the snapshot copies them itself: each one into a
    /// file of its own (with a checksum, like every other file) and the manifest says which file of the images directory it
    /// is. Restoring puts them back where the ideas that reference them look for them (idea-3m2h1g).
    /// </summary>
    private static void BackupIdeaImages(TestRunner r)
    {
        r.Add("backup: the idea images travel in the snapshot and come back on restore", async () =>
        {
            var home = T.TempDir("backup-images");
            var shot = new byte[] { 0x89, 0x50, 0x4e, 0x47, 1, 2, 3 };
            Directory.CreateDirectory(Path.Combine(home, "idea-images"));
            File.WriteAllBytes(Path.Combine(home, "idea-images", "img-abc12345.png"), shot);
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            var scope = new PluginScope("netpi.backup", kernel.Log);
            var ctx = new PluginContext(kernel, "netpi.backup", home, scope, default, () => "test");
            var plugin = new BackupPlugin();
            kernel.Settings.Set("backup.enabled", JsonValue.Create(false));
            await plugin.StartAsync(ctx, default);
            try
            {
                var manual = await plugin.CreateAsync(ctx, false, default);
                var dir = manual["path"]!.GetValue<string>();
                Check.Equal("idea-images/img-abc12345.png", manual["ideaImages"]!.AsObject()["idea-images.img-abc12345.png"]!.GetValue<string>(),
                    "the manifest says which file of the images directory the copy is");
                Check.True(manual["files"]!.AsObject().ContainsKey("idea-images.img-abc12345.png"), "and the copy is a checksummed file of the snapshot");
                BackupPlugin.Verify(home, manual["id"]!.GetValue<string>());

                var repo = FindRepo();
                var restored = Path.Combine(home, "restored-images");
                Check.Equal(0, await RunRestoreAsync(repo, dir, restored), "the snapshot restores");
                var back = Path.Combine(restored, "idea-images", "img-abc12345.png");
                Check.True(File.Exists(back) && shot.SequenceEqual(File.ReadAllBytes(back)), "the picture comes back where the idea that references it looks for it");

                // A manifest is a file anything can write, so where it says an image belongs is checked before a restore
                // builds a path from it: a target that climbs out of the images directory is refused, and refused before
                // any destination is touched.
                var tamperedId = "20200101-000000-tampered";
                var tampered = Path.Combine(home, "backups", tamperedId);
                T.CopyDir(dir, tampered);
                var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(tampered, "manifest.json")))!.AsObject();
                manifest["ideaImages"]!.AsObject()["idea-images.img-abc12345.png"] = "idea-images/../../escape.png";
                File.WriteAllText(Path.Combine(tampered, "manifest.json"), manifest.ToJsonString());
                Check.Throws<InvalidDataException>(() => BackupPlugin.Verify(home, tamperedId), "Verify refuses a target that climbs out of the images directory");
                var refused = Path.Combine(home, "restored-tampered");
                Check.True(await RunRestoreAsync(repo, tampered, refused) != 0, "the restore script refuses it too");
                Check.False(Directory.Exists(refused), "verify before touching destination");
            }
            finally { await plugin.StopAsync(default); scope.DisposeAll(); }
        });
    }

    private static async Task<int> RunRestoreAsync(string repo, string snapshot, string destination)
    {
        var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(Path.Combine(repo, "scripts", "restore-backup.mjs"));
        info.ArgumentList.Add(snapshot);
        info.ArgumentList.Add(destination);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output; await error;
        return process.ExitCode;
    }

    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NetPI.slnx"))) return dir.FullName;
        throw new Exception("Repository not found");
    }
}
