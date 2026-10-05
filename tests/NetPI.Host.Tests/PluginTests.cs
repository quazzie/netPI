using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Host.Web;

namespace NetPI.Host.Tests;

/// <summary>Builds tests/SamplePlugin in its variants (v1, v2, fail) into artifacts/tests/SamplePlugin/&lt;variant&gt;/.</summary>
public static class SampleBuild
{
    private static Task? _build;

    /// <summary>
    /// The configuration this runner was built with. With BuildProjectReferences=false the sample needs NetPI.Abstractions'
    /// reference assembly of the same configuration, and build.ps1 builds Release only.
    /// </summary>
    public static readonly string Configuration =
        typeof(SampleBuild).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration is { Length: > 0 } c ? c : "Debug";

    public static string Dir(string variant) => Path.Combine(T.RepoRoot, "artifacts", "tests", "SamplePlugin", variant);

    public static Task EnsureAsync() => _build ??= BuildAllAsync();

    private static async Task BuildAllAsync()
    {
        var project = Path.Combine(T.RepoRoot, "tests", "SamplePlugin", "SamplePlugin.csproj");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        var variants = new[] { "v1", "v2", "fail" };
        // Restore once first: NuGet writes its assets into the project's one obj/ (not the per-variant intermediate
        // dirs), so three concurrent restores in a fresh checkout race on the same files ("Cannot create a file when
        // that file already exists"). The checkout that already had them never saw it.
        await RunAsync(dotnet, ["restore", project, "-nologo", "-v:q"], "restoring SamplePlugin");
        // The three variants write disjoint obj/out dirs, so they build at once: the wall time is the slowest
        // build, not the sum of three sequential `dotnet build` startups.
        var procs = new Dictionary<string, Process>();
        var outs = new Dictionary<string, Task<string>>();
        var errs = new Dictionary<string, Task<string>>();
        try
        {
            foreach (var variant in variants)
            {
                var psi = new ProcessStartInfo(dotnet)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                foreach (var a in new[] { "build", project, "--no-restore", "-c", Configuration, $"-p:SampleVariant={variant}", "-p:BuildProjectReferences=false", "-nologo", "-v:q", "-clp:ErrorsOnly" })
                    psi.ArgumentList.Add(a);
                var p = Process.Start(psi)!;
                procs[variant] = p;
                outs[variant] = p.StandardOutput.ReadToEndAsync();
                errs[variant] = p.StandardError.ReadToEndAsync();
            }
            foreach (var variant in variants)
            {
                await procs[variant].WaitForExitAsync();
                if (procs[variant].ExitCode != 0)
                {
                    foreach (var other in procs.Values) other.Kill(true);
                    throw new AssertException($"building SamplePlugin ({variant}) failed:\n{await outs[variant]}\n{await errs[variant]}");
                }
                if (!File.Exists(Path.Combine(Dir(variant), "SamplePlugin.dll"))) throw new AssertException($"SamplePlugin ({variant}) output missing");
            }
        }
        finally
        {
            foreach (var p in procs.Values) p.Dispose();
        }
    }

    private static async Task RunAsync(string exe, string[] args, string what)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new AssertException($"{what} failed:\n{await stdout}\n{await stderr}");
    }
}

public static class PluginTests
{
    public static void Register(TestRunner r)
    {
        r.Add("plugins: sample plugin registers everything through scoped registries", async () =>
        {
            await SampleBuild.EnsureAsync();
            var (pluginsRoot, _) = PreparePluginDir("v1");
            await using var server = await StartAsync(pluginsRoot);
            var info = server.Plugins.List().Single(p => p.Id == "test.sample");
            Check.Equal("running", info.State, info.Error);
            Check.Equal("Sample plugin", info.Name);
            Check.Equal(50, info.Order);
            Check.Equal(1, info.LoadCount);
            Check.True(info.Enabled);
            Check.Equal("v1", (string?)await server.Rpc.InvokeAsync("sample.value"));
            Check.Contains(await EchoJsonAsync(server), "\"version\":\"v1\"");
            Check.Equal("v1|test.sample|1|1", Inspect(server));
            Check.Equal("hello v1", await HttpGetAsync(server, "/api/p/test.sample/hello"));
            Check.Contains(await HttpGetAsync(server, "/plugins/test.sample/ui.js", auth: false), "sample plugin");
            Check.Equal(1L, server.Kernel.Storage.Plugins.For("test.sample").Collection("sample_items", new CollectionSpec().Text("variant")).Count());
            Check.Contains(await PingAsync(server), "\"version\":\"v1\"");
            Check.Equal("test.sample", server.Kernel.Rpc.List().Single(m => m.Method == "sample.value").PluginId);
        });

        r.Add("plugins: hot reload on dll replacement swaps registrations and the old context unloads", async () =>
        {
            await SampleBuild.EnsureAsync();
            var (pluginsRoot, pluginDir) = PreparePluginDir("v1");
            await using var server = await StartAsync(pluginsRoot);
            Check.Equal("v1", (string?)await server.Rpc.InvokeAsync("sample.value"));
            Check.Equal("none", (string?)await server.Rpc.InvokeAsync("sample.overlap"), "a first start has nothing before it");
            Check.Contains(await PingAsync(server), "v1"); // plugin-typed event payload in the bus ring buffer
            Check.Contains(await EchoJsonAsync(server), "v1"); // plugin-typed RPC result serialized with the collectible options
            var oldContext = CurrentContext(server);
            var tabVersion = TabVersion(server);

            // "Rebuild": overwrite the dll (+pdb/deps) in the watched folder, like `dotnet build` does.
            foreach (var file in Directory.GetFiles(SampleBuild.Dir("v2")))
                File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);

            // Registrations appear during StartAsync; the state flips to running once StartAsync returned.
            await Wait.Until(() => server.Plugins.List().Single(p => p.Id == "test.sample") is { State: "running", LoadCount: 2 }, "plugin reloaded", 20_000);
            Check.Equal("v2", await TryValueAsync(server));
            // the swap: the old tool was still registered when the new instance started, so no chat ever saw it missing
            Check.Equal("present", (string?)await server.Rpc.InvokeAsync("sample.overlap"),
                "the previous version's registrations are retired after the new instance is up, not before");
            var info = server.Plugins.List().Single(p => p.Id == "test.sample");
            Check.Equal("running", info.State);
            Check.Equal(2, info.LoadCount);
            Check.Equal("v2|test.sample|1|1", Inspect(server), "registrations replaced, not duplicated");
            Check.Equal("hello v2", await HttpGetAsync(server, "/api/p/test.sample/hello"));
            Check.Contains(await PingAsync(server), "\"version\":\"v2\"");
            Check.False(tabVersion == TabVersion(server), "UI version changes on reload");
            Check.Equal(2L, server.Kernel.Storage.Plugins.For("test.sample").Collection("sample_items", new CollectionSpec().Text("variant")).Count());

            await Wait.Until(() => server.Kernel.Plugins.GetLoadState("test.sample").LastUnloadCollected is not null, "unload check finished", 30_000);
            Check.Equal(true, server.Kernel.Plugins.GetLoadState("test.sample").LastUnloadCollected, "host reports the old context collected");
            Check.True(await CollectedAsync(oldContext), "old AssemblyLoadContext was garbage collected");
        });

        r.Add("plugins: a reload that fails to start keeps the running version (swap, not restart)", async () =>
        {
            await SampleBuild.EnsureAsync();
            var (pluginsRoot, pluginDir) = PreparePluginDir("v1");
            await using var server = await StartAsync(pluginsRoot);
            Check.Equal("v1", (string?)await server.Rpc.InvokeAsync("sample.value"));
            var oldContext = CurrentContext(server);

            // the "rebuild" is a version whose StartAsync throws: the swap must not take the running one down with it
            foreach (var file in Directory.GetFiles(SampleBuild.Dir("fail")))
                File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);

            // the swap failed, the old load was put back: state is running again and the reason is on the plugin
            await Wait.Until(() => server.Plugins.List().Single(p => p.Id == "test.sample") is { State: "running" } info && info.Error is not null,
                "the failed swap was rolled back", 20_000);
            var info = server.Plugins.List().Single(p => p.Id == "test.sample");
            Check.Equal("running", info.State, "still serving");
            Check.Equal("v1", (string?)await server.Rpc.InvokeAsync("sample.value"), "the old version kept its registrations");
            Check.Equal("v1|test.sample|1|1", Inspect(server), "nothing was duplicated or lost");
            Check.Contains(info.Error ?? "", "sample plugin failure", "and the reason is on the plugin");
            Check.Equal("hello v1", await HttpGetAsync(server, "/api/p/test.sample/hello"), "its HTTP route is still mapped");
            Check.True(ReferenceEquals(oldContext.Target, CurrentContext(server).Target),
                "the running load context is the one that was there before, not a fresh one");
        });

        r.Add("plugins: plugins.quiet holds reloads back, and switching it off applies them", async () =>
        {
            await SampleBuild.EnsureAsync();
            var (pluginsRoot, pluginDir) = PreparePluginDir("v1");
            await using var server = await StartAsync(pluginsRoot);
            server.Settings.Set("plugins.quiet", true);
            var oldContext = CurrentContext(server).Target;

            // a build while quiet: the file changes, the running version does not
            foreach (var file in Directory.GetFiles(SampleBuild.Dir("v2")))
                File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);
            await Wait.Until(() => server.Plugins.Deferred().Contains("test.sample"), "the reload was deferred", 20_000);
            Check.Equal("v1", (string?)await server.Rpc.InvokeAsync("sample.value"), "the running version keeps serving");
            Check.Equal(1, server.Plugins.List().Single(p => p.Id == "test.sample").LoadCount, "nothing was loaded");
            Check.True(ReferenceEquals(oldContext, CurrentContext(server).Target), "the same load context, untouched");
            Check.Equal(0, server.Plugins.Deferred().Count(p => p != "test.sample"), "only this one is waiting");

            // switching quiet off applies what piled up
            server.Settings.Set("plugins.quiet", false);
            await Wait.Until(() => server.Plugins.List().Single(p => p.Id == "test.sample") is { State: "running", LoadCount: 2 },
                "the deferred reload was applied", 20_000);
            Check.Equal("v2", (string?)await server.Rpc.InvokeAsync("sample.value"));
            Check.Equal(0, server.Plugins.Deferred().Count, "the queue is empty");
        });

        r.Add("plugins: wwwroot changes only bump the UI version (no reload)", async () =>
        {
            await SampleBuild.EnsureAsync();
            var (pluginsRoot, pluginDir) = PreparePluginDir("v1");
            await using var server = await StartAsync(pluginsRoot);
            var before = TabVersion(server);
            var uiChanged = 0;
            using var sub = server.Events.Subscribe(EventTypes.UiChanged, _ => Interlocked.Increment(ref uiChanged));
            File.WriteAllText(Path.Combine(pluginDir, "wwwroot", "ui.js"), "export function mount(el) { el.textContent = 'changed'; }");
            await Wait.Until(() => TabVersion(server) != before, "tab version bumped");
            await Wait.Until(() => Volatile.Read(ref uiChanged) > 0, "ui.changed published");
            Check.Equal(1, server.Plugins.List().Single(p => p.Id == "test.sample").LoadCount, "not reloaded");
            Check.Contains(await HttpGetAsync(server, "/plugins/test.sample/ui.js", auth: false), "changed");
        });

        r.Add("plugins: failing StartAsync → failed state, registrations removed; new folders are picked up", async () =>
        {
            await SampleBuild.EnsureAsync();
            var (pluginsRoot, pluginDir) = PreparePluginDir("fail");
            await using var server = await StartAsync(pluginsRoot);
            var info = server.Plugins.List().Single(p => p.Id == "test.sample");
            Check.Equal("failed", info.State);
            Check.Contains(info.Error, "sample plugin failure");
            Check.False(server.Rpc.Exists("sample.value"), "RPC registered before the failure is removed");
            Check.True(server.Kernel.Tools.Get("sample_echo") is null);
            Check.Equal(0, server.Kernel.Ui.Tabs.Count(t => t.PluginId == "test.sample"));
            // The failed load is unloaded like any other, and nothing on the entry keeps its context alive: a reference
            // left there would survive every forced collection and read as a leak.
            await Wait.Until(() => server.Kernel.Plugins.GetLoadState("test.sample").LastUnloadCollected is not null, "unload check finished", 30_000);
            Check.Equal(true, server.Kernel.Plugins.GetLoadState("test.sample").LastUnloadCollected, "the failed load's context was collected");

            // Disabled via settings → stopped; a folder for another plugin appearing later is discovered.
            var second = Path.Combine(pluginsRoot, "Zz.Second");
            T.CopyDir(SampleBuild.Dir("v2"), second);
            File.WriteAllText(Path.Combine(second, "plugin.json"), """{ "id": "test.second", "assembly": "SamplePlugin.dll" }""");
            await Wait.Until(() => server.Plugins.List().Any(p => p.Directory == PathUtilNormalize(second) && p.State is not ("loading" or "unloaded")),
                "new folder discovered and started", 15_000);
            var twin = server.Plugins.List().Single(p => p.Directory == PathUtilNormalize(second));
            // Same attribute id as the failed plugin: it may run (the first one is not running).
            Check.Equal("running", twin.State, twin.Error);
            Check.Equal("v2", (string?)await server.Rpc.InvokeAsync("sample.value"));

            // A rebuild of the failed folder: its id is taken by the running twin, so the reload is refused rather than
            // starting a second instance beside it (both would answer the same RPCs and tools)...
            foreach (var file in Directory.GetFiles(SampleBuild.Dir("v1")))
                File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);
            await Wait.Until(() => server.Plugins.List().Single(p => p.Directory == PathUtilNormalize(pluginDir)).Error?.Contains("Duplicate plugin id") == true,
                "the twin's reload was refused", 20_000);
            Check.Equal("failed", server.Plugins.List().Single(p => p.Directory == PathUtilNormalize(pluginDir)).State);
            Check.Equal("v2", (string?)await server.Rpc.InvokeAsync("sample.value"), "the running twin is untouched");
            // ...and the id names the running one: plugins.reload goes to it, not to the first folder under that id
            await server.Plugins.ReloadAsync("test.sample");
            twin = server.Plugins.List().Single(p => p.Directory == PathUtilNormalize(second));
            Check.Equal("running", twin.State, twin.Error);
            Check.Equal(2, twin.LoadCount, "plugins.reload went to the running twin");

            await server.Plugins.SetEnabledAsync("test.sample", false);
            Check.False(server.Rpc.Exists("sample.value"));
            Check.True(server.Plugins.List().All(p => p.Id != "test.sample" || p.State is "disabled" or "failed"));
            Check.Contains(server.Settings.GetNode("plugins.disabled")!.ToJsonString(), "test.sample");
            await server.Plugins.SetEnabledAsync("test.sample", true);
            await Wait.Until(() => server.Rpc.Exists("sample.value"), "re-enabled");
        });

        r.Add("plugins: a plugin whose folder disappears is removed, and says so on the bus (kind: removed)", async () =>
        {
            await SampleBuild.EnsureAsync();
            var (pluginsRoot, pluginDir) = PreparePluginDir("v1");
            await using var server = await StartAsync(pluginsRoot);
            Check.Equal("running", server.Plugins.List().Single(p => p.Id == "test.sample").State);
            Check.True(server.Kernel.Tools.Get("sample_echo") is not null, "its tool is registered");

            // The context plugin names the cause of a vanished tool from plugins.reloaded, so a removal that says
            // nothing leaves the notice with no cause at all ("unknown").
            var removals = new List<(string Ids, string Kind)>();
            using var sub = server.Events.Subscribe(EventTypes.PluginsReloaded, e =>
            {
                var d = NetPiJson.ToNode(e.Data) as JsonObject;
                if (d?["ids"] is not JsonArray ids) return;
                lock (removals) removals.Add((string.Join(",", ids.Select(n => n!.GetValue<string>())), d["kind"]?.GetValue<string>() ?? ""));
            });

            Directory.Delete(pluginDir, recursive: true);   // the folder is gone (a moved or deleted plugin install)
            await server.Plugins.RescanAsync();

            Check.True(server.Plugins.List().All(p => p.Id != "test.sample"), "the plugin is no longer listed");
            Check.True(server.Kernel.Tools.Get("sample_echo") is null, "and its tools are gone with it");
            await Wait.Until(() => { lock (removals) return removals.Count > 0; }, "plugins.reloaded for the removal", 20_000);
            (string Ids, string Kind) first;
            lock (removals) first = removals[0];
            Check.Contains(first.Ids, "test.sample", "the event names the plugin that went away");
            Check.Equal("removed", first.Kind, "and says why its tools are gone");
        });

        r.Add("plugins: a failed plugin switched off is disabled and switched on is retried; plugin.json enabled:false stops it on reload", async () =>
        {
            await SampleBuild.EnsureAsync();
            var (pluginsRoot, pluginDir) = PreparePluginDir("fail");
            await using var server = await StartAsync(pluginsRoot);
            PluginInfo Info() => server.Plugins.List().Single(p => p.Id == "test.sample");
            // StartAsync writes a row before it throws, so the rows count its attempts
            long Starts() => server.Kernel.Storage.Plugins.For("test.sample").Collection("sample_items", new CollectionSpec().Text("variant")).Count();
            Check.Equal("failed", Info().State);
            Check.Equal(1L, Starts());

            await server.Plugins.SetEnabledAsync("test.sample", false);
            Check.Equal("disabled", Info().State, "off is off, also for a plugin that failed");
            Check.False(Info().Enabled);
            Check.Equal(1L, Starts(), "nothing was started");

            await server.Plugins.SetEnabledAsync("test.sample", true);
            Check.Equal("failed", Info().State, "switched on, it was started again (and failed again)");
            Check.Contains(Info().Error, "sample plugin failure");
            Check.Equal(2L, Starts(), "StartAsync ran again");

            await server.Plugins.SetEnabledAsync("test.sample", true);   // on already: an explicit enable is the way to retry it
            Check.Equal(3L, Starts(), "an enable of a failed plugin that is on retries it");

            // a build that fixes it is hot-reloaded like any other: failed → running
            foreach (var file in Directory.GetFiles(SampleBuild.Dir("v1")))
                File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);
            await Wait.Until(() => Info() is { State: "running", LoadCount: 1 }, "the fixed build started", 20_000);
            Check.Equal("v1", (string?)await server.Rpc.InvokeAsync("sample.value"));

            // plugin.json enabled:false is honoured by the reload it triggers: the plugin is stopped, not hot-reloaded
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """{ "id": "test.sample", "enabled": false }""");
            await Wait.Until(() => Info().State == "disabled", "off by its manifest", 20_000);
            Check.False(server.Rpc.Exists("sample.value"), "stopped: its registrations are gone");
            Check.False(Info().Enabled);
            Check.Equal(1, Info().LoadCount, "not loaded again");
            // switching it on forces it past the manifest (plugins.enabled) and starts it
            await server.Plugins.SetEnabledAsync("test.sample", true);
            Check.Equal("running", Info().State, Info().Error);
            Check.Equal(2, Info().LoadCount);
        });

        r.Add("plugins: a reload with nothing to load (the assembly deleted) keeps the running version and announces no reload", async () =>
        {
            await SampleBuild.EnsureAsync();
            var (pluginsRoot, pluginDir) = PreparePluginDir("v1");
            await using var server = await StartAsync(pluginsRoot);
            Check.Equal("v1", (string?)await server.Rpc.InvokeAsync("sample.value"));

            // One subscriber for both events, so they arrive in publish order: a reload that was attempted says
            // plugins.reloaded before plugins.changed; one that was not says plugins.changed alone. The marker is
            // delivered after whatever the start published, so from it on only the reload speaks.
            var seen = new List<string>();
            using var sub = server.Events.Subscribe("plugins.*", e => { lock (seen) seen.Add(e.Type); });
            server.Events.Publish("plugins.test-marker");
            await Wait.Until(() => { lock (seen) return seen.Contains("plugins.test-marker"); }, "marker delivered");
            lock (seen) seen.Clear();

            File.Delete(Path.Combine(pluginDir, "SamplePlugin.dll"));   // the watcher schedules a reload for the dll change
            await Wait.Until(() => { lock (seen) return seen.Contains(EventTypes.PluginsChanged); }, "the reload reported what it found", 20_000);
            var info = server.Plugins.List().Single(p => p.Id == "test.sample");
            Check.Equal("running", info.State, "the running version keeps serving");
            Check.Contains(info.Error, "not found", "and the plugin says what is missing");
            Check.Equal("v1", (string?)await server.Rpc.InvokeAsync("sample.value"));
            lock (seen) Check.False(seen.Contains(EventTypes.PluginsReloaded), "no reload was attempted, so none is announced: " + string.Join(",", seen));
        });

        r.Add("plugins: a rescan reloads a plugin whose assembly changed without the watcher seeing it (folder swapped in whole)", async () =>
        {
            await SampleBuild.EnsureAsync();
            var (pluginsRoot, pluginDir) = PreparePluginDir("v1");
            await using var server = await StartAsync(pluginsRoot);
            Check.Equal("v1", (string?)await server.Rpc.InvokeAsync("sample.value"));

            // A deploy that swaps the folder as a whole (staged outside the watched root): no file inside changes in
            // place, so the watcher sees at most the folder, which a rescan finds present. What a lost event looks like.
            var staged = Path.Combine(T.TempDir("staged"), "SamplePlugin");
            T.CopyDir(SampleBuild.Dir("v2"), staged);
            File.SetLastWriteTimeUtc(Path.Combine(staged, "SamplePlugin.dll"), DateTime.UtcNow);   // a rebuilt file is newer, whatever the build did
            Directory.Move(pluginDir, Path.Combine(T.TempDir("retired"), "SamplePlugin"));
            Directory.Move(staged, pluginDir);
            await server.Plugins.RescanAsync();

            await Wait.Until(() => server.Plugins.List().Single(p => p.Id == "test.sample") is { State: "running" } p && p.LoadCount >= 2,
                "reloaded by the rescan", 20_000);
            Check.Equal("v2", (string?)await server.Rpc.InvokeAsync("sample.value"), "the swapped-in build serves");
        });

        // build.ps1 while NetPI runs: what the running NetPI must not load yet waits in .pending, replaced host files in .old
        r.Add("plugins: the next start installs a build made while NetPI ran (.pending plugins and web UI, .old files)", () =>
        {
            var app = T.TempDir("pending");
            void Put(string rel, string text)
            {
                var path = Path.Combine(app, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            string Text(string rel) => File.ReadAllText(Path.Combine(app, rel));
            Put("plugins/A/A.dll", "old A");
            Put("plugins/A/stale.txt", "only in the old build");
            Put("plugins/C/C.dll", "C, unchanged");
            Put("wwwroot/index.html", "old UI");
            Put("wwwroot/assets/old-123.js", "an old bundle");
            Put(".pending/plugins/A/A.dll", "new A");
            Put(".pending/plugins/B/B.dll", "new B");
            Put(".pending/wwwroot/index.html", "new UI");
            Put(".old/20260925-120000/NetPI.Host.dll", "the host an earlier NetPI ran");

            var log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            if (OperatingSystem.IsWindows())
            {
                // a folder with a file in use can't be moved: that plugin stays pending, the rest is installed
                using (File.Open(Path.Combine(app, "plugins/A/A.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
                    PendingBuild.Install(app, log);
                Check.Equal("old A", Text("plugins/A/A.dll"), "left as it was");
                Check.Equal("new A", Text(".pending/plugins/A/A.dll"), "still pending");
                Check.Equal("new B", Text("plugins/B/B.dll"));
                Check.Equal("new UI", Text("wwwroot/index.html"));
            }

            PendingBuild.Install(app, log);
            Check.Equal("new A", Text("plugins/A/A.dll"));
            Check.False(File.Exists(Path.Combine(app, "plugins/A/stale.txt")), "the whole folder is replaced");
            Check.Equal("new B", Text("plugins/B/B.dll"));
            Check.Equal("C, unchanged", Text("plugins/C/C.dll"));
            Check.Equal("new UI", Text("wwwroot/index.html"));
            Check.False(File.Exists(Path.Combine(app, "wwwroot/assets/old-123.js")), "the web UI is replaced as a whole");
            Check.False(Directory.Exists(Path.Combine(app, ".pending")), "nothing left pending");
            Check.False(Directory.Exists(Path.Combine(app, ".old")), "replaced files and folders are deleted");

            PendingBuild.Install(app, log); // nothing pending: nothing changes
            Check.Equal("new A", Text("plugins/A/A.dll"));
        });

        r.Add("plugins: the next start waits for a publish that holds the install lock, and takes over a stale one", () =>
        {
            var app = T.TempDir("pending-lock");
            void Put(string rel, string text)
            {
                var path = Path.Combine(app, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            string Text(string rel) => File.ReadAllText(Path.Combine(app, rel));
            Put("plugins/A/A.dll", "old A");
            Put(".pending/plugins/A/A.dll", "new A");
            var lockFile = Path.Combine(app, PendingBuild.LockName);
            var log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

            // build.ps1 holds the lock for the whole install (CreateNew, no sharing): a start under it waits, and past
            // its wait leaves the pending build alone rather than move a half-written folder into place
            using (new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var sw = Stopwatch.StartNew();
                PendingBuild.Install(app, log, TimeSpan.FromMilliseconds(1200));
                Check.True(sw.ElapsedMilliseconds >= 1000, "it waited for the lock");
                Check.Equal("old A", Text("plugins/A/A.dll"), "nothing was installed under the publish");
                Check.Equal("new A", Text(".pending/plugins/A/A.dll"), "still pending");
                Check.True(File.Exists(lockFile), "a lock it did not take is left alone");
            }
            // the publisher released it (build.ps1 deletes the file after): the next start installs
            File.Delete(lockFile);
            PendingBuild.Install(app, log, TimeSpan.FromSeconds(5));
            Check.Equal("new A", Text("plugins/A/A.dll"));
            Check.False(Directory.Exists(Path.Combine(app, ".pending")), "nothing left pending");
            Check.False(File.Exists(lockFile), "the lock is released and removed");

            // a lock a publisher left behind when it died (older than a minute) is taken over, as build.ps1 does
            Put(".pending/plugins/B/B.dll", "new B");
            File.WriteAllText(lockFile, "pid 1, since long ago");
            File.SetLastWriteTimeUtc(lockFile, DateTime.UtcNow.AddMinutes(-2));
            PendingBuild.Install(app, log, TimeSpan.FromSeconds(5));
            Check.Equal("new B", Text("plugins/B/B.dll"), "installed past the stale lock");
            Check.False(File.Exists(lockFile));
        });

        r.Add("plugins: real built plugins (tools + providers) load from the build output", async () =>
        {
            // the build output (artifacts/dev/app) is where a build lands; NETPI_APP_DIR names an app folder (as
            // build.ps1 -Test sets it) and artifacts/app is the installed app. A worktree has its own artifacts/.
            var candidates = Environment.GetEnvironmentVariable("NETPI_APP_DIR") is { Length: > 0 } custom
                ? new[] { Path.Combine(custom, "plugins") }
                : new[] { "dev", "" }.Select(p => Path.Combine(T.RepoRoot, "artifacts", p, "app", "plugins")).ToArray();
            var root = candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "NetPI.Tools.Files", "NetPI.Tools.Files.dll")))
                ?? throw new AssertException($"no built plugins in {string.Join(" or ", candidates)}: run build.ps1");
            foreach (var name in new[] { "NetPI.Tools.Files", "NetPI.Tools.Shell", "NetPI.Providers.AiProxy", "NetPI.Providers.Anthropic" })
                if (!File.Exists(Path.Combine(root, name, name + ".dll"))) throw new AssertException($"{name} is not built in {root}");
            await using var server = await StartAsync(root);
            var plugins = server.Plugins.List().ToDictionary(p => p.Id);
            foreach (var id in new[] { "netpi.tools.files", "netpi.tools.shell", "netpi.providers.aiproxy", "netpi.providers.anthropic" })
            {
                Check.True(plugins.ContainsKey(id), $"{id} discovered");
                Check.Equal("running", plugins[id].State, $"{id}: {plugins[id].Error}");
            }
            var tools = server.Kernel.Tools.All.Select(t => t.Definition.Name).ToHashSet();
            foreach (var t in new[] { "read", "write", "edit", "grep", "find", "ls", "bash", "process" })
                Check.True(tools.Contains(t), $"tool {t} registered");
            Check.True(server.Kernel.Tools.Registrations.Where(t => t.Tool.Definition.Name == "read").All(t => t.PluginId == "netpi.tools.files"));
            var providers = server.Models.Providers.Select(p => p.Id).ToHashSet();
            Check.True(providers.Contains("aiproxy") && providers.Contains("anthropic"), string.Join(",", providers));
            Check.True(server.Rpc.Exists("files.search") && server.Rpc.Exists("processes.list"));
            _ = await server.Models.ListAsync(); // offline providers must not throw

            // Reloading real plugins must not leak their load contexts through host-side references.
            foreach (var id in new[] { "netpi.tools.files", "netpi.tools.shell", "netpi.providers.aiproxy", "netpi.providers.anthropic" })
            {
                await server.Plugins.ReloadAsync(id);
                Check.Equal("running", server.Plugins.List().Single(p => p.Id == id).State, id);
            }
            foreach (var id in new[] { "netpi.tools.files", "netpi.tools.shell", "netpi.providers.aiproxy", "netpi.providers.anthropic" })
            {
                await Wait.Until(() => server.Kernel.Plugins.GetLoadState(id).LastUnloadCollected is not null, id + " unload check", 30_000);
                Check.Equal(true, server.Kernel.Plugins.GetLoadState(id).LastUnloadCollected, id + " old context collected");
            }
            Check.True(server.Kernel.Tools.All.Count(t => t.Definition.Name == "read") == 1);
            Check.True(server.Models.Providers.Count(p => p.Id == "aiproxy") == 1);
        });
    }

    // ------------------------------------------------------------------ helpers (no plugin object may outlive them)

    private static string PathUtilNormalize(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    private static (string Root, string PluginDir) PreparePluginDir(string variant)
    {
        var root = T.TempDir("plugins");
        var dir = Path.Combine(root, "SamplePlugin");
        T.CopyDir(SampleBuild.Dir(variant), dir);
        return (root, dir);
    }

    public static Task<NetPiServer> StartAsync(string pluginsRoot, string? webRoot = null) =>
        NetPiServer.StartAsync(new NetPiServerOptions
        {
            Port = 0,
            Home = T.TempDir("home"),
            ExtraPluginDirs = [pluginsRoot],
            WebRoot = webRoot ?? T.TempDir("web"),
            ConsoleLogging = Environment.GetEnvironmentVariable("NETPI_TEST_LOGS") == "1",
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string Inspect(NetPiServer server)
    {
        var k = server.Kernel;
        var tool = k.Tools.Get("sample_echo");
        var owner = k.Tools.Registrations.Where(t => t.Tool.Definition.Name == "sample_echo").Select(t => t.PluginId).Single();
        var sections = k.Services.GetAll<IPromptSection>().Count(s => s.Id == "sample");
        var tabs = k.Ui.Tabs.Count(t => t.PluginId == "test.sample" && t.Id == "sample");
        var result = tool!.ExecuteAsync(new ToolContext
        {
            SessionId = "s", AgentId = "a", CallId = "c", Cwd = Path.GetTempPath(), Services = k.Services, Events = k.Bus,
        }, JsonDocument.Parse("{}").RootElement, CancellationToken.None).Result;
        return $"{result.Content.TrimEnd(':')}|{owner}|{sections}|{tabs}";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? TabVersion(NetPiServer server) =>
        server.Kernel.Ui.Tabs.SingleOrDefault(t => t.PluginId == "test.sample")?.Version;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CurrentContext(NetPiServer server) =>
        server.Kernel.Plugins.GetLoadState("test.sample").Current ?? throw new AssertException("no load context");

    private static async Task<string?> TryValueAsync(NetPiServer server)
    {
        try { return (string?)await server.Rpc.InvokeAsync("sample.value"); }
        catch (RpcException) { return null; }
    }

    private static async Task<string> EchoJsonAsync(NetPiServer server) =>
        System.Text.Encoding.UTF8.GetString(Wire.SerializeValue(await server.Rpc.InvokeAsync("sample.echo", new { text = "x" })));

    private static async Task<string> PingAsync(NetPiServer server)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = server.Events.Subscribe("sample.pong", e =>
        {
            if (e.Source != "test.sample") tcs.TrySetException(new AssertException("scoped bus must stamp Source = plugin id, got " + e.Source));
            else tcs.TrySetResult(System.Text.Encoding.UTF8.GetString(Wire.SerializeValue(e.Data)));
        });
        server.Events.Publish("sample.ping");
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<bool> CollectedAsync(WeakReference weak)
    {
        for (var i = 0; i < 50 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);
        }
        return !weak.IsAlive;
    }

    public static async Task<string> HttpGetAsync(NetPiServer server, string path, bool auth = true)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + path);
        if (auth) req.Headers.Add(WebServer.TokenHeader, server.Token);
        using var res = await http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new AssertException($"GET {path} → {(int)res.StatusCode}: {body}");
        return body;
    }
}
