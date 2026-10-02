using System.Text.Json.Nodes;
using NetPI.Backup;
namespace NetPI.Host.Tests;

/// <summary>
/// The regressions the post-merge review reproduced, against the verifier that replaced the one it read: a manifest that
/// names a file the snapshot does not contain is not a verified snapshot, whatever the checksum column says.
/// </summary>
public static class ReviewBackupTests
{
    /// <summary>A snapshot directory with a manifest of exactly these files (values are the checksums as written).</summary>
    private static (string Home, string Id) Snapshot(JsonObject files, string id = "review")
    {
        var home = T.TempDir("review-backup");
        var path = Path.Combine(home, "backups", id);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "manifest.json"), new JsonObject
        {
            ["version"] = 1,
            ["id"] = id,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["automatic"] = false,
            ["provider"] = "sqlite",
            ["files"] = files,
        }.ToJsonString());
        return (home, id);
    }

    private static string Hash(string content) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public static void Register(TestRunner r)
    {
        r.Add("review: backup verification must reject missing manifest files", () =>
        {
            // Real-looking checksums: the point is that the files are not there, whatever the manifest says about them.
            var (home, id) = Snapshot(new JsonObject
            {
                ["netpi.db"] = Hash("db"), ["settings.json"] = Hash("{}"), ["ideas.json"] = Hash("{}"),
            });
            Exception? failure = null;
            try { BackupPlugin.Verify(home, id); } catch (Exception ex) { failure = ex; }
            Check.True(failure is not null, "verification accepted a snapshot with all three data files missing");
            Check.Contains(failure?.Message ?? "", "is missing", "and it says which one: " + failure?.Message);
        });

        r.Add("backup verification: a valid snapshot verifies, and one that is not complete does not", () =>
        {
            var (home, id) = Snapshot(new JsonObject { ["netpi.db"] = Hash("db"), ["settings.json"] = Hash("{}") });
            Directory.CreateDirectory(Path.Combine(home, "backups", id));
            File.WriteAllText(Path.Combine(home, "backups", id, "netpi.db"), "db");
            File.WriteAllText(Path.Combine(home, "backups", id, "settings.json"), "{}");
            BackupPlugin.Verify(home, id);

            // A checksum that does not match the bytes is not a snapshot.
            File.WriteAllText(Path.Combine(home, "backups", id, "netpi.db"), "tampered");
            Check.Throws<InvalidDataException>(() => BackupPlugin.Verify(home, id), "the checksum is checked");

            // The required entries are required.
            var (h2, i2) = Snapshot(new JsonObject { ["settings.json"] = Hash("{}") });
            File.WriteAllText(Path.Combine(h2, "backups", i2, "settings.json"), "{}");
            Check.Throws<InvalidDataException>(() => BackupPlugin.Verify(h2, i2), "a snapshot without a database is not a snapshot");

            // An empty manifest, a manifest that is not an object of files, and a manifest with no checksum.
            var (h3, i3) = Snapshot(new JsonObject());
            Check.Throws<InvalidDataException>(() => BackupPlugin.Verify(h3, i3), "a manifest that lists nothing");
            var (h4, i4) = Snapshot(new JsonObject { ["netpi.db"] = "x" });
            Check.Throws<InvalidDataException>(() => BackupPlugin.Verify(h4, i4), "a manifest whose entry is not a checksum");
        });

        r.Add("backup verification: a manifest cannot name a file outside the snapshot", () =>
        {
            var outside = Path.Combine(T.TempDir("review-outside"), "secret.json");
            File.WriteAllText(outside, "secret");
            foreach (var name in new[] { Path.Combine("..", "..", "secret.json"), "../secret.json", "sub/secret.json", "C:\\secret.json" })
            {
                var (home, id) = Snapshot(new JsonObject { ["netpi.db"] = Hash("db"), ["settings.json"] = Hash("{}"), [name] = Hash("secret") });
                Directory.CreateDirectory(Path.Combine(home, "backups", id));
                File.WriteAllText(Path.Combine(home, "backups", id, "netpi.db"), "db");
                File.WriteAllText(Path.Combine(home, "backups", id, "settings.json"), "{}");
                var failure = Check.Throws<InvalidDataException>(() => BackupPlugin.Verify(home, id), $"'{name}' is refused");
                Check.Contains(failure.Message, "not a file of the snapshot");
            }
            _ = outside;
        });
    }
}
