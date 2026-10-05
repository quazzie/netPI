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
/// <item>Each load shadow-copies the folder (except <c>wwwroot</c>) to <c>&lt;home&gt;/shadow/&lt;name&gt;/&lt;pid&gt;_&lt;n&gt;/</c> and loads it into a
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
    /// <summary>Plugins whose reload waits for <c>plugins.quiet</c> to end (see <see cref="Quiet"/>).</summary>
    private readonly List<string> _deferred = [];
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(PathUtil.Comparer);
    private readonly Debouncer _rescan;
    private readonly Debouncer _applyEnabled;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _shadowRoot;
    /// <summary>Unloaded contexts whose collection is still being watched (see <see cref="WatchCollection"/>); guards itself.</summary>
    private readonly List<PendingUnload> _pendingUnloads = [];
    /// <summary>Whether <see cref="CollectLoopAsync"/> runs (guarded by <see cref="_pendingUnloads"/>).</summary>
    private bool _collecting;
    private volatile bool _disposed;

    public PluginManager(HostKernel kernel, ILogger log)
    {
        _k = kernel;
        _log = log;
        // The copies are code this process loads and runs: they live in the home, which is owner-only on Unix, and not
        // in the system temp folder, where another local user could swap a copy before it is loaded.
        _shadowRoot = Path.Combine(kernel.Paths.Home, "shadow");
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
        await ApplyEnabledStateAsync(ct, retry: enabled ? e : null).ConfigureAwait(false);
    }

    public async Task RescanAsync(CancellationToken ct = default)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        var changed = false;
        var removed = new List<string>();
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
                removed.Add(e.Id);
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
        // A change the watcher did not report (its buffer overflowed, or the folder was swapped in whole): a plugin whose
        // assembly on disk is no longer the one it loaded is reloaded as if the change had been seen.
        foreach (var e in Snapshot())
        {
            if (e.SourceStamp is not { } loaded) continue;   // never loaded here (disabled from the start): nothing to compare
            if (FileStamp.Of(Path.Combine(e.Folder, e.AssemblyFile)) is not { } current || current == loaded) continue;
            _log.LogInformation("Plugin {Id}: {File} changed unseen (watcher events lost or the folder replaced); reloading", e.Id, e.AssemblyFile);
            ScheduleReload(e);
        }
        // A plugin whose folder went away takes its tools with it, so say so like every other removal: otherwise the cause
        // of a vanished tool falls back to unknown and the model is told a tool is gone with nothing to explain it.
        if (removed.Count > 0) PublishReloaded(removed, "removed");
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
        // plugins.quiet switched off: the reloads it held back are the point of switching it off. Off the bus's line
        // (like the timer-driven reloads): the reloads load and start plugins, which must not run inside a settings.changed delivery.
        if (!Quiet() && Deferred().Count > 0) _ = Task.Run(() => Guard(ApplyDeferredAsync(_shutdown.Token), "apply deferred reloads"));
        _applyEnabled.Trigger();
    }

    /// <summary>
    /// <c>plugins.quiet</c>: while on, a plugin reload is recorded and not applied, so nothing moves under a running
    /// chat - no swap, no lost in-memory state, and (with the swap) no tool notices either. Switching it off applies
    /// everything that piled up. The point of the setting is the one thing a worktree cannot do: the running app is
    /// shared, so a reload in it is visible to every session, whoever asked for it.
    /// </summary>
    private bool Quiet()
    {
        try { return _k.Settings.Get("plugins.quiet", Rpc.CoreSettings.QuietDefault); }
        catch { return Rpc.CoreSettings.QuietDefault; }
    }

    /// <summary>The plugin ids whose reload is waiting for quiet to end, oldest first.</summary>
    public IReadOnlyList<string> Deferred()
    {
        lock (_deferred) return [.. _deferred];
    }

    private async Task ApplyDeferredAsync(CancellationToken ct)
    {
        List<string> ids;
        lock (_deferred) { ids = [.. _deferred]; _deferred.Clear(); }
        if (ids.Count == 0) return;
        _log.LogInformation("Applying {Count} deferred plugin reload(s): {Ids}", ids.Count, string.Join(", ", ids));
        foreach (var id in ids)
        {
            var e = FindById(id);
            if (e is null) continue;
            try { await ReloadEntryAsync(e, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Reloading {Plugin} failed", id); }
        }
        PublishChanged();
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
        var enabled = new List<PluginEntry>();
        foreach (var e in list)
        {
            if (!IsEnabled(e, sets))
            {
                e.State = "disabled";
                continue;
            }
            enabled.Add(e);
        }
        // Assembly loads are independent per plugin (its own shadow copy and load context), so they run in parallel,
        // each on the pool (the copy, the load and the type scan have no await to hand the caller back before them);
        // the start below stays in (Order, Name) sequence.
        var loads = await Task.WhenAll(enabled.Select(e => Task.Run(async () => (Entry: e, Ok: await LoadAssemblyAsync(e).ConfigureAwait(false)))));
        var loaded = loads.Where(p => p.Ok).Select(p => p.Entry).ToList();
        foreach (var e in loaded.OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsEnabled(e, sets)) // the real id (from the attribute) may be disabled
            {
                await UnloadContextAsync(e, CurrentLoad(e), track: true).ConfigureAwait(false);
                e.State = "disabled";
                continue;
            }
            if (RunningTwin(e) is { } twin)
            {
                await UnloadContextAsync(e, CurrentLoad(e), track: true).ConfigureAwait(false);
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
                // what this attempt copies: a rescan compares the file on disk against it (see RescanAsync)
                e.SourceStamp = FileStamp.Of(Path.Combine(e.Folder, e.AssemblyFile));
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
            await UnloadContextAsync(e, CurrentLoad(e), track: true).ConfigureAwait(false);
            e.State = "failed";
            e.Error = message;
        }
    }

    /// <summary>StopAsync (10 s) → cancel Stopping → dispose registrations (reverse) → unload the context.</summary>
    private async Task StopInstanceAsync(PluginEntry e, bool track)
    {
        var load = TakeLoad(e);
        if (load.Instance is not null) await StopQuietlyAsync(load.Instance, e.Id).ConfigureAwait(false);
        Cancel(load.Stopping, e.Id);
        load.Scope?.DisposeAll();
        load.Stopping?.Dispose();
        await UnloadContextAsync(e, load, track).ConfigureAwait(false);
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

    /// <summary>
    /// Unload a load context and watch whether it is collected. Works off <paramref name="load"/> rather than the
    /// entry's fields, so a reload can unload the version it replaced while the entry already points at the new one.
    /// </summary>
    private async Task UnloadContextAsync(PluginEntry e, Load load, bool track)
    {
        if (load.Alc is null) return;
        var shadow = load.ShadowDir;
        // The entry's own load (a start that failed, a disabled or duplicate id): nothing of it may stay on the entry,
        // the context least of all - a reference there would keep it alive through every collection.
        if (ReferenceEquals(e.Alc, load.Alc)) ClearLoad(e);
        // Deliver queued events (they may carry plugin payloads) before snapshotting the ring buffer.
        try { await _k.Bus.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        _k.Bus.DetachCollectible();
        _k.Services.ClearCache();
        var weak = UnloadAndTrack(load);
        ClearJsonCaches();
        e.LastUnloaded = weak;
        e.LastUnloadCollected = null;
        if (track) WatchCollection(e, weak, shadow);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference UnloadAndTrack(Load load)
    {
        var alc = load.Alc!;
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

    /// <summary>How long an unloaded context is given to be collected before it is reported as leaking.</summary>
    private const int CollectBudgetMs = 18_000;

    /// <summary>
    /// Watch whether an unloaded context is collected. One loop serves every unload in flight: each round is a forced
    /// collection and a check of them all, so a publish that reloads eight plugins costs one series of collections and
    /// not eight. The rounds back off (the first after 20 ms, then every 200 ms, after five of those every 2 s) within
    /// <see cref="CollectBudgetMs"/> per unload; the outcome is recorded on the entry and published (<see cref="UnloadedEvent"/>).
    /// </summary>
    private void WatchCollection(PluginEntry e, WeakReference weak, string? shadow)
    {
        lock (_pendingUnloads)
        {
            _pendingUnloads.Add(new PendingUnload(e, weak, shadow));
            if (_collecting) return;
            _collecting = true;
        }
        _ = Task.Run(CollectLoopAsync);
    }

    private async Task CollectLoopAsync()
    {
        try
        {
            while (true)
            {
                int delay;
                lock (_pendingUnloads)
                {
                    if (_disposed) _pendingUnloads.Clear();
                    if (_pendingUnloads.Count == 0) { _collecting = false; return; }
                    // the newest unload sets the pace: its first checks come quickly, the rest are spaced out
                    delay = _pendingUnloads.Min(p => p.Rounds == 0 ? 20 : p.Rounds <= 5 ? 200 : 2000);
                }
                await Task.Delay(delay).ConfigureAwait(false);
                _k.Bus.DetachCollectible();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var done = new List<PendingUnload>();
                lock (_pendingUnloads)
                {
                    var now = Environment.TickCount64;
                    foreach (var p in _pendingUnloads)
                    {
                        p.Rounds++;
                        if (!p.Weak.IsAlive || now - p.Since >= CollectBudgetMs) done.Add(p);
                    }
                    foreach (var p in done) _pendingUnloads.Remove(p);
                }
                foreach (var p in done) ReportUnload(p);
            }
        }
        catch (Exception ex)
        {
            // the loop must not die with its flag set, or no unload would be checked again
            lock (_pendingUnloads) { _pendingUnloads.Clear(); _collecting = false; }
            _log.LogError(ex, "Plugin manager: the unload check failed");
        }
    }

    private void ReportUnload(PendingUnload p)
    {
        var e = p.Entry;
        var collected = !p.Weak.IsAlive;
        if (ReferenceEquals(e.LastUnloaded, p.Weak)) e.LastUnloadCollected = collected;
        _k.Bus.Publish(new BusEvent { Type = UnloadedEvent, Data = new { id = e.Id, collected }, Source = "host" });
        if (collected)
        {
            _log.LogDebug("Plugin {Id}: previous load context unloaded", e.Id);
            PathUtil.TryDeleteDirectory(p.Shadow);
        }
        else
        {
            _log.LogWarning("Plugin {Id}: the previous load context is still alive after unload (something still references plugin objects); its memory is not reclaimed", e.Id);
        }
    }

    /// <summary>An unloaded context the collection loop watches: which entry it belonged to, since when, and how many rounds it had.</summary>
    private sealed class PendingUnload(PluginEntry entry, WeakReference weak, string? shadow)
    {
        public PluginEntry Entry { get; } = entry;
        public WeakReference Weak { get; } = weak;
        public string? Shadow { get; } = shadow;
        public long Since { get; } = Environment.TickCount64;
        public int Rounds { get; set; }
    }

    /// <summary>
    /// One load of a plugin: its context, its type, its instance and everything it registered. Held as a value so a
    /// reload can start the <em>next</em> load while this one is still serving, then retire this one explicitly
    /// (see <see cref="ReloadEntryAsync"/>).
    /// </summary>
    private sealed record Load(PluginLoadContext? Alc, Type? PluginType, string? ShadowDir, INetPiPlugin? Instance,
        PluginScope? Scope, CancellationTokenSource? Stopping)
    {
        public static readonly Load None = new(null, null, null, null, null, null);
        public bool Live => Alc is not null || Instance is not null;
    }

    private static Load TakeLoad(PluginEntry e)
    {
        var load = CurrentLoad(e);
        ClearLoad(e);
        return load;
    }

    /// <summary>The load the entry currently points at (not taken: the entry keeps pointing at it).</summary>
    private static Load CurrentLoad(PluginEntry e) =>
        new(e.Alc, e.PluginType, e.ShadowDir, e.Instance, e.Scope, e.Stopping);

    private static void RestoreLoad(PluginEntry e, Load load)
    {
        e.Alc = load.Alc; e.PluginType = load.PluginType; e.ShadowDir = load.ShadowDir;
        e.Instance = load.Instance; e.Scope = load.Scope; e.Stopping = load.Stopping;
    }

    private async Task ReloadEntryAsync(PluginEntry e, CancellationToken ct)
    {
        // plugins.quiet: record it and leave the running version alone. Nothing moves under a chat; switching quiet off
        // applies everything that piled up (OnSettingsChanged).
        if (Quiet())
        {
            lock (_deferred) if (!_deferred.Contains(e.Id)) _deferred.Add(e.Id);
            _log.LogInformation("Plugin {Id}: reload deferred (plugins.quiet) - the running version keeps serving", e.Id);
            PublishReloaded([e.Id], "deferred");
            PublishChanged();
            return;
        }
        await _op.WaitAsync(ct).ConfigureAwait(false);
        var swapped = false;
        var attempted = false;   // a load was tried: only then is a reload, or its failure, announced
        string? kind = null;     // what to announce otherwise: "disabled" when a running version was stopped
        var changed = false;
        try
        {
            if (_disposed) return;
            lock (_gate) if (!_entries.Contains(e)) return;   // a rescan removed it meanwhile
            changed = true;
            var manifestFile = Path.Combine(e.Folder, "plugin.json");
            e.Manifest = File.Exists(manifestFile) ? PluginManifest.TryLoad(manifestFile, _log) : null;
            e.AssemblyFile = AssemblyFileName(e.Manifest, e.FolderName);
            if (!IsEnabled(e, EnabledSets.Read(_k.Settings)))
            {
                // plugin.json says enabled:false (or the settings do): a plugin that is off is stopped, not hot-reloaded
                if (e.Instance is not null) kind = "disabled";
                _log.LogInformation("Plugin {Id}: off by its plugin.json or the settings; {What}", e.Id, kind is null ? "not loading it" : "stopping the running version");
                await StopInstanceAsync(e, track: true).ConfigureAwait(false);
                e.State = "disabled";
                e.Error = null;
                return;
            }
            if (e.Instance is null && RunningTwin(e) is { } twin)
            {
                // its id is taken: a second instance would answer the same RPCs and tools (LoadAndStartAsync refuses the same)
                e.State = "failed";
                e.Error = $"Duplicate plugin id '{e.Id}' (already loaded from {twin.Folder})";
                _log.LogWarning("Plugin in {Folder}: {Error}", e.Folder, e.Error);
                return;
            }
            if (!File.Exists(Path.Combine(e.Folder, e.AssemblyFile)))
            {
                // nothing to load: the running version keeps serving, and when the folder is gone the rescan the same
                // change triggers removes the plugin (and says so)
                e.Error = $"{e.AssemblyFile} not found";
                _log.LogWarning("Plugin {Id}: {Error}; still on the running version", e.Id, e.Error);
                return;
            }
            _log.LogInformation("Reloading plugin {Id}", e.Id);
            attempted = true;

            // A swap, not a restart. The old load keeps serving while the next one starts: a registration of the same
            // name and priority takes over the moment the new instance makes it (ties go to the latest), so a tool is
            // never absent, and a chat that calls a model in the middle sees no change at all - the "tools changed"
            // notice does not fire for a reload. The old registrations go last, and disposing them removes only the
            // old ones. A new version that fails to load or start therefore leaves the running one alone.
            var retired = TakeLoad(e);
            e.State = "loading";
            e.Error = null;
            try
            {
                if (await LoadAssemblyAsync(e).ConfigureAwait(false))
                {
                    await StartInstanceAsync(e, ct).ConfigureAwait(false);
                    swapped = e.State == "running";
                }
            }
            catch (Exception ex)   // a swap must not take the running plugin down with it
            {
                _log.LogError(ex, "Plugin {Id}: the new version could not be loaded", e.Id);
                e.Error = JsonUtil.Unwrap(ex).Message;
                e.State = "unloaded";
            }

            if (swapped)
            {
                // the new load is serving: now the old one's registrations go, and with them its context
                await RetireAsync(e, retired, track: true).ConfigureAwait(false);
            }
            else
            {
                // Nothing took over, and the failed attempt already cleaned up after itself (its own scope and
                // context). Put the running load back exactly as it was and leave it alone.
                var why = e.Error ?? "the new version did not start";
                ClearLoad(e);
                RestoreLoad(e, retired);
                e.State = retired.Instance is not null ? "running" : "stopped";
                _log.LogWarning("Plugin {Id}: still on the running version ({Why})", e.Id, why);
            }
        }
        finally
        {
            _op.Release();
            if (attempted) kind = swapped ? "reload" : "reload-failed";
            if (kind is not null) PublishReloaded([e.Id], kind);
            if (changed) PublishChanged();
        }
    }

    private static void ClearLoad(PluginEntry e)
    {
        e.Alc = null; e.PluginType = null; e.ShadowDir = null; e.Instance = null; e.Scope = null; e.Stopping = null;
    }

    /// <summary>
    /// Stop, unregister and unload a load that has been replaced: StopAsync → cancel Stopping → dispose its
    /// registrations → unload its context. The registrations go last, because they are what the tools are: until this
    /// runs, the replaced version's tools are still callable.
    /// </summary>
    private async Task RetireAsync(PluginEntry e, Load load, bool track)
    {
        if (!load.Live) { load.Stopping?.Dispose(); return; }
        if (load.Instance is not null) await StopQuietlyAsync(load.Instance, e.Id).ConfigureAwait(false);
        Cancel(load.Stopping, e.Id);
        load.Scope?.DisposeAll();
        load.Stopping?.Dispose();
        await UnloadContextAsync(e, load, track).ConfigureAwait(false);
    }

    /// <summary>
    /// Stop what is switched off and start what is switched on. <paramref name="retry"/> is the plugin an explicit
    /// <see cref="SetEnabledAsync"/> switched on: if it failed or stopped while on, that is the one way to start it again.
    /// </summary>
    private async Task ApplyEnabledStateAsync(CancellationToken ct, PluginEntry? retry = null)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        var changed = false;
        try
        {
            if (_disposed) return;
            var sets = EnabledSets.Read(_k.Settings);
            var stopped = new List<string>();
            foreach (var e in Snapshot().Where(e => e.Instance is not null).OrderByDescending(e => e.Order))
            {
                if (IsEnabled(e, sets)) continue;
                _log.LogInformation("Disabling plugin {Id}", e.Id);
                await StopInstanceAsync(e, track: true).ConfigureAwait(false);
                e.State = "disabled";
                stopped.Add(e.Id);
                changed = true;
            }
            // One that failed (or stopped) and is switched off is off like any other: nothing to stop, but its switch
            // must say so, and switching it on again is what starts it.
            foreach (var e in Snapshot().Where(e => e.State is ("failed" or "stopped") && !IsEnabled(e, sets)))
            {
                e.State = "disabled";
                e.Error = null;
                changed = true;
            }
            // Switching on starts what is off. A failed or stopped one that is on already is started again only when
            // asked for by name, not on every other change of a plugins.* setting.
            var toStart = Snapshot().Where(e => IsEnabled(e, sets) &&
                (e.State == "disabled" || (ReferenceEquals(e, retry) && e.State is ("failed" or "stopped")))).ToList();
            if (toStart.Count > 0)
            {
                await LoadAndStartAsync(toStart, ct).ConfigureAwait(false);
                changed = true;
            }
            if (stopped.Count > 0) PublishReloaded(stopped, "disabled");
            if (toStart.Count > 0) PublishReloaded(toStart.Select(e => e.Id).ToList(), "enabled");
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
                        // an overflow lost events: the rescan finds folders that came or went, and reloads every plugin
                        // whose assembly is no longer the one it loaded (RescanAsync)
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
            // ??= is not atomic: two events for one plugin racing would each create a timer, and the loser would
            // keep firing forever (nothing puts the field back to null). The entry guards its own timers.
            Debouncer timer;
            lock (e) timer = e.UiTimer ??= new Debouncer(TimeSpan.FromMilliseconds(300), () => BumpUiVersion(e));
            timer.Trigger();
            return;
        }
        var file = parts[^1];
        var ext = Path.GetExtension(file);
        if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("plugin.json", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            ScheduleReload(e);
    }

    /// <summary>Reload the plugin once its files have been quiet for <see cref="ReloadDelay"/>: one timer per entry, the entry guarding it.</summary>
    private void ScheduleReload(PluginEntry e)
    {
        // Same race as the UI timer in OnFileEvent, same guard.
        Debouncer timer;
        lock (e) timer = e.ReloadTimer ??= new Debouncer(ReloadDelay, () => _ = Guard(ReloadEntryAsync(e, _shutdown.Token), "reload " + e.Id));
        timer.Trigger();
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

    /// <summary>The entry with this id (else this folder name). Of two folders under one id, the running one: it is the one that answers.</summary>
    private PluginEntry? FindById(string id)
    {
        lock (_gate)
        {
            var byId = _entries.Where(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            return byId.FirstOrDefault(e => e.Instance is not null) ?? byId.FirstOrDefault()
                   ?? _entries.FirstOrDefault(e => e.FolderName.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Another entry running under this one's id: two folders with one plugin id, of which only one may run.</summary>
    private PluginEntry? RunningTwin(PluginEntry e) =>
        Snapshot().FirstOrDefault(o => !ReferenceEquals(o, e) && o.Instance is not null && o.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase));

    private List<PluginEntry> Snapshot()
    {
        lock (_gate) return [.. _entries];
    }

    private void PublishChanged() =>
        _k.Bus.Publish(new BusEvent { Type = EventTypes.PluginsChanged, Data = new { }, Source = "host" });

    /// <summary>What changed, for the plugins that keep state a reload touches (a chat's tools, e.g.).</summary>
    private void PublishReloaded(IReadOnlyList<string> ids, string kind) =>
        _k.Bus.Publish(new BusEvent { Type = EventTypes.PluginsReloaded, Data = new { ids, kind }, Source = "host" });

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
    /// <summary>The main assembly on disk as it was when last copied for a load; a rescan reloads the plugin when it differs.</summary>
    public FileStamp? SourceStamp { get; set; }

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

/// <summary>Size and last write of a file: enough to tell a rebuilt plugin assembly from the one that was loaded.</summary>
internal readonly record struct FileStamp(DateTime LastWriteUtc, long Length)
{
    /// <summary>The file's stamp, or null when there is no such file (or it cannot be read).</summary>
    public static FileStamp? Of(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch
        {
            return null;
        }
    }
}
