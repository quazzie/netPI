using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.MockLlm;

namespace NetPI.E2E;

public sealed class E2EOptions
{
    public int Port { get; set; } = 7470;
    public int MockPort { get; set; } = 7471;
    /// <summary>App directory to run (default: a fresh copy of artifacts/app, so plugin files can be touched safely).</summary>
    public string? AppDir { get; set; }
    public bool Keep { get; set; }
    public bool Ui { get; set; } = true;
    public bool Verbose { get; set; }
    public double MockSpeed { get; set; } = 1;
    public int TinyContext { get; set; } = 12000;
    public List<string> Filters { get; } = [];
}

/// <summary>A running netpi-server process (a copy of artifacts/app, own home) plus the mock model server.</summary>
public sealed class Env : IAsyncDisposable
{
    public const string Token = "e2e-token";

    private Process? _proc;
    private StreamWriter? _logWriter;
    private readonly Lock _logGate = new();

    public required E2EOptions Options { get; init; }
    public required string RepoRoot { get; init; }
    public required string Root { get; init; }
    public string AppDir { get; private set; } = "";
    public string Home => Path.Combine(Root, "home");
    public string Projects => Path.Combine(Root, "projects");
    public string ServerLog => Path.Combine(Root, "server.log");
    public string BaseUrl { get; private set; } = "";
    public int Port { get; private set; }
    public MockLlmServer Mock { get; private set; } = null!;
    public NetPiClient Client { get; private set; } = null!;
    public HttpClient Http { get; } = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public string MockUrl => $"http://127.0.0.1:{Options.MockPort}";

    public static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "NetPI.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root (NetPI.slnx) not found");
    }

    public static async Task<Env> StartAsync(E2EOptions o)
    {
        var repo = FindRepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "netpi-e2e", DateTime.Now.ToString("MMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4]);
        Directory.CreateDirectory(root);
        var env = new Env { Options = o, RepoRoot = repo, Root = root };
        await env.StartCoreAsync();
        return env;
    }

    private async Task StartCoreAsync()
    {
        Log($"work dir: {Root}");
        Mock = await MockLlmServer.StartAsync(new MockOptions
        {
            Port = Options.MockPort, Speed = Options.MockSpeed, TinyContext = Options.TinyContext, AnthropicKey = "mock-anthropic-key",
            Verbose = Options.Verbose,
        });

        // The server runs from a copy, so the hot-reload test can touch plugin files without disturbing other instances.
        if (Options.AppDir is { } app) AppDir = Path.GetFullPath(app);
        else
        {
            var src = Path.Combine(RepoRoot, "artifacts", "app");
            if (!File.Exists(Path.Combine(src, "netpi-server.dll"))) throw new InvalidOperationException($"netpi-server is not built ({src})");
            AppDir = Path.Combine(Root, "app");
            CopyDir(src, AppDir);
        }

        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Projects);
        var settings = new JsonObject
        {
            ["providers"] = new JsonObject
            {
                ["aiproxy"] = new JsonObject { ["baseUrl"] = MockUrl, ["modelsCacheSeconds"] = 2 },
                ["anthropic"] = new JsonObject { ["baseUrl"] = MockUrl + "/anthropic", ["apiKey"] = "mock-anthropic-key", ["modelsCacheSeconds"] = 5 },
            },
            ["retry"] = new JsonObject { ["baseDelayMs"] = 200, ["maxDelayMs"] = 1000 },
            ["logging"] = new JsonObject { ["level"] = "debug" },
        };
        await File.WriteAllTextAsync(Path.Combine(Home, "settings.json"), settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        await StartServerAsync();
    }

    public async Task StartServerAsync()
    {
        _logWriter = new StreamWriter(ServerLog, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Root,
        };
        foreach (var a in new[] { Path.Combine(AppDir, "netpi-server.dll"), "--port", Options.Port.ToString(), "--home", Home, "--token", Token })
            psi.ArgumentList.Add(a);
        psi.Environment["ANTHROPIC_API_KEY"] = "";
        psi.Environment["OPENROUTER_API_KEY"] = ""; // no real OpenRouter calls from a developer's environment
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc = proc;
        _proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            WriteLog(e.Data);
            var i = e.Data.IndexOf("NetPI is running at ", StringComparison.Ordinal);
            if (i >= 0) ready.TrySetResult(e.Data[(i + "NetPI is running at ".Length)..].Trim());
        };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) WriteLog("[stderr] " + e.Data); };
        _proc.Exited += (_, _) => ready.TrySetException(new InvalidOperationException($"netpi-server exited with code {proc.ExitCode} (see {ServerLog})"));
        var sw = Stopwatch.StartNew();
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
        BaseUrl = await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Port = new Uri(BaseUrl).Port;
        if (Port != Options.Port) throw new InvalidOperationException($"port {Options.Port} is busy (the server fell back to {Port})");
        Log($"netpi-server pid {_proc.Id} ready at {BaseUrl} in {sw.ElapsedMilliseconds} ms");
        Client = await NetPiClient.ConnectAsync(BaseUrl, Token);
        await Client.Subscribe("*");
    }

    /// <summary>Graceful stop (SIGTERM / Ctrl+C semantics). Returns the exit code.</summary>
    public async Task<int> StopServerAsync(TimeSpan timeout)
    {
        if (_proc is null) return -1;
        if (Client is not null) await Client.DisposeAsync();
        var p = _proc;
        _proc = null;
        if (!p.HasExited)
        {
            if (!OperatingSystem.IsWindows())
            {
                using var kill = Process.Start("kill", ["-TERM", p.Id.ToString()]);
                await kill!.WaitForExitAsync();
            }
            else p.Kill(entireProcessTree: true);
            using var cts = new CancellationTokenSource(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync();
                return -2;
            }
        }
        var code = p.ExitCode;
        p.Dispose();
        _logWriter?.Dispose();
        _logWriter = null;
        return code;
    }

    private void WriteLog(string line)
    {
        lock (_logGate) _logWriter?.WriteLine(line);
        if (Options.Verbose) Console.WriteLine("    | " + line);
    }

    public static void Log(string text) => Console.WriteLine("  " + text);

    public string ReadServerLog()
    {
        lock (_logGate)
        {
            using var fs = new FileStream(ServerLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // ------------------------------------------------------------------ mock control

    public async Task<JsonElement> MockStats() => await Http.GetFromJsonAsync<JsonElement>(MockUrl + "/_stats");

    public async Task<List<JsonElement>> MockLog(long since = 0) =>
        (await Http.GetFromJsonAsync<JsonElement>($"{MockUrl}/_log?since={since}")).EnumerateArray().Select(e => e.Clone()).ToList();

    public async Task<long> MockMark()
    {
        var log = await MockLog();
        return log.Count == 0 ? 0 : log.Max(e => e.L("seq"));
    }

    public async Task MockReset() => (await Http.PostAsync(MockUrl + "/_reset", null)).EnsureSuccessStatusCode();

    /// <summary>The raw request body the mock received as request <paramref name="seq"/> (see <see cref="MockLog"/>).</summary>
    public async Task<JsonElement> MockRequest(long seq)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{MockUrl}/_request/{seq}"));
        return doc.RootElement.Clone();
    }

    /// <summary>What was sent before is sent again unchanged: same instructions, earlier input items as a prefix.</summary>
    public static void PrefixKept(JsonElement earlier, JsonElement later, string what)
    {
        Check.Equal(earlier.S("instructions"), later.S("instructions"), $"{what}: instructions unchanged");
        var a = earlier.Arr("input").ToList();
        var b = later.Arr("input").ToList();
        Check.True(b.Count >= a.Count, $"{what}: input only grows");
        for (var i = 0; i < a.Count; i++) Check.Equal(a[i].GetRawText(), b[i].GetRawText(), $"{what}: input item {i} unchanged");
    }

    // ------------------------------------------------------------------ NetPI helpers

    public Task<JsonElement> Rpc(string method, object? p = null, int timeoutMs = 30_000) => Client.Rpc(method, p, timeoutMs);

    public string NewProjectDir(string name)
    {
        var dir = Path.Combine(Projects, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async Task<JsonElement> NewProject(string name, Action<string>? seed = null)
    {
        var dir = NewProjectDir(name);
        seed?.Invoke(dir);
        return await Rpc("projects.create", new { name, path = dir });
    }

    public async Task<JsonElement> NewSession(string? model = null, string? projectId = null, string? title = null, string? reasoning = null) =>
        await Rpc("sessions.create", new { title = title ?? "e2e", projectId, model, reasoning });

    /// <summary>Send a message and wait until the session's agent is idle again. Returns the run's events and the transcript.</summary>
    public async Task<RunResult> Run(string sessionId, string text, int timeoutMs = 60_000, string? mode = null)
    {
        var mark = Client.Mark();
        var agent = await Rpc("agent.send", new { sessionId, text, mode });
        var runs = agent.L("runs");
        var done = await WaitIdle(sessionId, mark, runs, timeoutMs);
        return await Result(sessionId, mark, done);
    }

    /// <summary>Wait until the streamed text (stream.delta, kind text) of the session contains <paramref name="text"/>.</summary>
    public async Task WaitStreamed(string sessionId, long mark, string text, int timeoutMs = 20_000) =>
        await Wait.Until(() => string.Concat(Client.Since(mark, e => e.Type == "stream.delta" && e.Sid == sessionId && e.D.S("kind") == "text")
            .Select(e => e.D.S("text"))).Contains(text, StringComparison.Ordinal), $"streamed text '{text}'", timeoutMs);

    public async Task<Ev> WaitIdle(string sessionId, long mark, long minRuns, int timeoutMs = 60_000) =>
        await Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").S("sessionId") == sessionId
                                        && e.D.P("agent").S("status") is "idle" or "completed" or "failed" or "cancelled"
                                        && e.D.P("agent").L("runs") >= minRuns,
            $"agent of {sessionId} to become idle (run {minRuns})", timeoutMs);

    public async Task<RunResult> Result(string sessionId, long mark, Ev? done = null)
    {
        var page = await Rpc("sessions.messages", new { id = sessionId, limit = 2000 });
        var all = page.Arr("messages").ToList();
        return new RunResult
        {
            SessionId = sessionId,
            Events = Client.Since(mark, e => e.Sid == sessionId || e.D.P("sessionId").GetString0() == sessionId
                                             || e.D.P("agent").S("sessionId") == sessionId),
            Messages = all.Where(m => !RunResult.IsContextNotice(m)).ToList(),
            AllMessages = all,
            Final = done?.D.P("agent") ?? default,
        };
    }

    /// <summary>
    /// How bash's pwd prints the directory it was started in. Git Bash maps it through its mount table: the user's temp
    /// folder (where the E2E work dir lives) is /tmp, other paths are /c/Users/….
    /// </summary>
    public static string BashPath(string dir)
    {
        if (!OperatingSystem.IsWindows() || dir.Length < 2 || dir[1] != ':') return dir;
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        if (dir.StartsWith(temp + "\\", StringComparison.OrdinalIgnoreCase)) return "/tmp/" + dir[(temp.Length + 1)..].Replace('\\', '/');
        return "/" + char.ToLowerInvariant(dir[0]) + dir[2..].Replace('\\', '/');
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopServerAsync(TimeSpan.FromSeconds(10)); } catch { }
        try { await Mock.DisposeAsync(); } catch { }
        Http.Dispose();
        if (!Options.Keep)
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
        else Log($"kept {Root}");
    }
}

public sealed class RunResult
{
    public required string SessionId { get; init; }
    public required List<Ev> Events { get; init; }
    /// <summary>The transcript without the context notices (working directory, instruction files, tools, skills), which have their own tests.</summary>
    public required List<JsonElement> Messages { get; init; }
    /// <summary>The whole transcript.</summary>
    public required List<JsonElement> AllMessages { get; init; }
    public JsonElement Final { get; init; }

    public static bool IsContextNotice(JsonElement m) =>
        m.S("role") == "notice" && m.P("meta").S("kind") is "project" or "instructions" or "tools" or "skills";

    public IEnumerable<JsonElement> Role(string role) => Messages.Where(m => m.S("role") == role);
    public JsonElement LastAssistant => Messages.LastOrDefault(m => m.S("role") == "assistant");
    public static string Text(JsonElement m) => string.Join("\n", m.Arr("parts").Where(p => p.S("type") == "text").Select(p => p.S("text")));
    public string FinalText => Text(LastAssistant);
    public IEnumerable<JsonElement> Parts(string type) => Messages.SelectMany(m => m.Arr("parts")).Where(p => p.S("type") == type);
    public List<string> Types => Events.Select(e => e.Type).ToList();
    public IEnumerable<Ev> OfType(string type) => Events.Where(e => e.Type == type);
}

internal static class JsonExt
{
    public static string? GetString0(this JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
