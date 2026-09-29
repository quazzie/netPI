using System.Text.Json.Nodes;
using NetPI.Backup;
namespace NetPI.Host.Tests;
public static class ReviewBackupTests
{
    public static void Register(TestRunner r)
    {
        r.Add("review: backup verification must reject missing manifest files", () =>
        {
            var home = T.TempDir("review-missing-backup");
            var path = Path.Combine(home, "backups", "review");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "manifest.json"), new JsonObject
            {
                ["version"] = 1, ["id"] = "review", ["files"] = new JsonObject { ["netpi.db"] = "missing", ["settings.json"] = "missing", ["ideas.json"] = "missing" }
            }.ToJsonString());
            Exception? failure = null;
            try { BackupPlugin.Verify(home, "review"); } catch (Exception ex) { failure = ex; }
            Check.True(failure is not null, "verification accepted a snapshot with all three data files missing");
        });
    }
}
