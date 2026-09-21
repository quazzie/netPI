using System.Reflection;
using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Host.Config;
using NetPI.Host.Events;
using NetPI.Host.Services;
using System.Threading.Channels;
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
    public Func<CancellationToken, Task<bool>>? AgentIdleGate { get; set; }

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
    private readonly NativeAssetCache _nativeCache = new();
    private readonly ServiceRegistry _registry;
    private readonly CommandRegistry _commands;
    private readonly WebPanelRegistry _webPanels;
    private readonly IConfigService _config;
    private readonly ILogger _logger;

    // ------------------------------------------------------------------
    // astra-1 P2: lifecycle operation queue.
    // Every state-changing plugin operation (load / scan / reload /
    // reload-all / shutdown) is serialized through ONE host-owned queue so
    // two lifecycle operations can never interleave. Callers await a
    // structured outcome (PluginOperationOutcome); the runner never dies on
    // a single bad operation. Ordinary agent/tool execution stays fully
    // concurrent — only host-side lifecycle mutations are serialized.
    // ------------------------------------------------------------------
    private readonly Channel<LifecycleOp> _lifecycleQueue;
    private readonly HashSet<LifecycleOp> _inflightOps = new();
    private readonly Dictionary<string, string> _unavailablePlugins = new(StringComparer.OrdinalIgnoreCase);
    private Task? _lifecycleRunner;
    private int _shutdownStarted;
    private readonly object _opGate = new();

    /// <summary>true once ShutdownAsync has been queued (new operations are rejected).</summary>
    public bool ShuttingDown => Volatile.Read(ref _shutdownStarted) == 1;

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
        _lifecycleQueue = Channel.CreateUnbounded<LifecycleOp>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _lifecycleRunner = Task.Run(RunLifecycleQueueAsync);
    }

    public IEventBus Events => _bus;
    public IServiceRegistry Services => _registry;

    /// <summary>Reload gate for AgentIdle plugins; overridable at runtime (tests / future agent plugin).</summary>
    /// <summary>
    /// Reload gate for AgentIdle plugins. astra-1 P2: the gate receives the
    /// operation token so a bounded wait aborts on timeout/cancellation.
    /// </summary>
    public Func<CancellationToken, Task<bool>> AgentIdleGate
    {
        get { lock (_gate) return _options.AgentIdleGate ?? (_ => Task.FromResult(true)); }
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

        /// <summary>astra-1 P4: the entry assembly (top-level file name) that must be loaded.</summary>
        public string? EntryAssembly { get; init; }

        /// <summary>astra-1 P4: abstractions build id the artifact was compiled against.</summary>
        public string? AbstractionsBuildId { get; init; }

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
                sources.Add(new PluginSource { Id = id, Directory = sub, Kind = SourceKind.Published, ArtifactDir = artifact, BuildId = manifest.BuildId, EntryAssembly = manifest.EntryAssembly, AbstractionsBuildId = manifest.AbstractionsBuildId });
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
    /// PLAN §50 / astra-1 P2: rescan the plugin directory for folders the host
    /// has never seen and load+start them — queue-serialized so a scan cannot
    /// interleave with a reload. Returns only ids that actually STARTED.
    /// </summary>
    public async Task<IReadOnlyList<string>> ScanAsync(CancellationToken ct = default)
    {
        var op = new LifecycleOp { Kind = LifecycleOpKind.Scan, Ct = ct };
        if (Volatile.Read(ref _shutdownStarted) == 1)
            return Array.Empty<string>();
        Enqueue(op);
        var outcome = await op.Tcs.Task;
        var loaded = new List<string>();
        if (outcome.Error is not null && outcome.Error.StartsWith("loaded: ", StringComparison.Ordinal))
            loaded.AddRange(outcome.Error["loaded: ".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return loaded;
    }

    /// <summary>Start every plugin that loaded successfully.</summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        // astra-1 P2: LoadGenerationAsync no longer marks Active (that now
        // happens only after a successful StartAsync); the bootstrap path
        // therefore starts instances left in Loading by LoadAllAsync.
        var active = CurrentSnapshots().Where(p => p.State is PluginState.Loading or PluginState.Active).ToList();
        foreach (var p in active)
        {
            try
            {
                await p.Plugin!.StartAsync(ct);
                p.StartRan = true;
                p.State = PluginState.Active;
                p.ClearLastError();
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
    public async Task<PluginInstance?> LoadGenerationAsync(PluginSource source, CancellationToken ct = default, Action<string>? errorSink = null, JsonElement ownConfigOverride = default)
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
        var alc = new PluginLoadContext($"{pluginId}-gen{generation}", loadFrom,
            path => _nativeCache.EnsureCached(path));
        var instance = new PluginInstance(pluginId, generation)
        {
            SourceDirectory = source.Directory,
            CacheDirectory = loadFrom,
            SourceArtifactDir = source.Kind == SourceKind.Published ? source.ArtifactDir : source.Directory,
            BuildId = source.Kind == SourceKind.Published ? source.BuildId : "legacy",
            // astra-1 P2: a caller may pin the config the generation is loaded
            // WITH (LKG recovery must use the retained last-known-good config,
            // not whatever the live config has mutated into since).
            SourceConfig = ownConfigOverride.ValueKind == System.Text.Json.JsonValueKind.Undefined
                ? _config.GetRaw(pluginId).DeepClone()
                : ownConfigOverride.DeepClone(),
            LoadContext = alc,
            Policy = _options.AgentIdlePlugins.Contains(pluginId) ? ReloadPolicy.AgentIdle : ReloadPolicy.PluginIdle,
            NativeFiles = NativeAssetCache.NativeRidFiles(loadFrom)
        };

        instance.State = PluginState.Loading;
        _logger.LogInformation("Loading {Plugin} gen {Gen} from {Dir}", pluginId, generation, loadFrom);

        try
        {
            // astra-1 P4: validate the contract BEFORE activation, load the
            // DECLARED entry assembly (not every top-level DLL), and require
            // exactly one implementation in it.
            var nativeCheck = ValidateContract(source);
            if (nativeCheck is not null)
                throw new PluginLoadException(pluginId, nativeCheck);
            var plugin = FindPluginEntry(alc, source, loadFrom);
            if (plugin is null)
                throw new PluginLoadException(pluginId, $"no INetPiPlugin implementation found in the entry assembly of '{loadFrom}'");
            instance.Plugin = plugin;
            instance.NativeFiles = LoadNativeAssets(loadFrom);
            instance.NativeBuildHashes = NativeAssetCache.NativeBuildHashes(loadFrom);

            instance.Info = plugin.Info;
            if (plugin.Info.Id != pluginId)
                _logger.LogWarning("Plugin directory '{Dir}' declares id '{InfoId}' (host uses '{DirId}')", source.Directory, plugin.Info.Id, pluginId);

            // astra-1 P3: no ambient owner state — the scoped context binds
            // the actual generation explicitly, so concurrent callbacks can
            // never attribute work to the wrong generation.
            instance.Commands = new ScopedCommands(_commands);
            instance.WebPanels = new ScopedWebPanels(_webPanels);
            var context = new PluginContext(
                    instance,
                    new PluginContextServices(_registry, instance),
                    new PluginContextEvents(_bus, instance),
                    ownConfigOverride.ValueKind == System.Text.Json.JsonValueKind.Undefined
                        ? _config.GetRaw(pluginId)
                        : ownConfigOverride,
                    new PluginLogger(pluginId, _logger),
                    instance.Commands,
                    instance.WebPanels);
                await plugin.LoadAsync(context, ct);


            RegisterInTables(instance);
            PruneStaleSnapshots(pluginId);
            _logger.LogInformation("Plugin {Plugin} gen {Gen} loaded (build {Build})", pluginId, generation, instance.BuildId);
            return instance;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading {Plugin} gen {Gen} failed — previous generation (if any) remains active", pluginId, generation);
            // astra-1 P3: unified idempotent cleanup (partial Load).
            await CleanupGeneration(instance, "partial load", ct, stop: false);
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
    /// astra-1 P4: load the DECLARED entry assembly (the manifest's
    /// entryAssembly for published builds; the plugin's own DLL by assembly
    /// name for legacy staged folders) and require exactly ONE concrete
    /// INetPiPlugin implementation in it. Dependencies resolve on demand
    /// through the ALC's resolver — the old loop loaded every top-level DLL
    /// and could instantiate several implementations at once.
    /// </summary>
    private INetPiPlugin? FindPluginEntry(PluginLoadContext alc, PluginSource source, string pluginDir)
    {
        string entryPath;
        if (source.EntryAssembly is not null)
        {
            entryPath = Path.GetFullPath(Path.Combine(pluginDir, source.EntryAssembly));
            if (!entryPath.StartsWith(Path.GetFullPath(pluginDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new PluginLoadException(source.Id, $"entry assembly escapes plugin root: '{source.EntryAssembly}'");
            if (!File.Exists(entryPath))
                throw new PluginLoadException(source.Id, $"entry assembly missing: '{source.EntryAssembly}'");
        }
        else
        {
            // Legacy staged folder: the entry is the top-level DLL matching the
            // DISCOVERY folder name (pluginDir is a snapshot of it — the name
            // itself lives on source.Directory). The abstractions copy is the
            // host's and is never loaded from here.
            var abstractionsName = typeof(INetPiPlugin).Assembly.GetName().Name!;
            var folderName = Path.GetFileName(source.Directory);
            // Prefer the DLL named after the discovery folder; otherwise the
            // folder is a RENAMED legacy staging (test fixtures do this) — the
            // entry is then the single non-abstractions top-level DLL.
            var candidates = Directory.EnumerateFiles(pluginDir, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(f => !string.Equals(Path.GetFileNameWithoutExtension(f), abstractionsName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => string.Equals(Path.GetFileNameWithoutExtension(f), folderName, StringComparison.OrdinalIgnoreCase)
                                 ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var named = candidates.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), folderName, StringComparison.OrdinalIgnoreCase));
            var match = candidates.Count == 1 ? candidates[0] : (named ?? null);
            if (match is null)
                throw new PluginLoadException(source.Id,
                    candidates.Count > 1
                        ? $"ambiguous legacy folder '{pluginDir}' — {candidates.Count} top-level assemblies, none named '{folderName}'"
                        : $"no entry assembly found in legacy folder '{pluginDir}'");
            entryPath = match;
        }

        var entry = alc.LoadFromAssemblyPath(entryPath);
        Type[] types;
        try { types = entry.GetTypes(); }
        catch (ReflectionTypeLoadException rtle)
        {
            throw new PluginLoadException(source.Id,
                $"entry assembly '{Path.GetFileName(entryPath)}' types could not load: {rtle.LoaderExceptions?.FirstOrDefault()?.Message}", rtle);
        }
        var impls = types
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(INetPiPlugin).IsAssignableFrom(t))
            .ToList();
        if (impls.Count == 0)
            throw new PluginLoadException(source.Id, $"entry assembly '{Path.GetFileName(entryPath)}' has no INetPiPlugin implementation");
        if (impls.Count > 1)
            throw new PluginLoadException(source.Id,
                $"entry assembly '{Path.GetFileName(entryPath)}' is ambiguous — {impls.Count} INetPiPlugin implementations: " +
                string.Join(", ", impls.Select(t => t.FullName)));
        return (INetPiPlugin)Activator.CreateInstance(impls[0])!;
    }

    /// <summary>
    /// astra-1 P4: the shared contract must be compatible BEFORE activation —
    /// the artifact's abstractions build id (content hash of the
    /// netPI.Abstractions.dll the plugin was compiled against) must equal the
    /// host's loaded abstractions build. An empty artifact token (pre-P4
    /// legacy manifest) degrades to a warning.
    /// </summary>
    private string? ValidateContract(PluginSource source)
    {
        if (source.AbstractionsBuildId is null or { Length: 0 })
        {
            _logger.LogWarning("{Plugin}: no abstractionsBuildId available — contract check skipped", source.Id);
            return null;
        }
        string hostToken;
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(typeof(INetPiPlugin).Assembly.Location)!,
                typeof(INetPiPlugin).Assembly.GetName().Name! + ".dll");
            hostToken = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))[..12].ToLowerInvariant();
        }
        catch
        {
            return "host netPI.Abstractions build could not be hashed — refusing to load (contract unverifiable)";
        }
        if (!string.Equals(source.AbstractionsBuildId, hostToken, StringComparison.OrdinalIgnoreCase))
            return $"contract mismatch: plugin built against netPI.Abstractions {source.AbstractionsBuildId}, host runs {hostToken}";
        return null;
    }

    /// <summary>
    /// astra-1 P4: load the plugin's native libraries for the CURRENT RID only —
    /// from the content-addressed process cache, so a second generation of the
    /// same native build reuses the exact same process mapping (no per-reload
    /// re-mapping, no blanket preloading of every RID). Unmappable files are
    /// warnings, not failures.
    /// </summary>
    private static bool SameNativeBuild(HashSet<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var x in a)
            if (!b.Contains(x)) return false;
        return true;
    }

    private List<string> LoadNativeAssets(string pluginDir)
    {
        var loaded = new List<string>();
        foreach (var path in NativeAssetCache.NativeRidFiles(pluginDir))
        {
            try
            {
                System.Runtime.InteropServices.NativeLibrary.Load(_nativeCache.EnsureCached(path));
                loaded.Add(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "could not preload native asset {Path} (the runtime may load it lazily)", path);
            }
        }
        return loaded;
    }

    // ----------------------------------------------------------------------
    // ----------------------------------------------------------------------
    // astra-1 P2: lifecycle operation queue + structured outcomes
    // ----------------------------------------------------------------------

    /// <summary>astra-1 P2: enqueue a reload of one plugin; awaits its structured outcome.</summary>
    public Task<PluginOperationOutcome> ReloadPluginAsync(string pluginId, CancellationToken ct = default)
    {
        var op = new LifecycleOp { Kind = LifecycleOpKind.Reload, PluginId = pluginId, Ct = ct };
        Enqueue(op);
        return op.Tcs.Task;
    }

    /// <summary>astra-1 P2: reload every known plugin, serially; one outcome per plugin.</summary>
    public Task<IReadOnlyList<PluginOperationOutcome>> ReloadAllOpAsync(CancellationToken ct = default)
    {
        var op = new LifecycleOp { Kind = LifecycleOpKind.ReloadAll, Ct = ct };
        Enqueue(op);
        return op.ListTcs.Task;
    }

    /// <summary>astra-1 P2: enqueue shutdown (queue-serialized; rejects new ops while queued).</summary>
    public async Task ShutdownOpAsync(CancellationToken ct = default)
    {
        var op = new LifecycleOp { Kind = LifecycleOpKind.Shutdown, Ct = ct };
        Enqueue(op);
        await op.Tcs.Task;
    }

    /// <summary>
    /// Legacy reload API (PLAN §6) kept for existing call sites and tests:
    /// returns the new generation on Applied/RolledBack, null otherwise.
    /// </summary>
    public async Task<PluginInstance?> ReloadAsync(string pluginId, CancellationToken ct = default)
    {
        if (Get(pluginId) is null)
            throw new ArgumentException($"plugin '{pluginId}' is not loaded", nameof(pluginId));
        var outcome = await ReloadPluginAsync(pluginId, ct);
        return outcome.Outcome is PluginLifecycleOutcome.Applied or PluginLifecycleOutcome.RolledBack
            ? Get(pluginId)
            : null;
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
            // astra-1 P3: the SAME idempotent cleanup path as the failure
            // paths — the unified stop decides on StartRan (a plugin whose
            // Start never ran is not Stop'd again).
            await CleanupGeneration(p, "shutdown", ct, stop: true);
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

/// <summary>
    /// astra-1 P3: ONE idempotent cleanup path, used for partial Load,
    /// partial Start, and shutdown — remove this generation's owned
    /// registrations/subscriptions/commands/panels from the host, stop and
    /// dispose the resources that were initialized, drop references, and
    /// initiate the ALC unload. Retries are no-ops (the idempotency guard),
    /// so a partially started generation can be cleaned up exactly once no
    /// matter which failure path reaches it.
    /// </summary>
    private async Task CleanupGeneration(PluginInstance p, string reason, CancellationToken ct, bool stop)
    {
        if (p.CleanupStarted) return;
        p.CleanupStarted = true;

        // 1. host-side teardown of owned registrations (all idempotent).
        _registry.RemoveAllFor(p);
        _bus.RemoveAllFor(p);
        p.Commands?.Unload();
        p.Commands = null;
        p.WebPanels?.Unload();
        p.WebPanels = null;

        // 2. stop + dispose the resources that were initialized.
        if (p.Plugin is not null)
        {
            if (stop && p.StartRan)
            {
                try { await p.Plugin.StopAsync(ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "{Plugin} StopAsync failed during {Reason} cleanup", p.PluginId, reason); }
            }
            try { await p.Plugin.UnloadAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "{Plugin} UnloadAsync failed during {Reason} cleanup", p.PluginId, reason); }
        }

        // 3. surface retained leases instead of clearing the table to conceal
        //    work that is still running: a generation retired with live leases
        //    keeps its ALC pinned (bounded diagnostics, not hidden state).
        if (p.LeasesHeld > 0)
            _logger.LogWarning("{Plugin} gen {Gen} retired with {N} live lease(s) during {Reason} — its ALC stays pinned until they release",
                p.PluginId, p.Generation, p.LeasesHeld, reason);

        // 4. drop references and initiate ALC unload (quiescence = steps 1-2 done).
        UnloadAloc(p);
    }

    /// <summary>Thrown when a plugin fails to load.</summary>
    public sealed class PluginLoadException(string pluginId, string message, Exception? inner = null)
        : Exception($"plugin '{pluginId}': {message}", inner)
    {
    }
    private sealed class LifecycleOp
    {
        required public LifecycleOpKind Kind { get; init; }
        public string? PluginId { get; init; }
        public CancellationToken Ct { get; init; }
        public TaskCompletionSource<PluginOperationOutcome> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<PluginOperationOutcome>> ListTcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum LifecycleOpKind { Scan, Reload, ReloadAll, Shutdown }

    /// <summary>
    /// astra-1 P2: one serialized queue writer. Every state-changing plugin
    /// operation (scan / reload / reload-all / shutdown) goes through here, so
    /// two lifecycle operations can never interleave. Ordinary agent/tool
    /// execution stays fully concurrent.
    /// </summary>
    private void Enqueue(LifecycleOp op)
    {
        if (Volatile.Read(ref _shutdownStarted) == 1)
        {
            var rej = RejectedOutcome(op);
            op.Tcs.TrySetResult(rej);
            op.ListTcs.TrySetResult(new List<PluginOperationOutcome>());
            return;
        }
        lock (_opGate)
        {
            if (Volatile.Read(ref _shutdownStarted) == 1)
            {
                var rej = RejectedOutcome(op);
                op.Tcs.TrySetResult(rej);
                op.ListTcs.TrySetResult(new List<PluginOperationOutcome>());
                return;
            }
            _inflightOps.Add(op);
            if (op.Kind == LifecycleOpKind.Shutdown)
                Volatile.Write(ref _shutdownStarted, 1);
        }
        _lifecycleQueue.Writer.TryWrite(op);
    }

    private static PluginOperationOutcome RejectedOutcome(LifecycleOp op) =>
        new(PluginOperationOutcome.ShutdownRejectedId, op.PluginId, null, null,
            PluginLifecyclePhase.Admission, PluginLifecycleOutcome.Deferred,
            "host shutdown in progress", false);

    private async Task RunLifecycleQueueAsync()
    {
        try
        {
            while (await _lifecycleQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_lifecycleQueue.Reader.TryRead(out var op))
                {
                    try
                    {
                        switch (op.Kind)
                        {
                            case LifecycleOpKind.Reload:
                            {
                                var r = await ReloadCoreAsync(op.PluginId!, op.Ct);
                                op.Tcs.TrySetResult(r.Outcome);
                                break;
                            }
                            case LifecycleOpKind.Scan:
                            {
                                var r = await ScanCoreAsync(op.Ct);
                                op.Tcs.TrySetResult(r);
                                break;
                            }
                            case LifecycleOpKind.ReloadAll:
                            {
                                var list = new List<PluginOperationOutcome>();
                                var ids = CurrentSnapshots().Select(p => p.PluginId).Distinct()
                                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
                                // plugins already in a Failed state are still reloadable (retry path)
                                foreach (var id in ids)
                                    list.Add((await ReloadCoreAsync(id, op.Ct)).Outcome);
                                op.ListTcs.TrySetResult(list);
                                var overall = list.Count == 0
                                    ? new PluginOperationOutcome(Guid.NewGuid().ToString("n"), null, null, null,
                                        PluginLifecyclePhase.Pinning, PluginLifecycleOutcome.Unchanged, null, false)
                                    : list.Aggregate((a, b) =>
                                        b.Outcome is PluginLifecycleOutcome.Applied
                                            ? b
                                            : a);
                                op.Tcs.TrySetResult(overall);
                                break;
                            }
                            case LifecycleOpKind.Shutdown:
                            {
                                await ShutdownAsync(op.Ct);
                                op.Tcs.TrySetResult(new PluginOperationOutcome(
                                    Guid.NewGuid().ToString("n"), null, null, null,
                                    PluginLifecyclePhase.Admission, PluginLifecycleOutcome.Applied, null, false));
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        op.Tcs.TrySetResult(new PluginOperationOutcome(
                            Guid.NewGuid().ToString("n"), op.PluginId, null, null,
                            PluginLifecyclePhase.Admission, PluginLifecycleOutcome.Deferred,
                            "cancelled", false));
                        op.ListTcs.TrySetResult(new List<PluginOperationOutcome>());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "lifecycle operation {Kind} failed", op.Kind);
                        op.Tcs.TrySetResult(new PluginOperationOutcome(
                            Guid.NewGuid().ToString("n"), op.PluginId, null, null,
                            PluginLifecyclePhase.Admission, PluginLifecycleOutcome.Failed, ex.Message, false));
                        op.ListTcs.TrySetException(ex);
                    }
                    finally
                    {
                        lock (_opGate) _inflightOps.Remove(op);
                    }
                }
            }
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "lifecycle queue runner died");
        }
    }

    /// <summary>
    /// astra-1 P2 core reload (queue runner only) — six steps:
    /// (1) admission + candidate pinning (old generation atomically → Draining);
    /// (2) AgentIdle gate, token flowing in, bounded;
    /// (3) drain — on timeout restore the ACTUAL previous state (Active or Failed);
    /// (4) stop + retire old generation, retaining its LKG source + config — an
    ///     uncooperative StopAsync ends the op as RestartRequired WITHOUT loading
    ///     a second, conflicting generation;
    /// (5) load + start the candidate — Active only after StartAsync;
    /// (6) candidate failure → best-effort LKG recovery: a FRESH last-known-good
    ///     generation (success = RolledBack) or Failed carrying both errors.
    /// </summary>
    private async Task<(PluginOperationOutcome Outcome, PluginInstance? New)> ReloadCoreAsync(string pluginId, CancellationToken ct)
    {
        var opId = Guid.NewGuid().ToString("n");
        string? requested = null;
        string? oldBuildId = null;

        PluginOperationOutcome Outcome(string? error, PluginLifecyclePhase phase, PluginLifecycleOutcome outcome,
            string? activeBuildId = null, bool restartRequired = false) =>
            new(opId, pluginId, requested, activeBuildId ?? oldBuildId, phase, outcome, error, restartRequired);

        var old = Get(pluginId);
        if (old is null)
            return (Outcome($"plugin '{pluginId}' is not loaded", PluginLifecyclePhase.Admission, PluginLifecycleOutcome.Deferred), null);
        oldBuildId = old.BuildId;
        if (old.State is not (PluginState.Active or PluginState.Failed))
            return (Outcome($"reload not possible in state {old.State}", PluginLifecyclePhase.Admission, PluginLifecycleOutcome.Deferred), null);

        // (1) admission: pin the candidate BEFORE touching the old generation.
        var src = DiscoverPluginSources().FirstOrDefault(x => x.Id == pluginId);
        if (src is null)
            return (Outcome($"no plugin source found under '{_options.PluginDirectory}'",
                PluginLifecyclePhase.Pinning, PluginLifecycleOutcome.Deferred), null);
        if (src.Kind == SourceKind.Invalid)
            return (Outcome($"source is invalid — {src.Note}",
                PluginLifecyclePhase.Pinning, PluginLifecycleOutcome.Deferred), null);
        requested = src.Kind == SourceKind.Published ? src.BuildId : "legacy";
        if (Volatile.Read(ref _shutdownStarted) == 1)
            return (Outcome("host shutdown in progress", PluginLifecyclePhase.Pinning, PluginLifecycleOutcome.Deferred), null);

        // astra-1 P4: a changed native build cannot be hot-swapped — the old
        // generation's native mappings stay pinned for the process lifetime,
        // so mapping a different native build in the same process is
        // RestartRequired (checked before the old generation is touched).
        var candidateNative = NativeAssetCache.NativeBuildHashes(
            src.Kind == SourceKind.Published ? src.ArtifactDir! : src.Directory);
        if (SameNativeBuild(candidateNative, old.NativeBuildHashes) == false)
            return (Outcome("native library build changed (e.g. e_sqlite3) — host restart required",
                PluginLifecyclePhase.Pinning, PluginLifecycleOutcome.RestartRequired, restartRequired: true), old);

        // astra-1 P3: drain admission is atomic with the state check, and
        // atomic with lease admission — after this returns, no NEW lease can
        // ever be admitted against this generation.
        if (!old.TryBeginDrain(out var prev))
            return (Outcome($"state moved to {old.State} during admission",
                PluginLifecyclePhase.Admission, PluginLifecycleOutcome.Deferred), null);
        _logger.LogInformation("{Plugin} draining; new service acquisitions are denied", pluginId);

        // (2) AgentIdle gate (bounded; operation token flows in).
        if (old.Policy == ReloadPolicy.AgentIdle)
        {
            var gate = AgentIdleGate;
            var gateOk = false;
            using (var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                gateCts.CancelAfter(_options.UnloadTimeout);
                try { gateOk = await gate(gateCts.Token).ConfigureAwait(false); }
                catch { gateOk = false; }
            }
            if (!gateOk)
            {
                RestoreDrain(old, prev, pluginId, "agent not idle");
                return (Outcome("agent not idle", PluginLifecyclePhase.Admission, PluginLifecycleOutcome.Deferred), null);
            }
        }

        // (3) drain (bounded) — on timeout restore the actual previous state.
        if (!await WaitForDrainAsync(old, ct).ConfigureAwait(false))
        {
            RestoreDrain(old, prev, pluginId, "lease drain timed out");
            return (Outcome($"lease drain timed out after {_options.UnloadTimeout.TotalSeconds:0}s",
                PluginLifecyclePhase.Draining, PluginLifecycleOutcome.Deferred), null);
        }

        // (4) stop + retire the old generation (retain LKG source + config).
        old.TrySetState(PluginState.Draining, PluginState.Unloading);
        try
        {
            using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stopCts.CancelAfter(_options.UnloadTimeout);
            await old.Plugin!.StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Uncooperative stop: quarantine the old generation and DO NOT load
            // a second, conflicting generation — a host restart is the only
            // safe path (PLAN §P2 step 4 / "hot rollback only for compatible
            // state").
            _logger.LogError(ex, "{Plugin} StopAsync did not complete; quarantining (restart required)", pluginId);
            return (Outcome($"stop failed: {ex.Message}", PluginLifecyclePhase.Stopping,
                PluginLifecycleOutcome.RestartRequired, oldBuildId, restartRequired: true), old);
        }

        var lkgDir = old.SourceArtifactDir;
        var lkgConfig = old.SourceConfig;
        RetireGeneration(old);
        UnloadAloc(old);
        lock (_gate) _current.Remove(pluginId);
        lock (_opGate) _unavailablePlugins[pluginId] = requested;

        // (5) load + start the candidate.
        var candidateError = await LoadAndStartCandidateAsync(src, pluginId, ct);
        if (candidateError is not null)
        {
            _logger.LogError("Reload of {Plugin} failed: {Err}", pluginId, candidateError);

            // (6) LKG recovery: the old generation is already stopped + unloaded,
            // so the only honest recovery is a FRESH generation from the retained
            // last-known-good source ("previous build active" must mean a running
            // build, never a phantom).
            var recovery = await TryRecoverAsync(pluginId, lkgDir, lkgConfig, requested, candidateError, ct);
            return recovery is null
                ? (Outcome($"{candidateError} (rollback unavailable: {LkgUnavailableReason(lkgDir)})",
                    PluginLifecyclePhase.RollingBack, PluginLifecycleOutcome.Failed), null)
                : (recovery.Value.Outcome, recovery.Value.New);
        }

        var fresh = Get(pluginId)!;
        fresh.StartRan = true;
        fresh.State = PluginState.Active;
        fresh.ClearLastError();
        lock (_opGate) _unavailablePlugins.Remove(pluginId);
        _logger.LogInformation("{Plugin} reloaded: gen {OldGen} → gen {NewGen} (build {Build})",
            pluginId, old.Generation, fresh.Generation, fresh.BuildId);
        return (Outcome(null, PluginLifecyclePhase.Starting, PluginLifecycleOutcome.Applied, fresh.BuildId), fresh);
    }

    private void RestoreDrain(PluginInstance old, PluginState prev, string pluginId, string reason)
    {
        if (old.TrySetState(PluginState.Draining, prev))
            _logger.LogWarning("reload of {Plugin} deferred — state restored to {Prev} ({Reason})", pluginId, prev, reason);
        else
            _logger.LogWarning("reload of {Plugin} deferred; state is {Now}, requested restore to {Prev} ({Reason})",
                pluginId, old.State, prev, reason);
    }

    /// <summary>Host-side teardown of a generation's registrations/subscriptions.</summary>
    private void RetireGeneration(PluginInstance instance)
    {
        _registry.RemoveAllFor(instance);
        _bus.RemoveAllFor(instance);
        instance.Commands?.Unload();
        instance.Commands = null;
        instance.WebPanels?.Unload();
        instance.WebPanels = null;
    }

    /// <summary>(5) Load + start the candidate generation. Returns null on success.</summary>
    private async Task<string?> LoadAndStartCandidateAsync(PluginSource src, string pluginId, CancellationToken ct)
    {
        string? loadError = null;
        PluginInstance? fresh;
        try
        {
            fresh = await LoadGenerationAsync(src, ct, errorSink: m => loadError = m).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        if (fresh is null)
            return loadError ?? "candidate generation could not load";

        try
        {
            await fresh.Plugin!.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed start never becomes Active: retire the half-started
            // candidate and let the LKG recovery decide the plugin's fate.
            fresh.LastError = ex.Message;
            fresh.State = PluginState.Failed;
            _logger.LogError(ex, "{Plugin} StartAsync failed after reload", pluginId);
            // astra-1 P3: unified idempotent cleanup (partial Start).
            await CleanupGeneration(fresh, "partial start", ct, stop: true);
            lock (_gate)
            {
                if (ReferenceEquals(Get(pluginId), fresh))
                    _current.Remove(pluginId);
            }
            return $"candidate start failed: {ex.Message}";
        }
        return null;
    }

    /// <summary>
    /// (6) Re-instantiate the retained LKG source as a FRESH generation and start
    /// it. Returns null when the LKG cannot be recovered.
    /// </summary>
    private async Task<(PluginOperationOutcome Outcome, PluginInstance? New)?> TryRecoverAsync(
        string pluginId, string? lkgDir, System.Text.Json.JsonElement lkgConfig,
        string requestedBuildId, string candidateError, CancellationToken ct)
    {
        if (lkgDir is null || !Directory.Exists(lkgDir))
            return null;

        var lkgSource = new PluginSource
        {
            Id = pluginId,
            Directory = lkgDir,
            Kind = SourceKind.Legacy,
            Note = "LKG recovery source",
        };
        PluginInstance? lkg = null;
        try
        {
            lkg = await LoadGenerationAsync(lkgSource, CancellationToken.None, ownConfigOverride: lkgConfig).ConfigureAwait(false);
            if (lkg is null)
                return null;
            await lkg.Plugin!.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (lkg is not null)
            {
                lkg.LastError = ex.Message;
                lkg.State = PluginState.Failed;
                await CleanupGeneration(lkg, "LKG recovery failure", ct, stop: lkg.StartRan);
                lock (_gate) { if (ReferenceEquals(Get(pluginId), lkg)) _current.Remove(pluginId); }
            }
            return null;
        }
        lkg.StartRan = true;
        lkg.State = PluginState.Active;
        lkg.ClearLastError();
        lock (_opGate) _unavailablePlugins.Remove(pluginId);
        _logger.LogWarning("{Plugin} rolled back to last-known-good as gen {Gen} (candidate error: {Err})",
            pluginId, lkg.Generation, candidateError);
        return (new PluginOperationOutcome(
            Guid.NewGuid().ToString("n"), pluginId, requestedBuildId, lkg.BuildId,
            PluginLifecyclePhase.RollingBack, PluginLifecycleOutcome.RolledBack,
            candidateError, false), lkg);
    }

    private static string LkgUnavailableReason(string? lkgDir) =>
        lkgDir is null
            ? "no retained last-known-good source"
            : !Directory.Exists(lkgDir)
                ? "last-known-good source directory no longer exists"
                : "last-known-good start failed";

    /// <summary>
    /// astra-1 P2: rescan the plugin directory (queue runner only). Loads+starts
    /// plugins the host has never seen; already-known plugins are untouched.
    /// </summary>
    private async Task<PluginOperationOutcome> ScanCoreAsync(CancellationToken ct)
    {
        var opId = Guid.NewGuid().ToString("n");
        var loaded = new List<string>();
        foreach (var src in DiscoverPluginSources())
        {
            ct.ThrowIfCancellationRequested();
            if (src.Kind == SourceKind.Invalid) continue;
            var existing = Get(src.Id);
            if (existing is { State: PluginState.Active or PluginState.Draining or PluginState.Loading or PluginState.Unloading or PluginState.Failed })
                continue;
            var inst = await LoadGenerationAsync(src, ct).ConfigureAwait(false);
            if (inst is null) continue;
            try
            {
                await inst.Plugin!.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // a start failure is NOT a successful scan: the plugin is left
                // Failed (retryable via reload), and its id is NOT reported.
                _logger.LogError(ex, "Plugin {Plugin} StartAsync failed (gen {Gen})", inst.PluginId, inst.Generation);
                inst.LastError = ex.Message;
                inst.State = PluginState.Failed;
                continue;
            }
            inst.StartRan = true;
            inst.State = PluginState.Active;
            inst.ClearLastError();
            loaded.Add(src.Id);
            _logger.LogInformation("Plugin {Plugin} scanned in from {Dir} (gen {Gen})", src.Id, inst.CacheDirectory, inst.Generation);
        }
        return new PluginOperationOutcome(opId, null, null, null,
            PluginLifecyclePhase.Starting, PluginLifecycleOutcome.Applied,
            loaded.Count == 0 ? null : $"loaded: {string.Join(", ", loaded)}", false);
    }
}
