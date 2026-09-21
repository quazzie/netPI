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

    /// <summary>
    /// astra-1 P1: the per-host-instance snapshot root
    /// (<c>&lt;runtimeDir&gt;/plugin-cache/&lt;instanceId&gt;/</c>) this host loads from and
    /// owns. Claimed at construction; only the owner may prune inside it.
    /// </summary>
    private readonly string _instanceRoot;

    /// <summary>astra-1 P1: per-process host-instance id (stable build prefix + unique guid suffix).</summary>
    private readonly string _instanceId;

    /// <summary>astra-1 P1: true when this process owns <see cref="_instanceRoot"/> (may prune it).</summary>
    private readonly bool _ownsInstanceRoot;

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
        _instanceId = PluginRuntimeLayout.GetHostInstanceId();
        _instanceRoot = PluginRuntimeLayout.InstanceRoot(options.RuntimeDirectory, _instanceId);
        _ownsInstanceRoot = string.Equals(
            PluginRuntimeLayout.ClaimOwnership(_instanceRoot, _instanceId), _instanceId, StringComparison.Ordinal);
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
    /// astra-1 P1: a discovered plugin and how it should be loaded. A directory
    /// with a <c>current.json</c> pointer is a <c>Published</c> build (its artifact
    /// is validated + snapshotted); a directory with a top-level DLL and no
    /// pointer is a <c>Legacy</c> staged folder (migrated through an allowlist);
    /// a directory with no DLL at all is <c>Invalid</c> (skipped, not an error).
    /// </summary>
    public sealed record PluginSource
    {
        public required string Id { get; init; }
        public required string Directory { get; init; }
        public required SourceKind Kind { get; init; }

        /// <summary>For <see cref="Published"/>: the artifact dir the pointer names.</summary>
        public string? ArtifactDir { get; init; }

        /// <summary>For <see cref="Published"/>: the build id pinned by the pointer.</summary>
        public string? BuildId { get; init; }

        /// <summary>Reason <see cref="Kind"/> is not a loadable source (diagnostics only).</summary>
        public string? Note { get; init; }
    }

    public enum SourceKind { Published, Legacy, Invalid }

    /// <summary>
    /// astra-1 P1 discovery: each subfolder of <c>pluginDirectory</c> that is a
    /// plugin. A folder with <c>current.json</c> is a published plugin; a folder
    /// with a top-level DLL (and no pointer) is a legacy staged folder (still
    /// loadable through the migration path); a folder with no DLL is not a
    /// plugin (skipped) — dependency folders no longer masquerade as candidates
    /// just because they contain a DLL.
    /// </summary>
    public IReadOnlyList<PluginSource> DiscoverPluginSources()
    {
        var dir = _options.PluginDirectory;
        if (!Directory.Exists(dir)) return [];
        var sources = new List<PluginSource>();
        foreach (var sub in Directory.EnumerateDirectories(dir)
                     .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            var id = Path.GetFileName(sub);

            var (pointer, perr) = PluginPublication.LoadPointer(sub);
            if (pointer is not null)
            {
                var artifact = pointer.ArtifactDir;
                var (manifest, merr) = PluginPublication.LoadManifest(artifact);
                if (manifest is null)
                {
                    sources.Add(new PluginSource { Id = id, Directory = sub, Kind = SourceKind.Invalid, Note = merr ?? "missing manifest" });
                    continue;
                }
                var verr = PluginPublication.ValidateArtifact(artifact, manifest);
                if (verr is not null)
                {
                    _logger.LogWarning("Plugin {Id} points to an invalid artifact: {Err}", id, verr);
                    sources.Add(new PluginSource { Id = id, Directory = sub, Kind = SourceKind.Invalid, BuildId = manifest.BuildId, Note = verr });
                    continue;
                }
                sources.Add(new PluginSource { Id = id, Directory = sub, Kind = SourceKind.Published, ArtifactDir = artifact, BuildId = manifest.BuildId });
                continue;
            }

            // No pointer: legacy staged folder (a top-level DLL, not a pointer) or
            // not a plugin at all. A stray current.json that failed to parse is
            // NOT treated as legacy — surfacing it as invalid is safer.
            if (perr is not null && !perr.Contains("legacy"))
            {
                sources.Add(new PluginSource { Id = id, Directory = sub, Kind = SourceKind.Invalid, Note = perr });
                continue;
            }

            var hasDll = Directory.EnumerateFiles(sub, "*.dll", SearchOption.TopDirectoryOnly)
                .Any(f => !string.Equals(Path.GetFileNameWithoutExtension(f),
                    PluginLoadContext.AbstractionsAssemblyName, StringComparison.OrdinalIgnoreCase));
            sources.Add(new PluginSource
            {
                Id = id,
                Directory = sub,
                Kind = hasDll ? SourceKind.Legacy : SourceKind.Invalid,
                Note = hasDll ? null : "no entry assembly (no top-level DLL)"
            });
        }
        return sources;
    }

    // ----------------------------------------------------------------------
    // Load / start
    // ----------------------------------------------------------------------

    /// <summary>
    /// astra-1 P1: resolve a discovered plugin source to the directory a
    /// generation loads from, snapshotting the source's bytes into this
    /// host-instance's per-attempt cache dir. A <c>Published</c> source copies its
    /// (already manifest-validated) artifact; a <c>Legacy</c> source copies its
    /// staged folder through the runtime allowlist (excluding bin/obj/source).
    /// Returns null when there is nothing loadable (an invalid source, or a
    /// legacy folder with no payload). The attempt id is reserved even when the
    /// copy later fails and is never reused.
    /// </summary>
    public string? ResolveLoadDir(PluginSource source, string attemptId)
    {
        return source.Kind switch
        {
            SourceKind.Published => PluginRuntimeLayout.SnapshotArtifact(
                _instanceRoot, source.Id, attemptId, source.ArtifactDir!),
            SourceKind.Legacy => PluginRuntimeLayout.SnapshotLegacy(
                _instanceRoot, source.Id, attemptId, source.Directory),
            _ => null,
        };
    }

    /// <summary>
    /// astra-1 P1: ownership-aware snapshot pruning. Keeps the newest
    /// <c>MaxCachedGenerations</c> per-plugin snapshot dirs under this host
    /// instance. Only runs when this process owns the instance root; a snapshot
    /// that is still locked (its ALC is not finalized) is left for the next
    /// prune — never an error.
    /// </summary>
    private void PruneStaleSnapshots(string pluginId)
    {
        if (!_ownsInstanceRoot) return;
        var pluginRoot = Path.Combine(_instanceRoot, pluginId);
        var removed = PluginRuntimeLayout.PruneSnapshots(pluginRoot, Math.Max(1, _options.MaxCachedGenerations));
        if (removed > 0)
            _logger.LogDebug("Pruned {Count} stale snapshot(s) for {Plugin}", removed, pluginId);
    }

    /// <summary>Load every discovered plugin that is not already loaded. Startup path.</summary>
    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        foreach (var src in DiscoverPluginSources())
        {
            if (src.Kind == SourceKind.Invalid) continue;
            var existing = Get(src.Id);
            if (existing is { State: PluginState.Active or PluginState.Draining or PluginState.Loading or PluginState.Failed })
                continue;
            await LoadGenerationAsync(src, ct: ct);
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
        foreach (var src in DiscoverPluginSources())
        {
            ct.ThrowIfCancellationRequested();
            if (src.Kind == SourceKind.Invalid) continue;
            var existing = Get(src.Id);
            if (existing is { State: PluginState.Active or PluginState.Draining or PluginState.Loading or PluginState.Failed })
                continue;
            var inst = await LoadGenerationAsync(src, ct);
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
            loaded.Add(src.Id);
            _logger.LogInformation("Plugin {Plugin} scanned in from {Dir} (gen {Gen})", src.Id, inst.CacheDirectory, inst.Generation);
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
    public async Task<PluginInstance?> LoadGenerationAsync(PluginSource source, CancellationToken ct = default, Action<string>? errorSink = null)
    {
        var pluginId = source.Id;
        var generation = NextGeneration(pluginId);
        var attemptId = $"netpi-{generation}-{Guid.NewGuid().ToString("n")[..12]}";
        var loadFrom = ResolveLoadDir(source, attemptId);
        if (loadFrom is null)
        {
            var msg = $"no loadable source for '{pluginId}' ({source.Note ?? "nothing to snapshot"})";
            _logger.LogWarning(msg);
            errorSink?.Invoke(msg);
            return null;
        }
        var alc = new PluginLoadContext($"{pluginId}-gen{generation}", loadFrom);
        var instance = new PluginInstance(pluginId, generation)
        {
            SourceDirectory = source.Directory,
            CacheDirectory = loadFrom,
            BuildId = source.Kind == SourceKind.Published ? source.BuildId : "legacy",
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
                _logger.LogWarning("Plugin directory '{Dir}' declares id '{InfoId}' (host uses '{DirId}')", source.Directory, plugin.Info.Id, pluginId);

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
            PruneStaleSnapshots(pluginId);
            _logger.LogInformation("Plugin {Plugin} gen {Gen} active (build {Build})", pluginId, generation, instance.BuildId);
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

        // astra-1 P1: reload re-resolves the CURRENT source (a new pointer may
        // have been published since the previous load) and snapshots it.
        var src = DiscoverPluginSources().FirstOrDefault(s => s.Id == pluginId);
        if (src is null)
        {
            _logger.LogError("Reload of {Plugin} failed: no plugin source found under '{Dir}'", pluginId, _options.PluginDirectory);
            return null;
        }
        if (src.Kind == SourceKind.Invalid)
        {
            _logger.LogError("Reload of {Plugin} failed: source is invalid — {Note}", pluginId, src.Note);
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
        var fresh = await LoadGenerationAsync(src, ct,
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
        string? BuildId,
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
                    p.BuildId,
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

