using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Tools.Shell;

namespace NetPI.Tools.Tests;

// ------------------------------------------------------------------ mini test framework

public sealed class AssertException(string message) : Exception(message);

public static class Check
{
    public static void True(bool condition, string message = "expected true", [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition) throw new AssertException($"{message} ({expr})");
    }

    public static void False(bool condition, string message = "expected false", [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (condition) throw new AssertException($"{message} ({expr})");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{message ?? "not equal"}\n      expected: {Show(expected)}\n      actual:   {Show(actual)}");
    }

    public static void Contains(string haystack, string needle, string? message = null)
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{message ?? "substring not found"}: {Show(needle)}\n      in: {Show(haystack)}");
    }

    public static void NotContains(string haystack, string needle, string? message = null)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{message ?? "unexpected substring"}: {Show(needle)}\n      in: {Show(haystack)}");
    }

    public static TEx Throws<TEx>(Action body, string? message = null) where TEx : Exception
    {
        try { body(); }
        catch (TEx ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"{message ?? "wrong exception"}: expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertException($"{message ?? "no exception"}: expected {typeof(TEx).Name}");
    }

    public static void Ok(ToolResult r)
    {
        if (r.IsError) throw new AssertException($"tool returned an error: {r.Content}");
    }

    public static void Error(ToolResult r, string? contains = null)
    {
        if (!r.IsError) throw new AssertException($"expected an error, got: {Show(r.Content)}");
        if (contains is not null) Contains(r.Content, contains);
    }

    public static string Show(object? o)
    {
        var s = o?.ToString() ?? "null";
        s = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length > 600 ? s[..600] + "…" : s;
    }
}

public sealed class TestRunner
{
    /// <summary>Exit code when the filter selected no test at all: a broken selection, not a pass (idea-jw74xi). The
    /// documented direct run is <c>dotnet &lt;suite&gt;.dll [filter]</c>, and it must not report green for a typo or a
    /// renamed test. scripts/test.ps1 maps a non-zero exit with no FAIL line to a failure.</summary>
    public const int NoTestSelected = 2;

    private readonly List<(string Name, Func<Task> Body)> _tests = [];

    public void Add(string name, Func<Task> body) => _tests.Add((name, body));
    public void Add(string name, Action body) => _tests.Add((name, () => { body(); return Task.CompletedTask; }));

    public async Task<int> RunAsync(string[] filters)
    {
        var selected = _tests.Where(t => filters.Length == 0 || filters.Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        int passed = 0, failed = 0;
        var failures = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var (name, body) in selected)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var task = body();
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(90))) != task)
                    throw new AssertException("test timed out after 90s");
                await task;
                passed++;
                Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                failed++;
                var msg = ex is AssertException ? ex.Message : ex.ToString();
                failures.Add($"{name}: {msg}");
                Console.WriteLine($"  FAIL  {name} ({sw.ElapsedMilliseconds}ms)\n        {msg.Replace("\n", "\n        ")}");
            }
        }
        Console.WriteLine();
        var nothing = selected.Count == 0;
        if (nothing) Console.WriteLine("No test matches the filter.");
        Console.WriteLine($"{passed} passed, {failed} failed, {selected.Count} total in {total.Elapsed.TotalSeconds:0.0}s");
        if (failed > 0)
        {
            Console.WriteLine("Failures:");
            foreach (var f in failures) Console.WriteLine("  - " + f.Split('\n')[0]);
        }
        return failed > 0 ? 1 : nothing ? NoTestSelected : 0;
    }
}

// ------------------------------------------------------------------ fakes

public sealed class FakeServices : IServiceRegistry
{
    private readonly List<(object Instance, int Priority)> _items = [];
    public IDisposable Register<T>(T instance, int priority = 0) where T : class
    {
        _items.Add((instance, priority));
        return new Disposer(() => _items.RemoveAll(i => ReferenceEquals(i.Instance, instance)));
    }
    public T? Get<T>() where T : class => GetAll<T>().FirstOrDefault();
    public IReadOnlyList<T> GetAll<T>() where T : class => _items.Where(i => i.Instance is T).OrderByDescending(i => i.Priority).Select(i => (T)i.Instance).ToList();
}

public sealed class FakeBus : IEventBus
{
    public ConcurrentQueue<BusEvent> Events { get; } = new();
    public void Publish(BusEvent evt) => Events.Enqueue(evt);
    public IDisposable Subscribe(string pattern, Action<BusEvent> handler) => new Disposer(() => { });
    public IDisposable SubscribeAsync(string pattern, Func<BusEvent, ValueTask> handler) => new Disposer(() => { });
    public IReadOnlyList<BusEvent> Recent(int max = 200) => Events.TakeLast(max).ToList();
    public List<BusEvent> OfType(string type) => Events.Where(e => e.Type == type).ToList();
}

public sealed class FakeSettings : ISettings
{
    private readonly JsonObject _root = new();
    public string FilePath => "(memory)";
    public bool InvalidOnDisk => false;
    public string? InvalidOnDiskError => null;
    public JsonObject Snapshot() => (JsonObject)_root.DeepClone();

    public JsonNode? GetNode(string path)
    {
        JsonNode? node = _root;
        foreach (var part in path.Split('.'))
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(part, out node)) return null;
        }
        return node;
    }

    public T? Get<T>(string path, T? defaultValue = default)
    {
        var node = GetNode(path);
        if (node is null) return defaultValue;
        try { return node.Deserialize<T>(NetPiJson.Options) ?? defaultValue; } catch { return defaultValue; }
    }

    public void Set(string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var o = _root;
        foreach (var part in parts[..^1])
        {
            if (o[part] is not JsonObject child) { child = new JsonObject(); o[part] = child; }
            o = child;
        }
        o[parts[^1]] = value;
    }

    public void Replace(JsonObject root) => throw new NotSupportedException();
}

public sealed class FakeTools : IToolRegistry
{
    public List<IAgentTool> Tools { get; } = [];
    public IDisposable Register(IAgentTool tool, int priority = 0) { Tools.Add(tool); return new Disposer(() => Tools.Remove(tool)); }
    public IReadOnlyList<IAgentTool> All => Tools;
    public IAgentTool? Get(string name) => Tools.FirstOrDefault(t => t.Definition.Name == name);
    public IReadOnlyList<ToolRegistration> Registrations => Tools.Select(t => new ToolRegistration(t, "test", 0)).ToList();
}

public sealed class FakeRpc : IRpcRegistry
{
    public Dictionary<string, RpcHandler> Handlers { get; } = [];
    public Dictionary<string, bool> ReadOnly { get; } = [];
    public IDisposable Register(string method, RpcHandler handler, string? description = null) => Register(method, handler, description, false);
    public IDisposable Register(string method, RpcHandler handler, string? description, bool readOnly)
    {
        Handlers[method] = handler;
        ReadOnly[method] = readOnly;
        return new Disposer(() => { Handlers.Remove(method); ReadOnly.Remove(method); });
    }
    public Task<object?> InvokeAsync(string method, object? parameters = null, CancellationToken ct = default) =>
        Handlers[method](new RpcRequest { Method = method, Params = NetPiJson.ToElement(parameters ?? new { }) }, ct);
    public IReadOnlyList<RpcMethodInfo> List() => Handlers.Keys.Select(k => new RpcMethodInfo(k, null, "test", ReadOnly.GetValueOrDefault(k))).ToList();
    public bool Exists(string method) => Handlers.ContainsKey(method);
}

public sealed class FakePluginContext(string workspace) : IPluginContext
{
    public string PluginId => "test";
    public string PluginDirectory => workspace;
    public NetPiPaths Paths { get; } = new()
    {
        AppDir = workspace, Home = workspace, LogsDir = workspace, WebRoot = workspace, SettingsFile = "settings.json",
        DatabaseFile = "db", TempDir = workspace, PluginDirs = [], DefaultWorkspace = workspace,
    };
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
    public IDatabase Db => null!;
    /// <summary>Set by a test that needs a session (the workspace-aware roots); null otherwise, like before.</summary>
    public ISessionStore SessionsFake { get; set; } = null!;
    public ISessionStore Sessions => SessionsFake;
    public IModelCatalog Models => null!;
    public CancellationToken Stopping => CancellationToken.None;
    public T Track<T>(T disposable) where T : IDisposable => disposable;
}

public sealed class FakeUi : IUiRegistry
{
    private readonly List<UiTabInfo> _tabs = [];
    private readonly List<SlashCommandInfo> _commands = [];
    public IDisposable AddTab(UiTabInfo tab) { _tabs.Add(tab); return new Disposer(() => _tabs.Remove(tab)); }
    public IDisposable AddCommand(SlashCommandInfo command) { _commands.Add(command); return new Disposer(() => _commands.Remove(command)); }
    public IReadOnlyList<UiTabInfo> Tabs => _tabs;
    public IReadOnlyList<SlashCommandInfo> Commands => _commands;
}

public sealed class Disposer(Action action) : IDisposable
{
    public void Dispose() => action();
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

    /// <summary>True if the file grew during the sample window: the child is running.</summary>
    public async Task<bool> IsAlive(int windowMs = 700)
    {
        var before = Bytes();
        await Task.Delay(windowMs);
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

    public static ToolContext Ctx(string cwd, ModelInfo? model = null, Action<string>? output = null, IEventBus? bus = null) => Ctx(cwd, null, model, output, bus);

    /// <summary>A tool context for a session bound to a workspace (the workspace-aware file and shell tools).</summary>
    public static ToolContext Ctx(string cwd, WorkspaceBinding? workspace, ModelInfo? model = null, Action<string>? output = null, IEventBus? bus = null) => new()
    {
        SessionId = "ses_test",
        AgentId = "agt_test",
        CallId = "call_" + Ids.Short(),
        Cwd = cwd,
        Workspace = workspace,
        Model = model,
        Services = Services,
        Events = bus ?? new FakeBus(),
        Output = output,
    };

    private static ToolContext CtxUnused(string cwd, ModelInfo? model = null, Action<string>? output = null, IEventBus? bus = null) => new()
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

    public static JsonElement Args(object o) => o is string s ? JsonDocument.Parse(s).RootElement.Clone() : NetPiJson.ToElement(o);

    public static Task<ToolResult> Run(IAgentTool tool, string cwd, object args, ModelInfo? model = null, CancellationToken ct = default) =>
        tool.ExecuteAsync(Ctx(cwd, model), Args(args), ct);

    /// <summary>Details as a JSON element (what the UI receives).</summary>
    public static JsonElement D(ToolResult r) => NetPiJson.ToElement(r.Details);

    public static string Str(this JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.String ? e.GetProperty(name).GetString()! : e.GetProperty(name).GetRawText();
    public static int Int(this JsonElement e, string name) => e.GetProperty(name).GetInt32();
    public static bool Bool(this JsonElement e, string name) => e.GetProperty(name).GetBoolean();
    public static bool Has(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    /// <summary>
    /// Root for this run's temporary files. Scoped to the process (or to <c>NETPI_TEST_ROOT</c>) so two
    /// runs of the same suite cannot delete each other's directories.
    /// </summary>
    public static string TestRoot { get; } = Environment.GetEnvironmentVariable("NETPI_TEST_ROOT") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Path.GetTempPath(), "netpi-tests", Environment.ProcessId.ToString());

    public static string TempDir(string prefix)
    {
        var dir = Path.Combine(TestRoot, prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

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
