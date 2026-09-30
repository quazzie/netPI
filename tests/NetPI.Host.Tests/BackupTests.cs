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
    /// The ideas backlog, the cards waiting for an answer and the commit cursors are plugin-owned tables in
    /// <c>netpi.db</c>, so they travel inside the consistent database snapshot: restoring a snapshot into a fresh home
    /// and querying the real ideas rows is what shows they came back (docs/plans/2026-09-29-ideas-sqlite-migration.md,
    /// assignment D). An older snapshot that still carries the ideas files restores exactly what it has.
    /// </summary>
    private static async Task BackupIdeasRoundTrip(TestRunner r)
    {
        r.Add("backup: the SQLite-backed ideas backlog, its cards and its cursors are in the snapshot and come back", async () =>
        {
            var home = T.TempDir("backup-ideas");
            await using var kernel = HostKernel.Create(new NetPiServerOptions { Home = home, ConsoleLogging = false });
            var scope = new PluginScope("netpi.backup", kernel.Log);
            var ctx = new PluginContext(kernel, "netpi.backup", home, scope, default, () => "test");
            var plugin = new BackupPlugin();
            kernel.Settings.Set("backup.enabled", JsonValue.Create(false));

            // The backlog as the ideas plugin stores it: a table, an id, a revision and the document.
            foreach (var statement in new[]
            {
                "CREATE TABLE ideas_items (id TEXT PRIMARY KEY, ord INTEGER NOT NULL, revision INTEGER NOT NULL," +
                    " title TEXT NOT NULL, status TEXT NOT NULL, priority TEXT NOT NULL, project_id TEXT, project_name TEXT," +
                    " created_at TEXT, updated_at TEXT, doc TEXT NOT NULL)",
                """INSERT INTO ideas_items VALUES ('idea-keep01', 0, 1, 'Keep me', 'open', 'medium', NULL, NULL,""" +
                    """ '2026-09-29T10:00:00Z', '2026-09-29T10:00:00Z', '{"id":"idea-keep01","title":"Keep me","custom":{"kept":true}}')""",
                "CREATE TABLE ideas_suggestions (id TEXT PRIMARY KEY, ord INTEGER NOT NULL, kind TEXT NOT NULL, session_id TEXT," +
                    " idea_id TEXT, project_id TEXT, source_rev INTEGER, title TEXT NOT NULL, at TEXT NOT NULL, doc TEXT NOT NULL)",
                "INSERT INTO ideas_suggestions VALUES ('sg_keep001', 0, 'save', 'ses_1', NULL, NULL, NULL, 'Waiting'," +
                    " '2026-09-29T10:00:00Z', '{\"id\":\"sg_keep001\",\"kind\":\"save\",\"title\":\"Waiting\"}')",
                "CREATE TABLE ideas_repos (repo TEXT PRIMARY KEY, project_id TEXT, project_name TEXT, hash TEXT, at TEXT NOT NULL," +
                    " tries INTEGER NOT NULL, error TEXT)",
                "INSERT INTO ideas_repos VALUES ('C:/repo', 'prj_1', 'Demo', 'abc123', '2026-09-29T10:00:00Z', 1, NULL)",
            }) kernel.Db.Execute(statement);

            await plugin.StartAsync(ctx, default);
            try
            {
                var manual = await plugin.CreateAsync(ctx, false, default);
                var dir = manual["path"]!.GetValue<string>();
                Check.Equal(2, manual["files"]!.AsObject().Count, "the database and the settings");
                Check.False(manual.ContainsKey("noIdeas"), "a snapshot is never a snapshot that left the ideas out");
                Check.False(File.Exists(Path.Combine(dir, "ideas.json")), "the ideas are not a file any more");
                BackupPlugin.Verify(home, manual["id"]!.GetValue<string>());

                // The database in the snapshot really holds them (the ideas plugin is not running here at all).
                var checkFile = Path.Combine(home, "check-ideas.db");
                File.Copy(Path.Combine(dir, "netpi.db"), checkFile);
                using (var snapshot = new Database(checkFile))
                {
                    Check.Equal("ok", snapshot.Scalar<string>("PRAGMA integrity_check"));
                    Check.Equal("Keep me", snapshot.Scalar<string>("SELECT title FROM ideas_items WHERE id = 'idea-keep01'"));
                    var doc = snapshot.Scalar<string>("SELECT doc FROM ideas_items WHERE id = 'idea-keep01'")!;
                    Check.True(doc.Contains("\"kept\":true"), "a field the ideas plugin stores as it is: " + doc);
                    Check.Equal(1L, snapshot.Scalar<long>("SELECT COUNT(*) FROM ideas_suggestions WHERE id = 'sg_keep001'"));
                    Check.Equal("abc123", snapshot.Scalar<string>("SELECT hash FROM ideas_repos WHERE repo = 'C:/repo'"));
                }

                // Restore into a new home and start a host on it: the data is there to be read.
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
                using (var recovered = new Database(Path.Combine(restored, "netpi.db")))
                {
                    Check.Equal("Keep me", recovered.Scalar<string>("SELECT title FROM ideas_items WHERE id = 'idea-keep01'"));
                    Check.Equal(1L, recovered.Scalar<long>("SELECT COUNT(*) FROM ideas_suggestions"));
                    Check.Equal(1L, recovered.Scalar<long>("SELECT COUNT(*) FROM ideas_repos"));
                }

                // An older snapshot, with the ideas as files, still restores exactly what it has.
                var old = Path.Combine(home, "backups", "20200101-000000-old");
                Directory.CreateDirectory(old);
                var backlog = "{ \"version\": 1, \"ideas\": [ { \"id\": \"idea-old001\", \"title\": \"From the file era\" } ] }\n";
                File.WriteAllText(Path.Combine(old, "ideas.json"), backlog);
                File.Copy(Path.Combine(dir, "netpi.db"), Path.Combine(old, "netpi.db"));
                File.Copy(Path.Combine(dir, "settings.json"), Path.Combine(old, "settings.json"));
                var hashes = new JsonObject();
                foreach (var name in new[] { "netpi.db", "settings.json", "ideas.json" })
                    hashes[name] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(old, name)))).ToLowerInvariant();
                File.WriteAllText(Path.Combine(old, "manifest.json"), new JsonObject
                {
                    ["version"] = 1, ["id"] = "20200101-000000-old", ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["automatic"] = true, ["files"] = hashes,
                }.ToJsonString());
                BackupPlugin.Verify(home, "20200101-000000-old");
                var oldHome = Path.Combine(home, "restored-old");
                info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add(Path.Combine(repo, "scripts", "restore-backup.mjs"));
                info.ArgumentList.Add(old);
                info.ArgumentList.Add(oldHome);
                using var oldProcess = Process.Start(info)!;
                var oldOutput = oldProcess.StandardOutput.ReadToEndAsync(); var oldError = oldProcess.StandardError.ReadToEndAsync();
                await oldProcess.WaitForExitAsync();
                if (oldProcess.ExitCode != 0) Console.WriteLine("    " + await oldError);
                await oldOutput;
                Check.Equal(0, oldProcess.ExitCode, "a snapshot of the file era still restores");
                Check.Equal(backlog, File.ReadAllText(Path.Combine(oldHome, "ideas.json")), "with the backlog the cutover then imports");

                // Retention keeps a snapshot this build would not write: its ideas files are the only copy.
                kernel.Settings.Set("backup.keepCount", JsonValue.Create(1));
                await plugin.CreateAsync(ctx, true, default);
                Check.True(Directory.Exists(old), "an older snapshot with ideas files is kept rather than deleted");
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
