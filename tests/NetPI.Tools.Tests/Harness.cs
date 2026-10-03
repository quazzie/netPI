using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Sessions;
using NetPI.Host.Storage.Memory;
using NetPI.Tools.Shell;

using SharedT = NetPI.TestShared.T;

namespace NetPI.Tools.Tests;

// The mini test framework (Check, TestRunner, the temp-dir T and TestGit) is the shared harness, linked from
// tests/Shared. The rest is this suite's: the fakes, the shell children and the tool-context builders.

/// <summary>Tool-result assertions (the shared Check has no notion of a <see cref="ToolResult"/>).</summary>
public static class ToolCheck
{
    public static void Ok(ToolResult r)
    {
        if (r.IsError) throw new AssertException($"tool returned an error: {r.Content}");
    }

    public static void Error(ToolResult r, string? contains = null)
    {
        if (!r.IsError) throw new AssertException($"expected an error, got: {Check.Show(r.Content)}");
        if (contains is not null) Check.Contains(r.Content, contains);
    }
}

// The recording fakes (FakeServices, FakeBus, FakeSettings, FakeTools, FakeRpc, FakeUi, Disposer) are
// the shared ones, linked from tests/Shared/Fakes.cs. The context below stays: what is real under it differs
// from the other suites' (the memory provider and the real SessionService).

/// <summary>
/// A plugin context over the kernel's own code: a real <c>memory</c> store and a real <see cref="SessionService"/>
/// over it, so what a plugin sees of a session is what production gives it. The registry, bus, RPC and tool
/// registries stay doubles — those are the harness's, not storage's.
/// </summary>
public sealed class FakePluginContext : IPluginContext, IDisposable
{
    private readonly IStorage _storage;

    public string Home { get; }
    public string PluginId => "test";
    public string PluginDirectory => Home;
    public NetPiPaths Paths { get; }

    public FakePluginContext(string workspace, string? home = null)
    {
        Home = home ?? workspace;
        Directory.CreateDirectory(Home);
        Paths = new()
        {
            AppDir = Home, Home = Home, LogsDir = Home, WebRoot = Home, SettingsFile = "settings.json",
            TempDir = Home, PluginDirs = [], DefaultWorkspace = workspace,
        };
        _storage = new MemoryStorageProvider().Open(new StorageOpenOptions
        {
            Home = Home, Logger = NullLogger.Instance, Settings = SettingsFake,
        });
        Store = new SessionService(_storage, Bus, workspace);
    }

    /// <summary>The store behind <see cref="Data"/> and <see cref="Sessions"/>; dispose the context to release it.</summary>
    public IStorage Storage => _storage;
    /// <summary>The kernel's session service over <see cref="Storage"/> (projects, sessions, messages, the fork rules).</summary>
    internal SessionService Store { get; }
    public ILogger Logger => NullLogger.Instance;
    public FakeBus Bus { get; } = new();
    public IEventBus Events => Bus;
    public IServiceRegistry Services { get; } = new FakeServices();
    public FakeRpc RpcFake { get; } = new();
    public IRpcRegistry Rpc => RpcFake;
    public FakeTools ToolsFake { get; } = new();
    public IToolRegistry Tools => ToolsFake;
    public FakeUi UiFake { get; } = new();
    public IUiRegistry Ui => UiFake;
    public IHttpRegistry Http => null!;
    public FakeSettings SettingsFake { get; } = new();
    public ISettings Settings => SettingsFake;
    public IPluginData Data => _storage.Plugins.For(PluginId);
    public ISessionStore Sessions => Store;
    public IModelCatalog Models => null!;
    public CancellationToken Stopping => CancellationToken.None;
    public T Track<T>(T disposable) where T : IDisposable => disposable;
    public void Dispose() => _storage.Dispose();
}

// ------------------------------------------------------------------ shell children

/// <summary>
/// A background child a shell test started, with an identity the test can actually check.
/// <para>
/// <c>sleep 60 &amp; echo $!</c> reports an <b>MSYS</b> pid, not a Windows pid. On this machine MSYS 3031
/// was native 14944, and <c>Process.GetProcessById(3031)</c> <i>throws</i> — so a check written as
/// "is the pid from $! still alive?" always answered "no" and the test passed while the child lived on,
/// holding the runner's stdout pipe open for the rest of its sleep. These tests therefore judge by side
/// effect: the child appends to a file for as long as it lives, so growth is proof of life and the end
/// of growth is proof it was killed. <see cref="NativePid"/> resolves the real Windows pid (through
/// <c>ps -W</c>) so a test can still kill a survivor it did create on purpose.
/// </para>
/// <para>
/// Always dispose it, including after a failed assertion — a survivor outlives the suite otherwise.
/// </para>
/// </summary>
public sealed class ShellChild : IDisposable
{
    private readonly ShellService _svc;
    private readonly string _dir;
    private int? _nativePid;
    private bool _disposed;

    public string Name { get; }
    /// <summary>Appended to while the child lives; its size is the liveness signal.</summary>
    public string AliveFile { get; }
    private string MsysPidFile { get; }

    private ShellChild(ShellService svc, string dir, string name)
    {
        _svc = svc; _dir = dir; Name = name;
        AliveFile = Path.Combine(dir, name + ".alive").Replace('\\', '/');
        MsysPidFile = Path.Combine(dir, name + ".pid").Replace('\\', '/');
    }

    /// <summary>Describe a child that <see cref="Preamble"/> will start in <paramref name="dir"/>.</summary>
    public static ShellChild For(ShellService svc, string dir, string name) => new(svc, dir, name);

    /// <summary>Bash to put in front of a test command: starts the child and records its pid.</summary>
    public string Preamble =>
        $"( while true; do echo x >> {AliveFile}; sleep 0.2; done ) & echo $! > {MsysPidFile}; ";

    /// <summary>The MSYS pid the shell reported ($!). Not a Windows pid; do not pass it to Process.</summary>
    public string MsysPid => File.Exists(MsysPidFile) ? File.ReadAllText(MsysPidFile).Trim() : "";

    /// <summary>The Windows pid, resolved through <c>ps -W</c>. Null while it is unknown or already gone.</summary>
    public int? NativePid => _nativePid;

    private long Bytes()
    {
        try { return File.Exists(AliveFile) ? new FileInfo(AliveFile).Length : 0; } catch { return 0; }
    }

    /// <summary>
    /// True if the file grows within the window: the child is running. It answers the moment the file grows, so a long window
    /// costs nothing for a child that lives (a loaded machine can starve its 0.2 s loop for a second or more); only "it is
    /// gone" waits the whole window, so a test that asserts death passes a short one.
    /// </summary>
    public async Task<bool> IsAlive(int windowMs = 700)
    {
        var before = Bytes();
        var until = DateTime.UtcNow.AddMilliseconds(windowMs);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(50);
            if (Bytes() > before) return true;
        }
        return Bytes() > before;
    }

    /// <summary>Resolve the Windows pid while the child is still running (so it can be killed later).</summary>
    public async Task RememberNativePid()
    {
        if (_nativePid is not null) return;
        var msys = MsysPid;
        if (msys.Length == 0) return;
        var win = await Bash($"ps -W | awk -v p={msys} '$1==p{{print $4}}'");
        if (int.TryParse(win.Trim(), out var id) && id > 0) _nativePid = id;
    }

    private async Task<string> Bash(string command)
    {
        var res = await T.Run(new ShellTool("bash", _svc), _dir, new { command });
        return res.Content;
    }

    /// <summary>Kill a survivor the test created on purpose. Best effort: it is cleanup, not an assertion.</summary>
    public void Kill()
    {
        var id = _nativePid;
        if (id is { } pid && pid > 0)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
    }
}

// ------------------------------------------------------------------ helpers

public static class T
{
    public static readonly FakeServices Services = new();

    public static ToolContext Ctx(string cwd, ModelInfo? model = null, Action<string>? output = null, IEventBus? bus = null) => Build(cwd, null, model, output, bus);

    /// <summary>A tool context for a session bound to a workspace (the workspace-aware file and shell tools).
    /// The binding travels in the context's feature bag, which is how the runtime hands it to a tool.</summary>
    public static ToolContext Ctx(string cwd, WorkspaceBinding? workspace, ModelInfo? model = null, Action<string>? output = null, IEventBus? bus = null) => Build(cwd, workspace, model, output, bus);

    private static ToolContext Build(string cwd, WorkspaceBinding? workspace, ModelInfo? model, Action<string>? output, IEventBus? bus)
    {
        var ctx = new ToolContext
        {
            SessionId = "ses_test",
            AgentId = "agt_test",
            CallId = "call_" + Ids.Short(),
            Cwd = cwd,
            Model = model,
            Services = Services,
            Events = bus ?? new FakeBus(),
            Output = output,
        };
        ctx.Features.Set(workspace);
        return ctx;
    }

    public static JsonElement Args(object o) => o is string s ? JsonDocument.Parse(s).RootElement.Clone() : NetPiJson.ToElement(o);

    public static Task<ToolResult> Run(IAgentTool tool, string cwd, object args, ModelInfo? model = null, CancellationToken ct = default) =>
        tool.ExecuteAsync(Ctx(cwd, model), Args(args), ct);

    /// <summary>Details as a JSON element (what the UI receives).</summary>
    public static JsonElement D(ToolResult r) => NetPiJson.ToElement(r.Details);

    public static string Str(this JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.String ? e.GetProperty(name).GetString()! : e.GetProperty(name).GetRawText();
    public static int Int(this JsonElement e, string name) => e.GetProperty(name).GetInt32();
    public static bool Bool(this JsonElement e, string name) => e.GetProperty(name).GetBoolean();
    public static bool Has(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    // The shared harness owns the root and the temp dirs (tests/Shared/Harness.cs); this T keeps the suite's builders.
    /// <summary>Root for this run's temporary files (the shared one: per process, or NETPI_TEST_ROOT).</summary>
    public static string TestRoot => SharedT.TestRoot;

    public static string TempDir(string prefix) => SharedT.TempDir(prefix);

    public static string WriteBytes(string dir, string rel, byte[] data)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return p;
    }

    public static string WriteText(string dir, string rel, string text, bool bom = false) =>
        WriteBytes(dir, rel, bom ? [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(text)] : System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Write a file in a specific encoding (the huge-file read path has to detect what a file really is).</summary>
    public static string WriteText(string dir, string rel, string text, System.Text.Encoding encoding) =>
        WriteBytes(dir, rel, encoding.GetBytes(text));

    public static string ReadRaw(string path) => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
}
