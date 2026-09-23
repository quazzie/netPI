using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Plugins;

/// <summary>
/// Discovers, loads, starts, hot-reloads and unloads plugins.
/// <list type="bullet">
/// <item>Discovery: every sub-folder <c>X/</c> of a plugin directory containing <c>X.dll</c> (or a <c>plugin.json</c> naming its assembly).</item>
/// <item>Each load shadow-copies the folder (except <c>wwwroot</c>) to <c>TempDir/shadow/&lt;name&gt;/&lt;pid&gt;_&lt;n&gt;/</c> and loads it into a
/// collectible <see cref="PluginLoadContext"/>, so a build can overwrite the originals while the plugin runs.</item>
/// <item>Plugins start in (Order, Name) sequence with a 30 s timeout. Failures leave the plugin <c>failed</c> with its registrations removed.</item>
/// <item>A watcher per plugin directory reloads a plugin ~800 ms after its dll/pdb/json files change; <c>wwwroot</c> changes only bump its UI version.</item>
/// </list>
/// Operations that change plugin state are serialized; do not call <see cref="ReloadAsync"/> from a plugin's StartAsync/StopAsync.
/// </summary>
internal sealed class PluginManager : IPluginManager, IAsyncDisposable
{
    /// <summary>Published after an unload check: <c>{ id, collected }</c> (collected = false means the old code is leaking).</summary>
    public const string UnloadedEvent = "plugins.unloaded";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(800);
    private static readonly MethodInfo? ClearStjCaches = typeof(JsonSerializer).Assembly
        .GetType("System.Text.Json.JsonSerializerOptionsUpdateHandler")
        ?.GetMethod("ClearCache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private static int s_shadowCounter;
    private static long s_versionCounter;

    private readonly HostKernel _k;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly Lock _gate = new();
    private readonly List<PluginEntry> _entries = [];
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(PathUtil.Comparer);
    private readonly Debouncer _rescan;
    private readonly Debouncer _applyEnabled;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _shadowRoot;
    private volatile bool _disposed;

    public PluginManager(HostKernel kernel, ILogger log)
    {
        _k = kernel;
        _log = log;
        _shadowRoot = Path.Combine(kernel.Paths.TempDir, "shadow");
        _rescan = new Debouncer(ReloadDelay, () => _ = Guard(RescanAsync(_shutdown.Token), "rescan"));
        _applyEnabled = new Debouncer(TimeSpan.FromMilliseconds(200), () => _ = Guard(ApplyEnabledStateAsync(_shutdown.Token), "apply enabled state"));
    }

    // ------------------------------------------------------------------ IPluginManager

    public IReadOnlyList<PluginInfo> List()
    {
        var sets = EnabledSets.Read(_k.Settings);
        lock (_gate)
            return _entries.OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.ToInfo(IsEnabled(e, sets))).ToList();
    }

    public async Task ReloadAsync(string pluginId, CancellationToken ct = default)
    {
        var e = FindById(pluginId) ?? throw new KeyNotFoundException($"Plugin '{pluginId}' not found");
        await ReloadEntryAsync(e, ct).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default)
    {
        var e = FindById(pluginId) ?? throw new KeyNotFoundException($"Plugin '{pluginId}' not found");
        var disabled = _k.Settings.Get<List<string>>("plugins.disabled") ?? [];
        var forced = _k.Settings.Get<List<string>>("plugins.enabled") ?? [];
        bool Match(string s) => s.Equals(e.Id, StringComparison.OrdinalIgnoreCase) || s.Equals(e.FolderName, StringComparison.OrdinalIgnoreCase);
        var forcedChanged = false;
        if (enabled)
        {
            disabled.RemoveAll(Match);
            if (e.Manifest is { Enabled: false } && !forced.Any(Match)) { forced.Add(e.Id); forcedChanged = true; }
        }
        else
        {
            if (!disabled.Any(Match)) disabled.Add(e.Id);
            forcedChanged = forced.RemoveAll(Match) > 0;
        }
        _k.Settings.Set("plugins.disabled", new JsonArray(disabled.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()));
        if (forcedChanged) _k.Settings.Set("plugins.enabled", new JsonArray(forced.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()));
        await ApplyEnabledStateAsync(ct).ConfigureAwait(false);
    }

    public async Task RescanAsync(CancellationToken ct = default)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        var changed = false;
        try
        {
            if (_disposed) return;
            foreach (var e in Snapshot())
            {
                if (Directory.Exists(e.Folder) && File.Exists(Path.Combine(e.Folder, e.AssemblyFile))) continue;
                _log.LogInformation("Plugin {Id} was removed from {Folder}", e.Id, e.Folder);
                await StopInstanceAsync(e, track: true).ConfigureAwait(false);
                lock (_gate) _entries.Remove(e);
                e.DisposeTimers();
                changed = true;
            }
            var found = Discover();
            if (found.Count > 0)
            {
                lock (_gate) _entries.AddRange(found);
                await LoadAndStartAsync(found, ct).ConfigureAwait(false);
                changed = true;
            }
        }
        finally { _op.Release(); }
        EnsureWatchers();
        if (changed) PublishChanged();
    }

    // ------------------------------------------------------------------ host API

    /// <summary>Initial discovery and start (called once by the host).</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_shadowRoot);
        CleanShadowRoot();
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var found = Discover();
            lock (_gate) _entries.AddRange(found);
            await LoadAndStartAsync(found, ct).ConfigureAwait(false);
        }
        finally { _op.Release(); }
        EnsureWatchers();
        PublishChanged();
    }

    /// <summary>The plugin's real <c>wwwroot</c> folder (served at /plugins/{id}/), or null.</summary>
    public string? GetWebRoot(string pluginId)
    {
        var e = FindById(pluginId);
        if (e is null) return null;
        var dir = Path.Combine(e.Folder, "wwwroot");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>React to settings.changed (plugins.disabled / plugins.enabled edits, also external ones).</summary>
    public void OnSettingsChanged(BusEvent e)
    {
        if (_disposed) return;
        if (e.Data is JsonObject o && o["path"] is JsonValue v && v.TryGetValue<string>(out var path) &&
            !path.StartsWith("plugins", StringComparison.Ordinal))
            return;
        _applyEnabled.Trigger();
    }

    /// <summary>Diagnostics/tests: weak reference to the current load context and whether the last unloaded one was collected.</summary>
    internal (WeakReference? Current, WeakReference? LastUnloaded, bool? LastUnloadCollected) GetLoadState(string pluginId)
    {
        var e = FindById(pluginId) ?? throw new KeyNotFoundException(pluginId);
        return (e.Alc is null ? null : new WeakReference(e.Alc), e.LastUnloaded, e.LastUnloadCollected);
    }

    // ------------------------------------------------------------------ discovery

    private List<PluginEntry> Discover()
    {
        var result = new List<PluginEntry>();
        HashSet<string> knownFolders, knownIds;
        lock (_gate)
        {
            knownFolders = new HashSet<string>(_entries.Select(e => e.Folder), PathUtil.Comparer);
            knownIds = new HashSet<string>(_entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        }
        foreach (var root in _k.Paths.PluginDirs)
        {
            string[] dirs;
            try
            {
                if (!Directory.Exists(root)) continue;
                dirs = Directory.GetDirectories(root);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Cannot list plugin directory {Dir}: {Error}", root, ex.Message);
                continue;
            }
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                var folder = PathUtil.Normalize(dir);
                if (knownFolders.Contains(folder)) continue;
                var folderName = Path.GetFileName(folder);
                if (folderName.StartsWith('.') || folderName.StartsWith('_')) continue;
                var manifestFile = Path.Combine(folder, "plugin.json");
                var manifest = File.Exists(manifestFile) ? PluginManifest.TryLoad(manifestFile, _log) : null;
                var asmFile = AssemblyFileName(manifest, folderName);
                if (!File.Exists(Path.Combine(folder, asmFile))) continue;
                var id = manifest?.Id ?? Path.GetFileNameWithoutExtension(asmFile).ToLowerInvariant();
                if (!knownIds.Add(id))
                {
                    _log.LogWarning("Skipping plugin folder {Folder}: a plugin with id '{Id}' is already registered", folder, id);
                    continue;
                }
                knownFolders.Add(folder);
                result.Add(new PluginEntry
                {
                    Folder = folder, FolderName = folderName, Root = PathUtil.Normalize(root), AssemblyFile = asmFile, Manifest = manifest,
                    Id = id, Name = manifest?.Name ?? folderName, Description = manifest?.Description, Order = manifest?.Order ?? 100,
                });
            }
        }
        return result;
    }

    private static string AssemblyFileName(PluginManifest? manifest, string folderName)
    {
        var name = manifest?.Assembly ?? folderName + ".dll";
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
    }

    // ------------------------------------------------------------------ load / start / stop

    private async Task LoadAndStartAsync(List<PluginEntry> list, CancellationToken ct)
    {
        var sets = EnabledSets.Read(_k.Settings);
        var loaded = new List<PluginEntry>();
        foreach (var e in list)
        {
            if (!IsEnabled(e, sets))
            {
                e.State = "disabled";
                continue;
            }
            if (await LoadAssemblyAsync(e).ConfigureAwait(false)) loaded.Add(e);
        }
        foreach (var e in loaded.OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsEnabled(e, sets)) // the real id (from the attribute) may be disabled
            {
                await UnloadContextAsync(e, track: true).ConfigureAwait(false);
                e.State = "disabled";
                continue;
            }
            var twin = Snapshot().FirstOrDefault(o => !ReferenceEquals(o, e) && o.Instance is not null && o.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase));
            if (twin is not null)
            {
                await UnloadContextAsync(e, track: true).ConfigureAwait(false);
                e.State = "failed";
                e.Error = $"Duplicate plugin id '{e.Id}' (already loaded from {twin.Folder})";
                _log.LogWarning("Plugin in {Folder}: {Error}", e.Folder, e.Error);
                continue;
            }
            await StartInstanceAsync(e, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> LoadAssemblyAsync(PluginEntry e)
    {
        var sw = Stopwatch.StartNew();
        e.State = "loading";
        e.Error = null;
        for (var attempt = 1; ; attempt++)
        {
            string? shadow = null;
            PluginLoadContext? alc = null;
            try
            {
                shadow = ShadowCopy(e);
                var main = Path.Combine(shadow, e.AssemblyFile);
                alc = new PluginLoadContext(main, $"plugin:{e.FolderName}#{e.LoadCount + 1}");
                var asm = alc.LoadFromAssemblyPath(main);
                var type = FindPluginType(asm);
                var attr = type.GetCustomAttribute<NetPiPluginAttribute>();
                var id = attr?.Id ?? e.Manifest?.Id ?? asm.GetName().Name!.ToLowerInvariant();
                if (attr is not null && e.Manifest?.Id is { } manifestId && !manifestId.Equals(attr.Id, StringComparison.OrdinalIgnoreCase))
                    _log.LogWarning("Plugin {Folder}: plugin.json id '{ManifestId}' differs from the attribute id '{Id}'; using '{Id}'", e.Folder, manifestId, attr.Id, attr.Id);
                e.Id = id;
                e.Name = attr?.Name ?? e.Manifest?.Name ?? e.FolderName;
                e.Description = attr?.Description ?? e.Manifest?.Description;
                e.Order = e.Manifest?.Order ?? attr?.Order ?? 100;
                e.Version = VersionOf(asm);
                e.Alc = alc;
                e.PluginType = type;
                e.ShadowDir = shadow;
                e.LoadMs = sw.ElapsedMilliseconds;
                return true;
            }
            catch (Exception ex)
            {
                if (alc is not null)
                {
                    try { alc.Unload(); } catch { }
                }
                PathUtil.TryDeleteDirectory(shadow); // may fail on Windows until collected; cleaned at next start
                var inner = JsonUtil.Unwrap(ex);
                if (attempt < 3 && inner is IOException or BadImageFormatException or FileLoadException or UnauthorizedAccessException)
                {
                    // Typically a build still writing the files.
                    await Task.Delay(400 * attempt).ConfigureAwait(false);
                    continue;
                }
                e.State = "failed";
                e.Error = inner.Message;
                _log.LogError(inner, "Plugin {Folder} failed to load", e.Folder);
                return false;
            }
        }
    }

    private async Task StartInstanceAsync(PluginEntry e, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var scope = new PluginScope(e.Id, _log);
        var stopping = new CancellationTokenSource();
        e.UiVersion = NewVersion();
        var context = new PluginContext(_k, e.Id, e.Folder, scope, stopping.Token, () => e.UiVersion);
        INetPiPlugin? instance = null;
        try
        {
            instance = (INetPiPlugin)Activator.CreateInstance(e.PluginType!)!;
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token, _shutdown.Token);
            startCts.CancelAfter(StartTimeout);
            await instance.StartAsync(context, startCts.Token).WaitAsync(StartTimeout, ct).ConfigureAwait(false);
            e.Instance = instance;
            e.Scope = scope;
            e.Stopping = stopping;
            e.State = "running";
            e.Error = null;
            e.LoadedAt = DateTimeOffset.UtcNow;
            e.LoadCount++;
            e.LoadMs += sw.ElapsedMilliseconds;
            _log.LogInformation("Plugin {Id} {Version} started in {Ms} ms", e.Id, e.Version, e.LoadMs);
        }
        catch (Exception ex)
        {
            var inner = JsonUtil.Unwrap(ex);
            var message = inner is TimeoutException ? $"StartAsync did not complete within {StartTimeout.TotalSeconds:0}s" : inner.Message;
            _log.LogError(inner, "Plugin {Id} failed to start", e.Id);
            if (instance is not null) await StopQuietlyAsync(instance, e.Id).ConfigureAwait(false);
            Cancel(stopping, e.Id);
            scope.DisposeAll();
            stopping.Dispose();
            instance = null;
            await UnloadContextAsync(e, track: true).ConfigureAwait(false);
            e.State = "failed";
            e.Error = message;
        }
    }

    /// <summary>StopAsync (10 s) → cancel Stopping → dispose registrations (reverse) → unload the context.</summary>
    private async Task StopInstanceAsync(PluginEntry e, bool track)
    {
        var instance = e.Instance;
        if (instance is not null) await StopQuietlyAsync(instance, e.Id).ConfigureAwait(false);
        Cancel(e.Stopping, e.Id);
        e.Scope?.DisposeAll();
        e.Stopping?.Dispose();
        e.Instance = null;
        e.Scope = null;
        e.Stopping = null;
        instance = null;
        await UnloadContextAsync(e, track).ConfigureAwait(false);
        if (e.State == "running") e.State = "stopped";
    }

    private async Task StopQuietlyAsync(INetPiPlugin instance, string id)
    {
        try
        {
            using var cts = new CancellationTokenSource(StopTimeout);
            await instance.StopAsync(cts.Token).WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(JsonUtil.Unwrap(ex), "Plugin {Id}: StopAsync failed", id);
        }
    }

    private void Cancel(CancellationTokenSource? cts, string id)
    {
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (Exception ex) { _log.LogWarning(ex, "Plugin {Id}: a Stopping callback threw", id); }
    }

    private async Task UnloadContextAsync(PluginEntry e, bool track)
    {
        if (e.Alc is null) return;
        var shadow = e.ShadowDir;
        e.PluginType = null;
        e.ShadowDir = null;
        // Deliver queued events (they may carry plugin payloads) before snapshotting the ring buffer.
        try { await _k.Bus.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        _k.Bus.DetachCollectible();
        _k.Services.ClearCache();
        var weak = UnloadAndTrack(e);
        ClearJsonCaches();
        e.LastUnloaded = weak;
        e.LastUnloadCollected = null;
        if (track) _ = Task.Run(() => WatchCollectionAsync(e, weak, shadow));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference UnloadAndTrack(PluginEntry e)
    {
        var alc = e.Alc!;
        e.Alc = null;
        alc.Unload();
        return new WeakReference(alc);
    }

    private static void ClearJsonCaches()
    {
        NetPiJson.ResetCollectibleCache();
        // System.Text.Json keeps a static reflection-emit accessor cache keyed by type; clear it like hot reload does.
        try { ClearStjCaches?.Invoke(null, [null]); }
        catch { /* best effort */ }
    }

    private async Task WatchCollectionAsync(PluginEntry e, WeakReference weak, string? shadow)
    {
        for (var i = 0; i < 25 && weak.IsAlive; i++)
        {
            await Task.Delay(i == 0 ? 20 : Math.Min(100 * i, 1000)).ConfigureAwait(false);
            _k.Bus.DetachCollectible();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        var collected = !weak.IsAlive;
        if (ReferenceEquals(e.LastUnloaded, weak)) e.LastUnloadCollected = collected;
        _k.Bus.Publish(new BusEvent { Type = UnloadedEvent, Data = new { id = e.Id, collected }, Source = "host" });
        if (collected)
        {
            _log.LogDebug("Plugin {Id}: previous load context unloaded", e.Id);
            PathUtil.TryDeleteDirectory(shadow);
        }
        else
        {
            _log.LogWarning("Plugin {Id}: the previous load context is still alive after unload (something still references plugin objects); its memory is not reclaimed", e.Id);
        }
    }

    private async Task ReloadEntryAsync(PluginEntry e, CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            lock (_gate) if (!_entries.Contains(e)) return;
            _log.LogInformation("Reloading plugin {Id}", e.Id);
            await StopInstanceAsync(e, track: true).ConfigureAwait(false);
            var manifestFile = Path.Combine(e.Folder, "plugin.json");
            e.Manifest = File.Exists(manifestFile) ? PluginManifest.TryLoad(manifestFile, _log) : null;
            e.AssemblyFile = AssemblyFileName(e.Manifest, e.FolderName);
            if (!File.Exists(Path.Combine(e.Folder, e.AssemblyFile)))
            {
                e.State = "unloaded";
                e.Error = $"{e.AssemblyFile} not found";
                return;
            }
            var sets = EnabledSets.Read(_k.Settings);
            if (!IsEnabled(e, sets))
            {
                e.State = "disabled";
                return;
            }
            await LoadAndStartAsync([e], ct).ConfigureAwait(false);
        }
        finally
        {
            _op.Release();
            PublishChanged();
        }
    }

    private async Task ApplyEnabledStateAsync(CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        var changed = false;
        try
        {
            if (_disposed) return;
            var sets = EnabledSets.Read(_k.Settings);
            foreach (var e in Snapshot().Where(e => e.Instance is not null).OrderByDescending(e => e.Order))
            {
                if (IsEnabled(e, sets)) continue;
                _log.LogInformation("Disabling plugin {Id}", e.Id);
                await StopInstanceAsync(e, track: true).ConfigureAwait(false);
                e.State = "disabled";
                changed = true;
            }
            var toStart = Snapshot().Where(e => e.State == "disabled" && IsEnabled(e, sets)).ToList();
            if (toStart.Count > 0)
            {
                await LoadAndStartAsync(toStart, ct).ConfigureAwait(false);
                changed = true;
            }
        }
        finally { _op.Release(); }
        if (changed) PublishChanged();
    }

    // ------------------------------------------------------------------ watching

    private void EnsureWatchers()
    {
        if (_disposed) return;
        foreach (var root in _k.Paths.PluginDirs)
        {
            lock (_gate)
            {
                if (_watchers.ContainsKey(root) || !Directory.Exists(root)) continue;
                try
                {
                    var w = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    var r = root;
                    w.Changed += (_, a) => OnFileEvent(r, a.FullPath);
                    w.Created += (_, a) => OnFileEvent(r, a.FullPath);
                    w.Deleted += (_, a) => OnFileEvent(r, a.FullPath);
                    w.Renamed += (_, a) =>
                    {
                        OnFileEvent(r, a.OldFullPath);
                        OnFileEvent(r, a.FullPath);
                    };
                    w.Error += (_, a) =>
                    {
                        _log.LogWarning(a.GetException(), "Plugin watcher error on {Dir}", r);
                        _rescan.Trigger();
                    };
                    w.EnableRaisingEvents = true;
                    _watchers[root] = w;
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Cannot watch plugin directory {Dir}: {Error} (hot reload disabled there)", root, ex.Message);
                }
            }
        }
    }

    private void OnFileEvent(string root, string fullPath)
    {
        if (_disposed) return;
        string rel;
        try { rel = Path.GetRelativePath(root, fullPath); }
        catch { return; }
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return;
        var parts = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        PluginEntry? e;
        lock (_gate)
            e = _entries.FirstOrDefault(x => PathUtil.Comparer.Equals(x.Root, root) && PathUtil.Comparer.Equals(x.FolderName, parts[0]));
        if (e is null)
        {
            _rescan.Trigger(); // a new plugin folder (or files arriving in one)
            return;
        }
        if (parts.Length == 1)
        {
            if (!Directory.Exists(e.Folder)) _rescan.Trigger();
            return;
        }
        if (parts[1].Equals("wwwroot", StringComparison.OrdinalIgnoreCase))
        {
            (e.UiTimer ??= new Debouncer(TimeSpan.FromMilliseconds(300), () => BumpUiVersion(e))).Trigger();
            return;
        }
        var file = parts[^1];
        var ext = Path.GetExtension(file);
        if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("plugin.json", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
        {
            (e.ReloadTimer ??= new Debouncer(ReloadDelay, () => _ = Guard(ReloadEntryAsync(e, _shutdown.Token), "reload " + e.Id))).Trigger();
        }
    }

    private void BumpUiVersion(PluginEntry e)
    {
        if (_disposed) return;
        e.UiVersion = NewVersion();
        _k.Ui.SetVersion(e.Id, e.UiVersion);
        _k.Ui.NotifyChanged();
        _log.LogDebug("Plugin {Id}: UI files changed (version {Version})", e.Id, e.UiVersion);
    }

    // ------------------------------------------------------------------ shadow copies

    private string ShadowCopy(PluginEntry e)
    {
        var n = Interlocked.Increment(ref s_shadowCounter);
        var dir = Path.Combine(_shadowRoot, SafeName(e.FolderName), $"{Environment.ProcessId}_{n}");
        CopyDirectory(e.Folder, dir, top: true);
        return dir;
    }

    private static void CopyDirectory(string source, string target, bool top)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;
            if (top && (name.Equals("wwwroot", StringComparison.OrdinalIgnoreCase) || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))) continue;
            CopyDirectory(dir, Path.Combine(target, name), top: false);
        }
    }

    /// <summary>Delete shadow copies left behind by processes that are no longer running.</summary>
    private void CleanShadowRoot()
    {
        try
        {
            foreach (var nameDir in Directory.GetDirectories(_shadowRoot))
            {
                foreach (var loadDir in Directory.GetDirectories(nameDir))
                {
                    var leaf = Path.GetFileName(loadDir);
                    var sep = leaf.IndexOf('_');
                    if (sep > 0 && int.TryParse(leaf.AsSpan(0, sep), out var pid) && (pid == Environment.ProcessId || IsProcessAlive(pid))) continue;
                    PathUtil.TryDeleteDirectory(loadDir);
                }
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(nameDir).Any()) Directory.Delete(nameDir);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Shadow cleanup skipped: {Error}", ex.Message);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    // ------------------------------------------------------------------ helpers

    private static Type FindPluginType(Assembly asm)
    {
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
            if (types.Length == 0)
                throw new InvalidOperationException($"Cannot load types of {asm.GetName().Name}: {ex.LoaderExceptions.FirstOrDefault()?.Message}", ex);
        }
        var candidates = types
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(INetPiPlugin).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .ToList();
        if (candidates.Count > 1) candidates = candidates.Where(t => t.IsPublic).ToList();
        if (candidates.Count > 1) candidates = candidates.Where(t => t.GetCustomAttribute<NetPiPluginAttribute>() is not null).ToList();
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1)
            throw new InvalidOperationException($"{asm.GetName().Name} contains several plugin classes: {string.Join(", ", candidates.Select(t => t.FullName))}");
        var foreign = types.Any(t => t.GetInterfaces().Any(i => i.FullName == typeof(INetPiPlugin).FullName));
        throw new InvalidOperationException(foreign
            ? $"{asm.GetName().Name} implements INetPiPlugin from a different NetPI.Abstractions (reference it with Private=false so the host's copy is used)"
            : $"No public INetPiPlugin implementation with a parameterless constructor found in {asm.GetName().Name}");
    }

    private static string? VersionOf(Assembly asm)
    {
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3);
    }

    private static string NewVersion() =>
        DateTime.UtcNow.Ticks.ToString("x") + Interlocked.Increment(ref s_versionCounter).ToString("x");

    private bool IsEnabled(PluginEntry e, EnabledSets sets)
    {
        if (sets.Disabled.Contains(e.Id) || sets.Disabled.Contains(e.FolderName)) return false;
        return e.Manifest?.Enabled != false || sets.Forced.Contains(e.Id) || sets.Forced.Contains(e.FolderName);
    }

    private PluginEntry? FindById(string id)
    {
        lock (_gate)
            return _entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                   ?? _entries.FirstOrDefault(e => e.FolderName.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private List<PluginEntry> Snapshot()
    {
        lock (_gate) return [.. _entries];
    }

    private void PublishChanged() =>
        _k.Bus.Publish(new BusEvent { Type = EventTypes.PluginsChanged, Data = new { }, Source = "host" });

    private async Task Guard(Task task, string what)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception ex) { _log.LogError(ex, "Plugin manager: {What} failed", what); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
        _rescan.Dispose();
        _applyEnabled.Dispose();
        foreach (var e in Snapshot()) e.DisposeTimers();
        _shutdown.Cancel();
        await _op.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var e in Snapshot().Where(e => e.Instance is not null)
                         .OrderByDescending(e => e.Order).ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                _log.LogDebug("Stopping plugin {Id}", e.Id);
                await StopInstanceAsync(e, track: false).ConfigureAwait(false);
            }
        }
        finally { _op.Release(); }
    }

    private sealed class EnabledSets
    {
        public required HashSet<string> Disabled { get; init; }
        public required HashSet<string> Forced { get; init; }

        public static EnabledSets Read(ISettings s) => new()
        {
            Disabled = new HashSet<string>(s.Get<List<string>>("plugins.disabled") ?? [], StringComparer.OrdinalIgnoreCase),
            Forced = new HashSet<string>(s.Get<List<string>>("plugins.enabled") ?? [], StringComparer.OrdinalIgnoreCase),
        };
    }
}

/// <summary>Host-side state of one plugin folder.</summary>
internal sealed class PluginEntry
{
    public required string Folder { get; init; }
    public required string FolderName { get; init; }
    public required string Root { get; init; }
    public required string AssemblyFile { get; set; }
    public PluginManifest? Manifest { get; set; }

    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public int Order { get; set; } = 100;
    public string State { get; set; } = "unloaded";
    public string? Error { get; set; }
    public DateTimeOffset? LoadedAt { get; set; }
    public int LoadCount { get; set; }
    public long LoadMs { get; set; }
    public string UiVersion { get; set; } = "0";

    // Current load (all cleared on unload so nothing roots the collectible context).
    public PluginLoadContext? Alc { get; set; }
    public Type? PluginType { get; set; }
    public string? ShadowDir { get; set; }
    public INetPiPlugin? Instance { get; set; }
    public PluginScope? Scope { get; set; }
    public CancellationTokenSource? Stopping { get; set; }

    public WeakReference? LastUnloaded { get; set; }
    public bool? LastUnloadCollected { get; set; }
    public Debouncer? ReloadTimer { get; set; }
    public Debouncer? UiTimer { get; set; }

    public PluginInfo ToInfo(bool enabled) => new()
    {
        Id = Id, Name = Name, Description = Description, Version = Version, Directory = Folder, Assembly = AssemblyFile,
        State = State, Error = Error, LoadedAt = LoadedAt, LoadCount = LoadCount, LoadMs = LoadMs, Order = Order, Enabled = enabled,
    };

    public void DisposeTimers()
    {
        ReloadTimer?.Dispose();
        UiTimer?.Dispose();
    }
}
