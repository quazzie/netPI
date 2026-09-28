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

    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NetPI.slnx"))) return dir.FullName;
        throw new Exception("Repository not found");
    }
}
