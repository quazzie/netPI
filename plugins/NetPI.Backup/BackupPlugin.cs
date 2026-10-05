using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Backup;

[NetPiPlugin("netpi.backup", Name = "Backups", Description = "Database, settings and idea-image snapshots with checksums and retention (the ideas backlog travels in the database)", Order = 95)]
public sealed class BackupPlugin : INetPiPlugin
{
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly TimeSpan _retryBase, _checkEvery, _startupDelay;

    public BackupPlugin() : this(null, null, null) { }

    /// <summary>The timings of the automatic loop, with the production cadence as default (a test compresses them).</summary>
    internal BackupPlugin(TimeSpan? retryBase, TimeSpan? checkEvery, TimeSpan? startupDelay)
    {
        _retryBase = retryBase ?? TimeSpan.FromHours(1);
        _checkEvery = checkEvery ?? TimeSpan.FromMinutes(1);
        _startupDelay = startupDelay ?? TimeSpan.FromSeconds(10);
    }

    public Task StartAsync(IPluginContext ctx, CancellationToken ct)
    {
        ctx.Services.Register(new SettingsSection
        {
            Id = "backup", Title = "Backups", Group = "Data", Order = 10,
            Settings = [
                SettingInfo.Bool("backup.enabled", "Automatic backups", true, "A consistent snapshot of the database (which holds the ideas backlog, its cards and its commit cursors), of your settings and of the images attached to ideas, stored in your NetPI home. Project files and skills are not included."),
                SettingInfo.Int("backup.intervalHours", "Interval", 24, "Checked at startup and every minute.", 1, 720, "hours"),
                SettingInfo.Int("backup.keepCount", "Automatic backups to keep", 7, "Manual backups are kept until you remove them.", 1, 365),
            ],
        });
        ctx.Rpc.RegisterReadOnly("backup.list", (_, _) => Task.FromResult<object?>(List(ctx.Paths.Home)), "Available verified-format snapshots → { id, path, createdAt, automatic }[]");
        ctx.Rpc.Register("backup.create", async (_, token) => await CreateAsync(ctx, false, token), "Create a database and settings snapshot → { id, path, createdAt, automatic }");
        ctx.Rpc.RegisterReadOnly("backup.verify", (r, _) => Task.FromResult<object?>(Verify(ctx.Paths.Home, r.Required("id"))), "Verify all checksums: { id } → snapshot manifest");
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        _worker = RunAsync(ctx, _stop.Token);
        return Task.CompletedTask;
    }

    private async Task RunAsync(IPluginContext ctx, CancellationToken ct)
    {
        // Let startup migrations complete before the initial snapshot.
        try
        {
            SweepPending(ctx.Paths.Home);   // a kill mid-backup leaves a DB-sized .pending-*: nothing else deletes it
            await Task.Delay(_startupDelay, ct);
            var backoff = TimeSpan.Zero;
            while (!ct.IsCancellationRequested)
            {
                var wait = _checkEvery;
                try
                {
                    if (ctx.Settings.Get("backup.enabled", true))
                    {
                        var latest = List(ctx.Paths.Home).Select(n => DateTimeOffset.Parse(n!["createdAt"]!.GetValue<string>())).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
                        if (DateTimeOffset.UtcNow - latest >= TimeSpan.FromHours(Math.Clamp(ctx.Settings.Get("backup.intervalHours", 24), 1, 720)))
                        {
                            // the success is logged like the failure in the catch below: when an automatic backup last ran
                            // is otherwise invisible, and the backoff test needs to know the attempt it waits for is over.
                            var made = await CreateAsync(ctx, true, ct).ConfigureAwait(false);
                            ctx.Logger.LogInformation("Automatic backup created ({Snapshot})", made["id"]!.GetValue<string>());
                        }
                    }
                    backoff = TimeSpan.Zero;   // it came back: the regular cadence is back
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failing backup is not retried every minute (a full disk does not un-full in a minute):
                    // the wait starts at an hour and doubles, until the next try is a day away.
                    backoff = NextBackoff(backoff, _retryBase);
                    wait = backoff;
                    ctx.Logger.LogError(ex, "Automatic backup failed; the next try is in {Backoff}", backoff);
                }
                await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>The wait after a failed automatic backup: an hour at first, then doubling, capped at a day.</summary>
    internal static TimeSpan NextBackoff(TimeSpan current, TimeSpan baseBackoff)
    {
        var next = current <= TimeSpan.Zero ? baseBackoff : current * 2;
        return next > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : next;
    }

    /// <summary>A snapshot is a full copy of the store: it is refused before it starts when the disk cannot hold it.</summary>
    internal static void EnsureFreeSpace(long need, string root)
    {
        if (need <= 0) return;   // the store does not report a size: nothing to reserve
        var driveRoot = Path.GetPathRoot(root);
        if (driveRoot is null) return;
        try
        {
            var free = new DriveInfo(driveRoot).AvailableFreeSpace;
            if (free < need)
                throw new IOException($"Not enough free space for a backup: {need:N0} bytes are needed and {free:N0} are free");
        }
        catch (IOException) { throw; }
        catch { return; }   // the platform does not say how much is free: the copy is allowed
    }

    /// <summary>A kill mid-backup leaves a DB-sized .pending-* behind: it is swept at startup.</summary>
    internal static void SweepPending(string home)
    {
        var root = Path.Combine(home, "backups");
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!Path.GetFileName(dir).StartsWith(".pending-", StringComparison.Ordinal)) continue;
            try { Directory.Delete(dir, true); }
            catch { /* something else holds it: it stays */ }
        }
    }

    /// <summary>
    /// A snapshot only counts once it verifies: when the Verify after the move fails, the moved destination is deleted,
    /// or a bad snapshot would count as the latest and hold the next backup for a whole interval.
    /// </summary>
    internal static void FinalizeSnapshot(string home, string id)
    {
        var destination = Path.Combine(home, "backups", id);
        try { Verify(home, id); }
        catch
        {
            try { Directory.Delete(destination, true); }
            catch { /* it is gone or held: the failure below is the point */ }
            throw;
        }
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
            var store = ctx.Services.Require<IStorageAccess>();
            EnsureFreeSpace(store.Info.SizeBytes ?? 0, root);   // a copy that does not fit is refused before it starts
            var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            staging = Path.Combine(root, ".pending-" + id);
            Directory.CreateDirectory(staging);
            // The provider takes a consistent copy of the whole store, committed writes included, and names the files it
            // wrote. The ideas backlog, the cards waiting for an answer and the commit cursors are in it, so a backup
            // does not need the ideas plugin to be running, and it cannot be a snapshot that quietly left the ideas out.
            var written = store.Snapshot.Write(staging).ToList();
            File.WriteAllText(Path.Combine(staging, SettingsFile), ctx.Settings.Snapshot().ToJsonString(NetPiJson.Indented));
            written.Add(SettingsFile);
            // The idea images are the one thing a plugin keeps outside the store, so the snapshot carries them itself:
            // a restored backlog whose screenshots are gone is a broken backlog (idea-3m2h1g).
            var images = CopyIdeaImages(ctx.Paths.Home, staging);
            written.AddRange(images.Select(p => p.Key));
            var files = new JsonObject();
            foreach (var name in written)
            {
                using var input = File.OpenRead(Path.Combine(staging, name));
                files[name] = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            }
            var manifest = new JsonObject
            {
                ["version"] = 1, ["id"] = id, ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["automatic"] = automatic,
                // the store that wrote it, so a restore knows what reads the files the provider named
                ["provider"] = store.Info.Provider, ["files"] = files,
            };
            // what each copied image is, in the home it restores into (absent when there are none to carry)
            if (images.Count > 0) manifest["ideaImages"] = images;
            File.WriteAllText(Path.Combine(staging, "manifest.json"), manifest.ToJsonString(NetPiJson.Indented));
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(root, id);
            Directory.Move(staging, destination);
            staging = null;
            FinalizeSnapshot(ctx.Paths.Home, id);   // a snapshot that does not verify is deleted, not kept as the latest
            // Never remove manual backups. Retention happens only after a successful new snapshot.
            var expected = new HashSet<string>(written, StringComparer.Ordinal) { "manifest.json" };
            foreach (var old in List(ctx.Paths.Home).Where(n => n!["automatic"]?.GetValue<bool>() == true)
                .OrderByDescending(n => n!["createdAt"]!.GetValue<string>()).Skip(Math.Clamp(ctx.Settings.Get("backup.keepCount", 7), 1, 365)))
            {
                var path = SnapshotPath(ctx.Paths.Home, old!["id"]!.GetValue<string>());
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                // A snapshot this build would not write (files in it a new snapshot does not have) is kept rather than
                // deleted: an older snapshot's ideas files are still the only copy of anything, if the user is on one.
                // The idea images it carries do not count: a picture deleted since is exactly what retention lets go of,
                // or every snapshot taken while it existed would be kept past the count, each a full copy of the store.
                if (Directory.EnumerateFileSystemEntries(path).Select(p => Path.GetFileName(p))
                    .Any(f => !expected.Contains(f) && !f.StartsWith(IdeaImagesPrefix, StringComparison.Ordinal))) continue;
                foreach (var file in Directory.EnumerateFiles(path)) File.Delete(file);
                Directory.Delete(path);
            }
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

    /// <summary>The one file a snapshot has that is not the store's: the settings, which the store does not hold.</summary>
    private const string SettingsFile = "settings.json";

    /// <summary>The folder under the home the idea images live in — the only plugin-owned files a snapshot carries.</summary>
    private const string IdeaImagesDir = "idea-images";

    /// <summary>How a copied image is named inside a snapshot: a snapshot holds flat file names, so the folder is a prefix.</summary>
    private const string IdeaImagesPrefix = IdeaImagesDir + ".";

    /// <summary>
    /// The idea images, each copied into the staging snapshot under a name of its own and mapped to the home-relative
    /// path it comes from. A home without the folder has none: nothing is written and the manifest says nothing.
    /// </summary>
    private static JsonObject CopyIdeaImages(string home, string staging)
    {
        var copied = new JsonObject();
        var dir = Path.Combine(home, IdeaImagesDir);
        if (!Directory.Exists(dir)) return copied;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name.Length == 0) continue;
            var inSnapshot = IdeaImagesPrefix + name;
            File.Copy(file, Path.Combine(staging, inSnapshot));
            copied[inSnapshot] = $"{IdeaImagesDir}/{name}";
        }
        return copied;
    }

    public static JsonObject Verify(string home, string id)
    {
        var dir = SnapshotPath(home, id);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!.AsObject();
        if (manifest["version"]?.GetValue<int>() != 1 || manifest["id"]?.GetValue<string>() != id)
            throw new InvalidDataException("Unsupported or mismatched backup manifest");
        // Whatever the manifest lists, each one must be there, be a file inside this snapshot and have the checksum the
        // manifest says. A name that climbs out of the snapshot is refused before it is opened or written anywhere.
        var files = manifest["files"] as JsonObject ?? throw new InvalidDataException("The backup manifest lists no files");
        if (files.Count == 0) throw new InvalidDataException("The backup manifest lists no files");
        foreach (var (name, node) in files)
        {
            if (node is not JsonValue hash || !hash.TryGetValue<string>(out var expected) || expected.Length != 64)
                throw new InvalidDataException($"The backup manifest has no checksum for {name}");
            var path = Inside(dir, name);
            if (!File.Exists(path)) throw new InvalidDataException($"The backup is missing {name}, which its manifest lists");
            using var input = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(input));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Backup checksum mismatch: {name}");
        }
        // The store's files are the ones the provider wrote and named, and it says which provider it was so a restore
        // knows what reads them. The settings are the one file this plugin writes itself, so it is the one it requires.
        if (manifest["provider"] is not JsonValue named || !named.TryGetValue<string>(out var provider) || provider.Length == 0)
            throw new InvalidDataException("The backup manifest does not say which storage provider wrote it");
        if (!files.ContainsKey(SettingsFile)) throw new InvalidDataException($"The backup manifest does not list {SettingsFile}");
        if (files.Count < 2) throw new InvalidDataException("The backup manifest lists no file of the store: a snapshot without its data is not a snapshot");
        // An image the manifest carries has to be one of the files above (so it has a checksum that was checked) and has
        // to belong at a plain file name inside the images directory of the home it restores into.
        foreach (var (name, node) in manifest["ideaImages"] as JsonObject ?? [])
        {
            var target = node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            IdeaImage(target);
            if (!files.ContainsKey(name)) throw new InvalidDataException($"The backup manifest lists an idea image, {name}, that the snapshot does not have");
        }
        return manifest;
    }

    /// <summary>
    /// The file of the images directory an idea image belongs at. A manifest is a file anything can write, so the place
    /// it names is checked before a restore builds a path from it: one directory, then a plain file name in it.
    /// </summary>
    internal static string IdeaImage(string? target)
    {
        var parts = (target ?? "").Replace('\\', '/').Split('/');
        if (parts.Length != 2 || parts[0] != IdeaImagesDir || parts[1].Length == 0 || parts[1] is "." or ".." || parts[1] != Path.GetFileName(parts[1]))
            throw new InvalidDataException($"The backup manifest names an idea image at '{target}', which is not a file of the images directory");
        return parts[1];
    }

    /// <summary>
    /// A file of a snapshot is a plain name inside it: no directory, no <c>..</c>, no rooted path, nothing that
    /// resolves outside. A manifest is a file that was written once and can be edited by anything that can write to the
    /// snapshot, so what it names is checked before it is read (and, in the restore script, before it is written).
    /// </summary>
    internal static string Inside(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name is "." or "..")
            throw new InvalidDataException($"The backup manifest names '{name}', which is not a file of the snapshot");
        var full = Path.GetFullPath(Path.Combine(Path.GetFullPath(dir), name));
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, WorkspacePaths.Comparison))
            throw new InvalidDataException($"The backup manifest names '{name}', which is outside the snapshot");
        return full;
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
