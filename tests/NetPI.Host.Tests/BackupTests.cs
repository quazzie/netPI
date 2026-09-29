using System.Diagnostics;
using System.Text.Json.Nodes;
using NetPI.Backup;
using NetPI.Host.Data;
using NetPI.Host.Plugins;

namespace NetPI.Host.Tests;

public static class BackupTests
{
    public static void Register(TestRunner r)
    {
        BackupIdeasRoundTrip(r);

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
    }

    /// <summary>
    /// The ideas backlog is in the home as plain files, so a snapshot has to carry them: the backlog (under the name
    /// the user configured), the cards waiting to be answered, and the import receipt. Restoring them into a fresh home
    /// and starting the host on it is what shows that they came back (docs/plans/2026-09-29-ideas-flow-storage-handoff.md,
    /// assignment D).
    /// </summary>
    private static async Task BackupIdeasRoundTrip(TestRunner r)
    {
        r.Add("backup: the ideas backlog, its pending cards and its receipt are in the snapshot and come back", async () =>
        {
            var home = T.TempDir("backup-ideas");
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            var scope = new PluginScope("netpi.backup", kernel.Log);
            var ctx = new PluginContext(kernel, "netpi.backup", home, scope, default, () => "test");
            var plugin = new BackupPlugin();
            kernel.Settings.Set("backup.enabled", JsonValue.Create(false));
            kernel.Settings.Set("ideas.fileName", JsonValue.Create("backlog.json")); // a named file, not the default
            var backlog = "{ \"version\": 1, \"ideas\": [ { \"id\": \"idea-keep01\", \"title\": \"Keep me\" } ] }\n";
            var pending = "{ \"suggestions\": [ { \"id\": \"sg_keep001\", \"kind\": \"save\", \"title\": \"Waiting\" } ], \"ops\": [], \"checked\": {}, \"repos\": {} }";
            await File.WriteAllTextAsync(Path.Combine(home, "backlog.json"), backlog);
            await File.WriteAllTextAsync(Path.Combine(home, "ideas-pending.json"), pending);
            await File.WriteAllTextAsync(Path.Combine(home, "ideas-migration.json"), "{ \"version\": 1, \"imports\": [] }");

            await plugin.StartAsync(ctx, default);
            try
            {
                var manual = await plugin.CreateAsync(ctx, false, default);
                var dir = manual["path"]!.GetValue<string>();
                Check.Equal(5, manual["files"]!.AsObject().Count, "database, settings and the three ideas files");
                Check.Equal(File.ReadAllText(Path.Combine(home, "backlog.json")), File.ReadAllText(Path.Combine(dir, "backlog.json")));
                Check.Equal(pending.Replace(" ", ""), File.ReadAllText(Path.Combine(dir, "ideas-pending.json")).Replace(" ", ""), "the cards waiting for an answer");
                BackupPlugin.Verify(home, manual["id"]!.GetValue<string>()); // every file, checksums included

                // The snapshot a busy ideas plugin refused: no torn files, and the log says so.
                await File.WriteAllTextAsync(Path.Combine(home, "ideas-pending.json"), pending.Replace("Waiting", "Waiting again"));
                using (var held = new FileStream(Path.Combine(home, ".backlog.json.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    var busy = await plugin.CreateAsync(ctx, false, default);
                    Check.Equal(2, busy["files"]!.AsObject().Count, "the ideas files were left out rather than copied half");
                    Check.False(File.Exists(Path.Combine(busy["path"]!.GetValue<string>(), "ideas-pending.json")), "and no half copy of the cards");
                }

                // Restore into a new home and start a host on it: the ideas and the card are there.
                var restored = Path.Combine(home, "restored-ideas");
                var repo = FindRepo();
                var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add(Path.Combine(repo, "scripts", "restore-backup.mjs"));
                info.ArgumentList.Add(dir);
                info.ArgumentList.Add(restored);
                using var process = Process.Start(info)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) Console.WriteLine("    " + await error);
                await output;
                Check.Equal(0, process.ExitCode, "the snapshot restores");
                Check.Equal(backlog, File.ReadAllText(Path.Combine(restored, "backlog.json")));
                Check.Contains(File.ReadAllText(Path.Combine(restored, "ideas-pending.json")), "sg_keep001", "and the card that was waiting for the user");
            }
            finally { await plugin.StopAsync(default); scope.DisposeAll(); }
        });
    }

    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NetPI.slnx"))) return dir.FullName;
        throw new Exception("Repository not found");
    }
}
