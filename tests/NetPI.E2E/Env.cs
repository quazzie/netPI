using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.MockLlm;

namespace NetPI.E2E;

public sealed class E2EOptions
{
    /// <summary>netpi-server port; 0 (the default) picks a free one per server, so two runs at once never meet on a port.</summary>
    public int Port { get; set; }
    /// <summary>MockLlm port; 0 picks a free one.</summary>
    public int MockPort { get; set; }
    /// <summary>App directory to run (default: a fresh copy of the build output, artifacts/dev/app, so plugin files can be touched safely).</summary>
    public string? AppDir { get; set; }
    public bool Keep { get; set; }
    public bool Verbose { get; set; }
    public double MockSpeed { get; set; } = 1;
    public int TinyContext { get; set; } = 12000;
    /// <summary>Where this run keeps what it leaves behind: report.json, failures/, screenshots/ (one folder per run).</summary>
    public string OutDir { get; set; } = "";
    public List<string> Filters { get; } = [];
    public List<string> Tags { get; } = [];
    public List<string> SkipTags { get; } = [];
    /// <summary>Shards (server + mock pairs) running tests at once; 0 = as many as the selection is worth.</summary>
    public int Parallel { get; set; }
    /// <summary>Every selected test this many times (finding out how often a flaky one fails).</summary>
    public int Repeat { get; set; } = 1;
    /// <summary>A fresh server for every test run, so none can see what another left behind.</summary>
    public bool Fresh { get; set; }
    /// <summary>Multiplies every test's timeout (a slow or busy machine).</summary>
    public double TimeoutScale { get; set; } = 1;
    /// <summary>Where the failing list and the timing history persist between runs; empty = nowhere.</summary>
    public string StateDir { get; set; } = "";

    public E2EOptions ForServer(int port, int mockPort)
    {
        var o = (E2EOptions)MemberwiseClone();
        o.Port = port;
        o.MockPort = mockPort;
        return o;
    }
}

/// <summary>Free TCP ports, taken together so that two asks never get the same one.</summary>
public static class Ports
{
    // ports this process handed out: a port freed a moment ago must not be handed to the next shard before its server binds it
    private static readonly HashSet<int> Given = [];

    public static int[] Pick(int count)
    {
        lock (Given)
        {
            var ports = new List<int>();
            while (ports.Count < count)
            {
                var listeners = new List<System.Net.Sockets.TcpListener>();
                try
                {
                    for (var i = 0; i < count - ports.Count; i++)
                    {
                        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                        l.Start();
                        listeners.Add(l);
                    }
                    foreach (var p in listeners.Select(l => ((System.Net.IPEndPoint)l.LocalEndpoint).Port))
                        if (Given.Add(p)) ports.Add(p);
                }
                finally { foreach (var l in listeners) l.Stop(); }
            }
            return ports.ToArray();
        }
    }
}

/// <summary>Where a test started: what to cut from the server log, the mock's request log and the client's events to explain a failure.</summary>
public readonly record struct DiagMark(long ServerLogBytes, long MockSeq, long ClientIndex);

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
    /// <summary>Screenshots of this run (run-specific, so two runs never write the same file).</summary>
    public string ScreenshotDir => Path.Combine(Options.OutDir.Length > 0 ? Options.OutDir : Root, "screenshots");
    /// <summary>How long each phase of starting this env took (copy, mock, server), for the report.</summary>
    public Dictionary<string, long> Setup { get; } = [];
    public string BaseUrl { get; private set; } = "";
    /// <summary>The server process (null once stopped).</summary>
    public int? ServerPid => _proc is { HasExited: false } p ? p.Id : null;
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
        if (o.Port == 0 || o.MockPort == 0)
        {
            var p = Ports.Pick(2);
            o = o.ForServer(o.Port == 0 ? p[0] : o.Port, o.MockPort == 0 ? p[1] : o.MockPort);
        }
        var repo = FindRepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "netpi-e2e", DateTime.Now.ToString("MMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4]);
        Directory.CreateDirectory(root);
        var env = new Env { Options = o, RepoRoot = repo, Root = root };
        try { await env.StartCoreAsync(); return env; }
        catch { await env.DisposeAsync(); throw; }
    }

    private async Task StartCoreAsync()
    {
        if (Options.Verbose) Log($"work dir: {Root}");
        var phase = Stopwatch.StartNew();
        Mock = await MockLlmServer.StartAsync(new MockOptions
        {
            Port = Options.MockPort, Speed = Options.MockSpeed, TinyContext = Options.TinyContext, AnthropicKey = "mock-anthropic-key",
            Verbose = Options.Verbose,
        });
        Setup["mock"] = phase.ElapsedMilliseconds;
        phase.Restart();

        // The server runs from a copy, so the hot-reload test can touch plugin files without disturbing other instances.
        // The source is the build output (artifacts/dev/app): a build never writes into the app folder a running
        // NetPI loads plugins from, so that is where a fresh build is. --app picks any other one.
        if (Options.AppDir is { } app) AppDir = Path.GetFullPath(app);
        else
        {
            AppDir = Path.Combine(Root, "app");
            string? src = null;
            foreach (var candidate in new[] { Path.Combine(RepoRoot, "artifacts", "dev", "app"), Path.Combine(RepoRoot, "artifacts", "app") })
                if (File.Exists(Path.Combine(candidate, "netpi-server.dll"))) { src = candidate; break; }
            if (src is null)
                throw new InvalidOperationException(
                    $"netpi-server is not built ({Path.Combine(RepoRoot, "artifacts", "dev", "app")} and artifacts/app): run build.ps1, or pass --app <dir>");
            CopyDir(src, AppDir);
        }
        Setup["copy"] = phase.ElapsedMilliseconds;

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
        Setup["server"] = sw.ElapsedMilliseconds;
        if (Options.Verbose) Log($"netpi-server pid {_proc.Id} ready at {BaseUrl} in {sw.ElapsedMilliseconds} ms");
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

    // ------------------------------------------------------------------ diagnostics (what a failed test leaves behind)

    public async Task<DiagMark> MarkAsync()
    {
        long bytes = 0;
        try { bytes = new FileInfo(ServerLog).Length; } catch { }
        long seq = 0;
        try { seq = await MockMark(); } catch { }
        return new DiagMark(bytes, seq, Client?.Mark() ?? 0);
    }

    /// <summary>
    /// What happened around a failed test, so the failure can be read without running it again: the server's log since the
    /// test started, the requests the mock model server got, and the last events the client heard. A section that has
    /// nothing says why, because a header followed by nothing reads as "nothing happened" and explains none of it.
    /// </summary>
    public async Task<string> DiagnoseAsync(DiagMark since)
    {
        var sb = new StringBuilder();
        try
        {
            string log;
            lock (_logGate)
            {
                using var fs = new FileStream(ServerLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(Math.Min(since.ServerLogBytes, fs.Length), SeekOrigin.Begin);
                log = new StreamReader(fs).ReadToEnd();
            }
            var lines = log.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            sb.AppendLine($"--- server log since the test started ({lines.Length} lines, last 120)");
            if (lines.Length == 0)
                sb.AppendLine("(the server wrote nothing since the test started. It logs at debug level: every model call, plugin reload, stall and failed RPC. " +
                              "A silent server was either not working on anything or not running at all; 'server state' above says which.)");
            foreach (var l in lines.TakeLast(120)) sb.AppendLine(l);
        }
        catch (Exception ex) { sb.AppendLine("--- server log unavailable"); sb.AppendLine(ex.Message); }
        try
        {
            var mock = await MockLog(since.MockSeq);
            sb.AppendLine($"--- mock model server requests since the test started ({mock.Count}, last 40)");
            if (mock.Count == 0)
                sb.AppendLine("(none. A test that never asks a model has none; for one that does, the server never got as far as calling the model.)");
            foreach (var e in mock.TakeLast(40))
            {
                var calls = string.Join(",", e.Arr("calls").Select(c => c.GetString()));
                var flags = string.Concat(e.B("dropped") ? " dropped" : "", e.B("cancelled") ? " cancelled" : "", e.B("subagent") ? " subagent" : "");
                sb.AppendLine($"#{e.L("seq")} {e.S("api")} {e.S("model")} scenario={e.S("scenario")} step={e.L("step")} status={e.L("status")}{(e.S("error") is { } err ? " error=" + err : "")}{flags} calls=[{calls}] last={e.S("lastRole")}: {Check.Show(e.S("lastText") ?? "")} ({e.L("durationMs")} ms)");
            }
        }
        catch (Exception ex) { sb.AppendLine("--- mock log unavailable"); sb.AppendLine(ex.Message); }
        try
        {
            var events = Client?.Since(since.ClientIndex) ?? [];
            sb.AppendLine($"--- client events since the test started ({events.Count}, last 40)");
            if (events.Count == 0)
                sb.AppendLine($"(none. The server published nothing this client heard{(Client is { Closed: true } ? "; the client's WebSocket is closed" : "; the WebSocket is open, so the server was silent")}.)");
            foreach (var e in events.TakeLast(40)) sb.AppendLine(Check.Show(e.ToString()));
        }
        catch (Exception ex) { sb.AppendLine("--- client events unavailable"); sb.AppendLine(ex.Message); }
        return sb.ToString();
    }

    /// <summary>
    /// Asks the server four small questions at once, each with its own short deadline, and says what the answers add up to.
    /// Nothing here can wait on the server for long: a wedged one costs <paramref name="probeMs"/>, not a probe each.
    /// </summary>
    public async Task<ServerTriage> TriageAsync(int probeMs = 2000, bool captureStacks = false)
    {
        var proc = _proc;
        if (proc is null) return ServerTriage.Classify(new ServerVitals(false), []);
        TimeSpan cpu0;
        try
        {
            if (proc.HasExited) return ServerTriage.Classify(new ServerVitals(false, proc.ExitCode), []);
            proc.Refresh();
            cpu0 = proc.TotalProcessorTime;
        }
        catch (Exception ex) { return ServerTriage.Of("the server process cannot be inspected: " + ex.Message); }
        // the probes are made from here: how long does THIS process take to run a trivial work item? (a starved runner blames a healthy server)
        var poolWatch = Stopwatch.StartNew();
        await Task.Run(() => { }).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var runnerPoolMs = poolWatch.ElapsedMilliseconds;
        var window = Stopwatch.StartNew();

        async Task<Probe> Ask(string name, Func<CancellationToken, Task> call)
        {
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(probeMs);
            try
            {
                await call(cts.Token).ConfigureAwait(false);
                return new Probe(name, true, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) { return new Probe(name, false, sw.ElapsedMilliseconds, $"no answer in {probeMs} ms"); }
            catch (Exception ex) { return new Probe(name, false, sw.ElapsedMilliseconds, ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); }
        }
        async Task HttpRpc(string method, string body, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/rpc/{method}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-NetPI-Token", Token);
            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
        }
        var asked = new List<Task<Probe>>
        {
            Ask(ServerTriage.Health, async ct => { using var r = await Http.GetAsync(BaseUrl + "/api/health", ct).ConfigureAwait(false); r.EnsureSuccessStatusCode(); }),
            Ask(ServerTriage.Rpc, ct => HttpRpc("app.info", "{}", ct)),
            Ask(ServerTriage.Db, ct => HttpRpc("sessions.list", "{\"limit\":1}", ct)),
        };
        if (Client is { Closed: false } client) asked.Add(Ask(ServerTriage.Ws, _ => client.Rpc("app.info", null, probeMs)));
        else if (Client is not null) asked.Add(Task.FromResult(new Probe(ServerTriage.Ws, false, 0, "the client's WebSocket is closed")));
        var probes = await Task.WhenAll(asked).ConfigureAwait(false);

        ServerVitals vitals;
        try
        {
            proc.Refresh();
            var seconds = Math.Max(0.05, window.Elapsed.TotalSeconds);
            var cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / seconds / Environment.ProcessorCount * 100;
            vitals = new ServerVitals(!proc.HasExited, proc.HasExited ? proc.ExitCode : null, proc.Threads.Count, proc.HandleCount, proc.WorkingSet64 / (1024 * 1024), cpu);
        }
        catch { vitals = new ServerVitals(!proc.HasExited); }
        var triage = ServerTriage.Classify(vitals, probes, runnerPoolMs);
        // A server that is alive and answers nothing (or not the database) is blocked somewhere: its threads' stacks say where.
        if (captureStacks && vitals.Alive && !triage.Healthy && probes.Any(p => p.Name != ServerTriage.Ws && !p.Ok))
            triage = triage.WithStacks(await StackCapture.CaptureAsync(proc.Id).ConfigureAwait(false));
        return triage;
    }

    /// <summary>
    /// A test has returned: waits for the agents it started to finish, then stops the ones that will not. Whatever was still
    /// running when the test returned is reported as a leak, because it goes on making model requests and changing state
    /// inside the NEXT test's window (that is how one test's background worker failed another test's check on the mock's log).
    /// </summary>
    public async Task<SettleResult> SettleAsync(int graceMs = 5000)
    {
        static bool Busy(JsonElement a) => a.S("status") is not ("idle" or "completed" or "failed" or "cancelled" or null);
        var sw = Stopwatch.StartNew();
        string? leak = null;
        var quietPolls = 0;
        try
        {
            List<JsonElement> busy;
            while (true)
            {
                busy = (await Client.Rpc("runs.list", new { includeFinished = false }, 5000)).Arr().Where(Busy).ToList();
                if (busy.Count == 0)
                {
                    if (leak is null) return SettleResult.Quiet;
                    // A finished worker's report wakes its parent: there is a gap between "the worker is done" and "the parent is
                    // running again" in which nothing looks busy. Quiet counts once it has stayed quiet for a few polls.
                    if (++quietPolls >= 4) return new SettleResult(leak + $"; they finished {sw.ElapsedMilliseconds - 4 * 80} ms later", true);
                }
                else
                {
                    quietPolls = 0;
                    leak ??= $"left {busy.Count} agent(s) running when it returned: " + string.Join(", ", busy.Select(a => $"{a.S("name")}:{a.S("status")}"));
                }
                if (sw.ElapsedMilliseconds >= graceMs) break;
                await Task.Delay(busy.Count == 0 ? 80 : 40);
            }
            foreach (var a in busy)
                if (a.S("sessionId") is { } sid) try { await Client.Rpc("agent.abort", new { sessionId = sid }, 5000); } catch (Exception) { }
            var quiet = false;
            for (var i = 0; i < 100 && !quiet; i++)
            {
                await Task.Delay(50);
                quiet = !(await Client.Rpc("runs.list", new { includeFinished = false }, 5000)).Arr().Any(Busy);
            }
            return new SettleResult(leak + $"; still running {graceMs} ms later, " + (quiet ? "stopped by the runner" : "and would not stop"), quiet);
        }
        catch (Exception ex)
        {
            // a test that stopped or restarted the server itself leaves nothing to settle; anything else is worth saying
            return Client is { Closed: true } || _proc is null ? SettleResult.Quiet : new SettleResult($"could not check for leftovers: {ex.Message.Split('\n')[0]}", false);
        }
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

    /// <summary>An RPC. When it times out, the message says whether the server is wedged and where, instead of only that it timed out.</summary>
    public async Task<JsonElement> Rpc(string method, object? p = null, int timeoutMs = 30_000)
    {
        try { return await Client.Rpc(method, p, timeoutMs); }
        catch (RpcTimeoutException ex)
        {
            ServerTriage t;
            try { t = await TriageAsync(); } catch (Exception inner) { t = ServerTriage.Of("not checked: " + inner.Message); }
            throw new RpcTimeoutException($"{ex.Message}\n      server: {t.Summary}");
        }
    }

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

    /// <summary>
    /// A background subagent was started in this session: wait until its report has reached the parent and the parent has dealt
    /// with it. The report either wakes an idle parent for one more run, or (when the worker is quick) arrives in the middle of a
    /// run and is answered by it, so the number of runs says nothing; what does is a parent that is idle with its own answer
    /// after the report. Returns the report's notice.
    /// </summary>
    public async Task<Ev> WaitReportHandled(string sessionId, long mark, int timeoutMs = 30_000)
    {
        var notice = await Client.WaitFor(mark, e => e.Type == "message.added" && e.Sid == sessionId
                                                    && e.D.P("message").P("meta").S("kind") == "agent-result", "the background report reaching its parent", timeoutMs);
        var noticeSeq = notice.D.P("message").L("seq");
        await Wait.UntilAsync(async () =>
        {
            if ((await Rpc("agent.get", new { sessionId })).S("status") is not "idle") return null;
            var last = (await Rpc("sessions.messages", new { id = sessionId, limit = 2000 })).Arr("messages").LastOrDefault();
            return last.ValueKind == JsonValueKind.Object && last.S("role") == "assistant" && last.L("seq") > noticeSeq ? "ok" : null;
        }, "the parent answering the background report and going idle", timeoutMs);
        return notice;
    }

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
        else Console.WriteLine($"  kept {Root}");
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
