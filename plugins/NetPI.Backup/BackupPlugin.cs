using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Backup;

[NetPiPlugin("netpi.backup", Name = "Backups", Description = "Database, settings and ideas snapshots with checksums and retention", Order = 95)]
public sealed class BackupPlugin : INetPiPlugin
{
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private readonly SemaphoreSlim _gate = new(1);

    public Task StartAsync(IPluginContext ctx, CancellationToken ct)
    {
        ctx.Services.Register(new SettingsSection
        {
            Id = "backup", Title = "Backups", Group = "Data", Order = 10,
            Settings = [
                SettingInfo.Bool("backup.enabled", "Automatic backups", true, "A consistent database snapshot, settings and the ideas backlog (with its pending cards), stored in your NetPI home. Project files and skills are not included."),
                SettingInfo.Int("backup.intervalHours", "Interval", 24, "Checked at startup and every minute.", 1, 720, "hours"),
                SettingInfo.Int("backup.keepCount", "Automatic backups to keep", 7, "Manual backups are kept until you remove them.", 1, 365),
            ],
        });
        ctx.Rpc.Register("backup.list", (_, _) => Task.FromResult<object?>(List(ctx.Paths.Home)), "Available verified-format snapshots → { id, path, createdAt, automatic }[]");
        ctx.Rpc.Register("backup.create", async (_, token) => await CreateAsync(ctx, false, token), "Create a database, settings and ideas snapshot → { id, path, createdAt, automatic }");
        ctx.Rpc.Register("backup.verify", (r, _) => Task.FromResult<object?>(Verify(ctx.Paths.Home, r.Required("id"))), "Verify all checksums: { id } → snapshot manifest");
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        _worker = RunAsync(ctx, _stop.Token);
        return Task.CompletedTask;
    }

    private async Task RunAsync(IPluginContext ctx, CancellationToken ct)
    {
        // Let startup migrations complete before the initial snapshot.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (ctx.Settings.Get("backup.enabled", true))
                    {
                        var latest = List(ctx.Paths.Home).Select(n => DateTimeOffset.Parse(n!["createdAt"]!.GetValue<string>())).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
                        if (DateTimeOffset.UtcNow - latest >= TimeSpan.FromHours(Math.Clamp(ctx.Settings.Get("backup.intervalHours", 24), 1, 720)))
                            await CreateAsync(ctx, true, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Logger.LogError(ex, "Automatic backup failed"); }
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task<JsonObject> CreateAsync(IPluginContext ctx, bool automatic, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        string? staging = null;
        try
        {
            var root = Path.Combine(ctx.Paths.Home, "backups");
            Directory.CreateDirectory(root);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            staging = Path.Combine(root, ".pending-" + id);
            Directory.CreateDirectory(staging);
            // VACUUM INTO includes committed WAL contents and creates a consistent independent database.
            ctx.Db.Execute("VACUUM INTO @file", new { file = Path.Combine(staging, "netpi.db") });
            File.WriteAllText(Path.Combine(staging, "settings.json"), ctx.Settings.Snapshot().ToJsonString(NetPiJson.Indented));
            var files = new JsonObject();
            foreach (var name in SnapshotFiles(ctx, staging))
            {
                using var input = File.OpenRead(Path.Combine(staging, name));
                files[name] = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            }
            var manifest = new JsonObject { ["version"] = 1, ["id"] = id, ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["automatic"] = automatic, ["files"] = files };
            File.WriteAllText(Path.Combine(staging, "manifest.json"), manifest.ToJsonString(NetPiJson.Indented));
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(root, id);
            Directory.Move(staging, destination);
            staging = null;
            Verify(ctx.Paths.Home, id);
            // Never remove manual backups. Retention happens only after a successful new snapshot.
            foreach (var old in List(ctx.Paths.Home).Where(n => n!["automatic"]?.GetValue<bool>() == true)
                .OrderByDescending(n => n!["createdAt"]!.GetValue<string>()).Skip(Math.Clamp(ctx.Settings.Get("backup.keepCount", 7), 1, 365)))
            {
                var path = SnapshotPath(ctx.Paths.Home, old!["id"]!.GetValue<string>());
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                // A snapshot this build would not write (new files in it) is kept rather than deleted.
                if (Directory.EnumerateFileSystemEntries(path).Any(p => !new[] { "netpi.db", "settings.json", "manifest.json", "ideas.json", "ideas-pending.json", "ideas-migration.json" }.Contains(Path.GetFileName(p)))) continue;
                foreach (var file in Directory.EnumerateFiles(path)) File.Delete(file);
                Directory.Delete(path);
            }
            ctx.Events.Publish("backup.created", new JsonObject { ["id"] = id, ["path"] = destination });
            manifest["path"] = destination;
            return manifest;
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, true);
            _gate.Release();
        }
    }

    private static string SnapshotPath(string home, string id)
    {
        if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new RpcException("bad_request", "Invalid backup id");
        return Path.Combine(home, "backups", id);
    }

    public static JsonObject Verify(string home, string id)
    {
        var dir = SnapshotPath(home, id);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!.AsObject();
        if (manifest["version"]?.GetValue<int>() != 1 || manifest["id"]?.GetValue<string>() != id)
            throw new InvalidDataException("Unsupported or mismatched backup manifest");
        // Whatever the manifest lists, so a snapshot written by this or an older build is checked in full.
        foreach (var name in (manifest["files"] as JsonObject ?? []).Select(f => f.Key).Where(n => File.Exists(Path.Combine(dir, n))))
        {
            using var input = File.OpenRead(Path.Combine(dir, name));
            var actual = Convert.ToHexString(SHA256.HashData(input));
            if (!actual.Equals(manifest["files"]?[name]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Backup checksum mismatch: {name}");
        }
        return manifest;
    }

    /// <summary>How long a snapshot waits for the ideas files to be free before leaving them out of it.</summary>
    private static readonly TimeSpan IdeasLockWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The ideas files of this snapshot, copied while the ideas plugin's own lock is held: the backlog, the cards and
    /// check marks waiting in the pending file, and the import receipt of the old per-project files. Taking the same
    /// lock (<c>.&lt;name&gt;.lock</c>, which the OS releases even after a crash) is what makes this a coordinated
    /// snapshot — a card answered at this moment is either in both files or in neither. If the lock cannot be taken the
    /// files are left out and the log says so: an older snapshot is better than one that caught half an answer.
    /// </summary>
    private IReadOnlyList<string> SnapshotIdeas(IPluginContext ctx, string staging)
    {
        var name = ctx.Settings.Get("ideas.fileName", "ideas.json");
        var sources = new[] { name, "ideas-pending.json", "ideas-migration.json" }
            .Select(n => Path.Combine(ctx.Paths.Home, Path.GetFileName(n)))
            .Where(File.Exists)
            .ToList();
        if (sources.Count == 0) return [];
        var deadline = DateTimeOffset.UtcNow + IdeasLockWait;
        FileStream? hold = null;
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var lockPath = Path.Combine(ctx.Paths.Home, "." + Path.GetFileName(sources[0]) + ".lock");
                    hold = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
            }
            if (hold is null)
            {
                ctx.Logger.LogWarning("Backups: the ideas files were busy, so this snapshot does not include them (the previous snapshot still does)");
                return [];
            }
            foreach (var source in sources) File.Copy(source, Path.Combine(staging, Path.GetFileName(source)), overwrite: true);
            return sources.Select(s => Path.GetFileName(s)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Logger.LogWarning(ex, "Backups: the ideas files could not be read, so this snapshot does not include them");
            return [];
        }
        finally { hold?.Dispose(); }
    }

    /// <summary>The files a snapshot is made of, in the order they are written.</summary>
    private IReadOnlyList<string> SnapshotFiles(IPluginContext ctx, string staging)
    {
        var names = new List<string> { "netpi.db", "settings.json" };
        names.AddRange(SnapshotIdeas(ctx, staging));
        return names;
    }

    public static JsonArray List(string home)
    {
        var result = new JsonArray();
        var root = Path.Combine(home, "backups");
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(root).OrderDescending())
        {
            if (Path.GetFileName(dir).StartsWith('.')) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))) is not JsonObject m) continue;
                if (m["version"]?.GetValue<int>() != 1 || m["id"]?.GetValue<string>() != Path.GetFileName(dir)) continue;
                if (!DateTimeOffset.TryParse(m["createdAt"]?.GetValue<string>(), out _)) continue;
                if (m["automatic"] is not JsonValue flag || !flag.TryGetValue<bool>(out _)) continue;
                m["path"] = dir;
                result.Add(m);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        }
        return result;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stop?.Cancel();
        if (_worker is not null) await _worker;
        _stop?.Dispose();
    }
}
