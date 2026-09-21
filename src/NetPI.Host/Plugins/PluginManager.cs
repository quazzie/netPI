using System.Reflection;
using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Host.Config;
using NetPI.Host.Events;
using NetPI.Host.Services;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Plugins;

/// <summary>Options controlling plugin discovery and loading.</summary>
public sealed class PluginManagerOptions
{
    /// <summary>Directory containing plugin folders (each folder = one plugin).</summary>
    public string PluginDirectory { get; set; } = "./plugins";

    /// <summary>Runtime directory (~/.netpi by default) for the plugin cache.</summary>
    public string RuntimeDirectory { get; set; } = ConfigService.DefaultRuntimeDirectory();

    /// <summary>
    /// How many per-generation snapshot directories are kept per plugin in the
    /// plugin cache. Older snapshots are deleted when a newer generation of the
    /// same plugin is staged (a snapshot whose ALC is not finalized yet is left
    /// alone and retried on the next stage of that plugin).
    /// </summary>
    public int MaxCachedGenerations { get; set; } = 2;

    /// <summary>Reload gate for AgentIdle-policy plugins (phase 1: pluggable, agent runtime not present yet).</summary>
    public Func<Task<bool>>? AgentIdleGate { get; set; }

    /// <summary>Plugins matching these ids use the AgentIdle reload policy (PLAN §6).</summary>
    public IReadOnlySet<string> AgentIdlePlugins { get; set; } =
        new HashSet<string>(["netPI.Agent", "netPI.Web"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Timeout for Stop/Unload of a generation. Default 30s.</summary>
    public TimeSpan UnloadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Poll interval while waiting for leases to drain.</summary>
    public TimeSpan DrainPollInterval { get; set; } = TimeSpan.FromMilliseconds(10);
}

/// <summary>
/// Plugin discovery, generation management, reload coordination, and ALC leak
/// detection (PLAN §5/§6/§7).
/// </summary>
public sealed class PluginManager
{
    private readonly object _gate = new();
    private readonly PluginManagerOptions _options;
    private readonly EventBus _bus;
    private readonly ServiceRegistry _registry;
    private readonly CommandRegistry _commands;
    private readonly WebPanelRegistry _webPanels;
    private readonly IConfigService _config;
    private readonly ILogger _logger;

    private readonly Dictionary<string, PluginInstance> _current = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _generation = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Label, WeakReference ALC)> _unloadedAlocs = [];

    public PluginManager(
        PluginManagerOptions options,
        EventBus bus,
        ServiceRegistry registry,
        CommandRegistry commands,
        IConfigService config,
        ILogger logger)
        : this(options, bus, registry, commands, new WebPanelRegistry(), config, logger)
    {
    }

    public PluginManager(
        PluginManagerOptions options,
        EventBus bus,
        ServiceRegistry registry,
        CommandRegistry commands,
        WebPanelRegistry webPanels,
        IConfigService config,
        ILogger logger)
    {
        _options = options;
        _bus = bus;
        _registry = registry;
        _commands = commands;
        _webPanels = webPanels;
        _config = config;
        _logger = logger;
    }

    public IEventBus Events => _bus;
    public IServiceRegistry Services => _registry;

    /// <summary>Reload gate for AgentIdle plugins; overridable at runtime (tests / future agent plugin).</summary>
    public Func<Task<bool>> AgentIdleGate
    {
        get { lock (_gate) return _options.AgentIdleGate ?? (() => Task.FromResult(true)); }
        set { lock (_gate) _options.AgentIdleGate = value; }
    }

    // ----------------------------------------------------------------------
    // Discovery
    // ----------------------------------------------------------------------

    /// <summary>
    /// Discover plugin directories: each subfolder of <c>pluginDirectory</c>
    /// that contains at least one <c>*.dll</c> is a plugin candidate.
    /// </summary>
    public IReadOnlyList<string> DiscoverPluginDirectories()
    {
        var dir = _options.PluginDirectory;
        if (!Directory.Exists(dir)) return [];
        return Directory
            .EnumerateDirectories(dir)
            .Where(d => Directory.EnumerateFiles(d, "*.dll", SearchOption.TopDirectoryOnly).Any())
            .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
            .ToList();
    }

    // ----------------------------------------------------------------------
    // Load / start
    // ----------------------------------------------------------------------

    /// <summary>
    /// Copy the staged plugin folder into an immutable per-generation snapshot,
    /// ~/.netpi/plugin-cache/&lt;plugin&gt;/&lt;generation&gt;/, and return the snapshot
    /// path (the directory plugins are actually loaded from).
    ///
    /// The host ALWAYS loads from a snapshot, never from the staged folder
    /// itself. That is what makes staging hot-swappable: while generation N is
    /// mapped from its snapshot (on Windows the file stays locked until the ALC
    /// is unloaded and finalized), a new build may freely overwrite the staged
    /// folder; the next reload simply snapshots generation N+1. A generation's
    /// snapshot is never written again after staging.
    /// </summary>
    public string StagePluginFiles(string sourceDir, int generation)
    {
        var pluginName = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar));
        var pluginCacheRoot = Path.Combine(_options.RuntimeDirectory, "plugin-cache", pluginName);
        var genDir = Path.Combine(pluginCacheRoot, generation.ToString());

        // A stale snapshot with this exact number may linger from a previous
        // host run; the new snapshot fully replaces it (snapshots are only ever
        // written during staging, never after).
        if (Directory.Exists(genDir)) Directory.Delete(genDir, recursive: true);
        Directory.CreateDirectory(genDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(genDir, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        SweepStaleGenerations(pluginCacheRoot, generation);
        return genDir;
    }

    /// <summary>
    /// Keep only the <c>MaxCachedGenerations</c> most recent snapshot directories
    /// per plugin. Snapshots with a number &gt;= the just-staged generation are
    /// never deleted (an in-flight reload may still be mapping one). Best-effort:
    /// a directory that is still locked (its ALC has not been finalized) or that
    /// cannot be removed for any other reason is left alone and retried on the
    /// next staging of that plugin.
    /// </summary>
    private void SweepStaleGenerations(string pluginCacheRoot, int currentGeneration)
    {
        if (!Directory.Exists(pluginCacheRoot)) return;
        var keep = Math.Max(1, currentGeneration - Math.Max(1, _options.MaxCachedGenerations) + 1);
        foreach (var dir in Directory.EnumerateDirectories(pluginCacheRoot)
                     .Where(d => int.TryParse(Path.GetFileName(d), out _))
                     .OrderByDescending(d => int.Parse(Path.GetFileName(d))))
        {
            var g = int.Parse(Path.GetFileName(dir));
            if (g >= currentGeneration || g >= keep) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch { /* ALC still alive or IO blip: retried on the next stage */ }
        }
    }

    /// <summary>Load every discovered plugin that is not already loaded. Startup path.</summary>
    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        foreach (var dir in DiscoverPluginDirectories())
        {
            var id = Path.GetFileName(dir);
            var existing = Get(id);
            if (existing is { State: PluginState.Active or PluginState.Draining or PluginState.Loading or PluginState.Failed })
                continue;
            await LoadGenerationAsync(id, dir, ct: ct);
        }
    }

    /// <summary>
    /// PLAN §50: discover plugins staged on disk after startup (a new folder
    /// dropped into plugins/). Existing ids are left alone — they keep their
    /// live generation (reload a specific id to swap its bytes); only ids the
    /// host has never seen are loaded, so a scan can never unload anything a
    /// connection is talking to (including the web surface itself). Returns the
    /// ids of the plugins that were newly loaded and started.
    /// </summary>
    public async Task<IReadOnlyList<string>> ScanAsync(CancellationToken ct = default)
    {
        var loaded = new List<string>();
        foreach (var dir in DiscoverPluginDirectories())
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(dir);
            var existing = Get(id);
            if (existing is { State: PluginState.Active or PluginState.Draining or PluginState.Loading or PluginState.Failed })
                continue;
            var inst = await LoadGenerationAsync(id, dir, ct);
            if (inst is null) continue;
            try
            {
                await inst.Plugin!.StartAsync(ct);
                _logger.LogInformation("Plugin {Plugin} started (gen {Gen})", inst.PluginId, inst.Generation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {Plugin} StartAsync failed (gen {Gen})", inst.PluginId, inst.Generation);
                inst.LastError = ex.Message;
                inst.State = PluginState.Failed;
            }
            loaded.Add(id);
            _logger.LogInformation("Plugin {Plugin} scanned in from {Dir} (gen {Gen})", id, inst.CacheDirectory, inst.Generation);
        }
        return loaded;
    }

    /// <summary>Start every plugin that loaded successfully.</summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        var active = CurrentSnapshots().Where(p => p.State == PluginState.Active).ToList();
        foreach (var p in active)
        {
            try
            {
                await p.Plugin!.StartAsync(ct);
                _logger.LogInformation("Plugin {Plugin} started (gen {Gen})", p.PluginId, p.Generation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {Plugin} StartAsync failed (gen {Gen})", p.PluginId, p.Generation);
                p.LastError = ex.Message;
                p.State = PluginState.Failed;
            }
        }
    }

    /// <summary>
    /// Load one plugin generation. Returns the loaded instance, or null on failure
    /// (a previous generation remains Active when there is one, otherwise the
    /// plugin is marked Failed).
    /// </summary>
    public async Task<PluginInstance?> LoadGenerationAsync(string pluginId, string sourceDir, CancellationToken ct = default, Action<string>? errorSink = null)
    {
        var generation = NextGeneration(pluginId);
        var loadFrom = StagePluginFiles(sourceDir, generation);
        var alc = new PluginLoadContext($"{pluginId}-gen{generation}", loadFrom);
        var instance = new PluginInstance(pluginId, generation)
        {
            SourceDirectory = sourceDir,
            CacheDirectory = loadFrom,
            LoadContext = alc,
            Policy = _options.AgentIdlePlugins.Contains(pluginId) ? ReloadPolicy.AgentIdle : ReloadPolicy.PluginIdle
        };

        instance.State = PluginState.Loading;
        _logger.LogInformation("Loading {Plugin} gen {Gen} from {Dir}", pluginId, generation, loadFrom);

        try
        {
            var plugin = FindPluginInstance(alc, loadFrom);
            if (plugin is null)
                throw new PluginLoadException(pluginId, $"no INetPiPlugin implementation found in '{loadFrom}'");
            instance.Plugin = plugin;

            instance.Info = plugin.Info;
            if (plugin.Info.Id != pluginId)
                _logger.LogWarning("Plugin directory '{Dir}' declares id '{InfoId}' (host uses '{DirId}')", Path.GetFileName(sourceDir), plugin.Info.Id, pluginId);

            ServiceRegistry.ServiceOwner.Current = instance;
            try
            {
                instance.Commands = new ScopedCommands(_commands);
                instance.WebPanels = new ScopedWebPanels(_webPanels);
                var context = new PluginContext(
                    instance,
                    new PluginContextServices(_registry, instance),
                    new PluginContextEvents(_bus, instance),
                    _config.GetRaw(pluginId),
                    new PluginLogger(pluginId, _logger),
                    instance.Commands,
                    instance.WebPanels);
                await plugin.LoadAsync(context, ct);
            }
            finally
            {
                ServiceRegistry.ServiceOwner.Current = null;
            }


            RegisterInTables(instance);
            instance.State = PluginState.Active;
            _logger.LogInformation("Plugin {Plugin} gen {Gen} active", pluginId, generation);
            return instance;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading {Plugin} gen {Gen} failed — previous generation (if any) remains active", pluginId, generation);
            DisposeUnloadedGeneration(instance);
            instance.State = PluginState.Failed;
            errorSink?.Invoke(ex.Message);
            // Only record the error when this generation owns the plugin's
            // slot in the table (a failed reload of a live plugin leaves the
            // old generation active — its status must not be polluted).
            if (ReferenceEquals(Get(pluginId), instance))
                instance.LastError = ex.Message;
            else if (Get(pluginId) is null)
            {
                // Startup failure: nothing to keep running — surface the
                // failed generation in the status table (PLAN §46).
                instance.LastError = ex.Message;
                lock (_gate) _current[pluginId] = instance;
            }
            // best-effort: nothing to preserve (previous generation, if any, remains Active)
            return null;
        }
    }

    /// <summary>
    /// Pre-loads native (unmanaged) libraries that ship inside the plugin
    /// directory (e.g. <c>e_sqlite3.dll</c> for SQLitePCLRaw). P/Invoke
    /// <c>DllImport</c> probes the process default search path, which does
    /// not include collectible plugin ALCs; pre-loading via the default ALC
    /// makes the DllImport resolve. Idempotent and best-effort.
    /// </summary>
    private static void PreloadNativeAssets(string pluginDir)
    {
        // Recursively collect candidate native libraries under the plugin
        // directory (runtimes/<rid>/native/*.dll, top-level *.so / *.dylib,
        // or any *.dll that happens to be native). NativeLibrary.TryLoad on a
        // managed dll simply returns false, so this is safe.
        var candidates = new List<string>();
        void Collect(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(f);
        }

        foreach (var runtimesDir in Directory.EnumerateDirectories(pluginDir, "runtimes"))
            Collect(runtimesDir);

        // Top-level native libraries (no runtimes/ nesting).
        CollectTopLevelOnly(pluginDir, candidates);

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { System.Runtime.InteropServices.NativeLibrary.Load(path); }


            catch { /* best-effort; the runtime may load it lazily */ }
        }
    }

    private static void CollectTopLevelOnly(string pluginDir, List<string> candidates)
    {
        foreach (var f in Directory.EnumerateFiles(pluginDir, "*", SearchOption.TopDirectoryOnly))
            if (f.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                candidates.Add(f);
    }
    private INetPiPlugin? FindPluginInstance(PluginLoadContext alc, string pluginDir)
    {
        // Explicitly load every local assembly into the collectible ALC so it
        // shows up in alc.Assemblies (a fresh collectible ALC starts empty of
        // plugin assemblies).
        var assemblies = new List<Assembly>();
        PreloadNativeAssets(pluginDir);

        foreach (var dll in Directory.EnumerateFiles(pluginDir, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(f => !string.Equals(Path.GetFileNameWithoutExtension(f),
                PluginLoadContext.AbstractionsAssemblyName, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                assemblies.Add(alc.LoadFromAssemblyPath(dll));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load '{Dll}' into plugin context", dll);
            }
        }

        // Pick the assembly that actually implements INetPiPlugin (the
        // plugin's own assembly), not the alphabetically-first package dll.
        INetPiPlugin? found = null;
        foreach (var asm in assemblies
            .Where(a => !a.IsDynamic && a.GetName().Name is not null)
            .Where(a => a.IsCollectible))
        {
            try
            {
                var types = asm.GetTypes();
                var impls = types.Where(t => t is { IsClass: true, IsAbstract: false } && typeof(INetPiPlugin).IsAssignableFrom(t)).ToList();
                if (impls.Count == 0) continue;
                if (found is not null)
                    _logger.LogWarning("Multiple INetPiPlugin implementations found across assemblies; using first");
                found = (INetPiPlugin)Activator.CreateInstance(impls[0])!;
            }
            catch (ReflectionTypeLoadException rtle)
            {
                _logger.LogWarning($"could not fully load plugin types in '{asm.GetName().Name}': {rtle.LoaderExceptions?.FirstOrDefault()?.Message}");
            }
        }
        return found;
    }
    // ----------------------------------------------------------------------
    // Reload (PLAN §6)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Reload one plugin: Active → Draining (new leases denied) → wait for
    /// existing leases to drain (and for the AgentIdle gate, if configured) →
    /// StopAsync → remove registrations/subscriptions → UnloadAsync →
    /// ALC.Unload → load new generation → Active.
    ///
    /// Returns the new generation, or null when the reload could not be
    /// started (plugin not loaded / already reloading) or failed (old generation
    /// stays Active).
    /// </summary>
    public async Task<PluginInstance?> ReloadAsync(string pluginId, CancellationToken ct = default)
    {
        var old = Get(pluginId);
        if (old is null)
            throw new ArgumentException($"plugin '{pluginId}' is not loaded", nameof(pluginId));
        if (old.State is not PluginState.Active and not PluginState.Failed)
        {
            _logger.LogWarning("Reload of {Plugin} ignored: state is {State}", pluginId, old.State);
            return null;
        }

        var sourceDir = old.SourceDirectory
            ?? Path.Combine(_options.PluginDirectory, pluginId);
        if (!Directory.Exists(sourceDir))
        {
            _logger.LogError("Reload of {Plugin} failed: source directory '{Dir}' not found", pluginId, sourceDir);
            return null;
        }

        // --- Active → Draining ---
        old.State = PluginState.Draining;
        _logger.LogInformation("{Plugin} draining; new service acquisitions are denied", pluginId);

        // --- wait for existing leases to drain ---
        var drained = await WaitForDrainAsync(old, ct);
        if (!drained)
        {
            old.State = PluginState.Active;
            _logger.LogWarning("Reload of {Plugin} aborted: lease drain timed out, plugin is Active again", pluginId);
            return null;
        }

        // --- AgentIdle gate (PLAN §6 special policies) ---
        if (old.Policy == ReloadPolicy.AgentIdle)
        {
            var gate = AgentIdleGate;
            var gateOk = false;
            using (var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                gateCts.CancelAfter(_options.UnloadTimeout);
                try { gateOk = await gate(); }
                catch { gateOk = false; }
            }
            if (!gateOk)
            {
                old.State = PluginState.Active;
                _logger.LogWarning("Reload of {Plugin} deferred: agent not idle", pluginId);
                return null;
            }
        }

        // --- StopAsync ---
        old.State = PluginState.Unloading;
        try
        {
            using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stopCts.CancelAfter(_options.UnloadTimeout);
            await old.Plugin!.StopAsync(stopCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Plugin} StopAsync failed; proceeding with unload anyway", pluginId);
        }

        // --- host removes registrations + subscriptions (PLAN §7) ---
        _registry.RemoveAllFor(old);
        _bus.RemoveAllFor(old);
        old.Commands?.Unload();
        old.Commands = null;
        old.WebPanels?.Unload();
        old.WebPanels = null;

        // --- UnloadAsync ---
        try
        {
            using var unloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            unloadCts.CancelAfter(_options.UnloadTimeout);
            await old.Plugin!.UnloadAsync(unloadCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Plugin} UnloadAsync failed; proceeding with ALC unload", pluginId);
        }

        UnloadAloc(old);

        // --- load new generation ---
        var fresh = await LoadGenerationAsync(pluginId, sourceDir, ct,
            errorSink: ex =>
            {
                // The old generation was already unloaded, so this plugin has
                // no running copy — surface a clear Failed state in the status
                // table (PLAN §50: "old version remains usable OR clear Failed
                // state"; here there is no usable version left).
                var cur = Get(pluginId);
                if (cur is not null)
                {
                    cur.LastError = ex;
                    cur.State = PluginState.Failed;
                }
            });
        if (fresh is null)
        {
            // Old generation was already unloaded; mark the plugin Failed.
            _logger.LogError("Reload of {Plugin} failed: new generation could not load", pluginId);
            return null;
        }

        // Start the new generation (old one's Start was already stopped).
        try
        {
            await fresh.Plugin!.StartAsync(CancellationToken.None);
            fresh.ClearLastError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Plugin} StartAsync failed after reload", pluginId);
            fresh.LastError = ex.Message;
            fresh.State = PluginState.Failed;
        }

        _logger.LogInformation("{Plugin} reloaded: gen {OldGen} → gen {NewGen}", pluginId, old.Generation, fresh.Generation);
        return fresh;
    }

    /// <summary>Reload every known plugin in dependency-agnostic order (alphabetical).</summary>
    public async Task ReloadAllAsync(CancellationToken ct = default)
    {
        foreach (var id in CurrentSnapshots().Select(p => p.PluginId).OrderBy(x => x, StringComparer.Ordinal))
            await ReloadAsync(id, ct);
    }

    private async Task<bool> WaitForDrainAsync(PluginInstance old, CancellationToken ct)
    {
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainCts.CancelAfter(_options.UnloadTimeout);
        while (old.LeasesHeld > 0)
        {
            try
            {
                await Task.Delay(_options.DrainPollInterval, drainCts.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return true;
    }

    // ----------------------------------------------------------------------
    // Shutdown / unload
    // ----------------------------------------------------------------------

    /// <summary>
    /// Stop and unload every plugin in reverse load order (PLAN §48).
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        var byGen = CurrentSnapshots()
            .Where(p => p.State is PluginState.Active or PluginState.Draining or PluginState.Failed)
            .OrderByDescending(p => p.Generation)
            .ThenByDescending(p => p.PluginId, StringComparer.Ordinal)
            .ToList();

        foreach (var p in byGen)
        {
            try
            {
                if (p.State == PluginState.Active)
                    await p.Plugin!.StopAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Plugin} StopAsync failed during shutdown", p.PluginId);
            }

            _registry.RemoveAllFor(p);
            _bus.RemoveAllFor(p);
            p.Commands?.Unload();
            p.Commands = null;
            p.WebPanels?.Unload();
            p.WebPanels = null;

            try
            {
                await p.Plugin!.UnloadAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Plugin} UnloadAsync failed during shutdown", p.PluginId);
            }

            UnloadAloc(p);
            p.State = PluginState.Unloaded;
            _current.Remove(p.PluginId);
        }
    }

    private void UnloadAloc(PluginInstance instance)
    {
        var alc = instance.LoadContext;
        if (alc is null) return;
        // Drop host-side references so the ALC becomes collectible.
        instance.Plugin = null;
        instance.LoadContext = null;
        instance.LiveLeases.Clear();
        _unloadedAlocs.Add((instance.PluginId + "-gen" + instance.Generation, new WeakReference(alc, true)));
        try
        {
            alc.Unload();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ALC unload for {Plugin} gen {Gen} failed (context may be in use)", instance.PluginId, instance.Generation);
        }
    }

    // ----------------------------------------------------------------------
    // Status / diagnostics (PLAN §7)
    // ----------------------------------------------------------------------

    public sealed record PluginStatus(
        string PluginId,
        string? Name,
        string? Version,
        int Generation,
        PluginState State,
        int ActiveLeases,
        int RegistrationCount,
        int SubscriptionCount,
        bool UnloadPending,
        string? LastError);

    /// <summary>
    /// Status table for the CLI <c>plugins</c> command: one row per plugin with
    /// generation, state, lease counts, and ALC collection state.
    /// </summary>
    public IReadOnlyList<PluginStatus> GetStatus()
    {
        var rows = new List<PluginStatus>();
        lock (_gate)
        {
            foreach (var p in CurrentSnapshots())
            {
                rows.Add(new PluginStatus(
                    p.PluginId,
                    p.Info?.Name,
                    p.Info?.Version,
                    p.Generation,
                    p.State,
                    p.LeasesHeld,
                    p.Registrations.Count,
                    p.Subscriptions.Count,
                    UnloadPending(p),
                    p.LastError));
            }
        }
        return rows;
    }

    /// <summary>
    /// WeakReference-based ALC leak detection (PLAN §7): for every unloaded ALC
    /// we keep a WeakReference and report whether it has been collected.
    /// </summary>
    public IReadOnlyList<(string Label, bool Collected)> UnloadedAlocs()
    {
        lock (_gate)
        {
            var list = new List<(string, bool)>();
            foreach (var (label, wr) in _unloadedAlocs)
                list.Add((label, wr.Target is null));
            return list;
        }
    }


    // ----------------------------------------------------------------------
    // Internals
    // ----------------------------------------------------------------------

    private bool UnloadPending(PluginInstance p)
    {
        if (p.State is not PluginState.Unloading and not PluginState.Draining) return false;
        return true;
    }

    private int NextGeneration(string pluginId)
    {
        lock (_gate)
            return _generation.TryGetValue(pluginId, out var g) ? g + 1 : 1;
    }

    private void RegisterInTables(PluginInstance instance)
    {
        lock (_gate)
        {
            var previous = _current.TryGetValue(instance.PluginId, out var prev) ? prev : null;
            _current[instance.PluginId] = instance;
            _generation[instance.PluginId] = instance.Generation;
            if (previous is { LoadContext: not null })
            {
                // Previous generation was superseded (e.g. startup with a stale one).
                previous.State = PluginState.Unloaded;
                _unloadedAlocs.Add(("superseded-" + previous.PluginId + "-gen" + previous.Generation, new WeakReference(previous.LoadContext, true)));
            }
        }
    }

    public PluginInstance? Get(string pluginId)
    {
        lock (_gate) return _current.TryGetValue(pluginId, out var p) ? p : null;
    }

    public IReadOnlyList<PluginInstance> CurrentSnapshots()
    {
        lock (_gate) return _current.Values.Where(p => p.State is not PluginState.Unloaded).ToList();
    }

    private static void DisposeUnloadedGeneration(PluginInstance instance)
    {
        // Failure path: nothing started; just drop the ALC so it can be GC'd.
        if (instance.LoadContext is not null)
        {
            instance.Plugin = null;
            instance.LoadContext = null;
            instance.LiveLeases.Clear();
            instance.Registrations.Clear();
            instance.Subscriptions.Clear();
            instance.Commands?.Unload();
            instance.Commands = null;
            instance.WebPanels?.Unload();
            instance.WebPanels = null;
        }
    }

    /// <summary>Thrown when a plugin fails to load.</summary>
    public sealed class PluginLoadException(string pluginId, string message, Exception? inner = null)
        : Exception($"plugin '{pluginId}': {message}", inner)
    {
    }
}

