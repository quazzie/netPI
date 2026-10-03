using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetPI.E2E;

/// <summary>Inspecting the running app from outside (docs/DEBUGGING.md): server.json, the diag.* RPCs, scripts/netpi.mjs.</summary>
public static class InspectTests
{
    public static void Register(TestRunner r, Env env)
    {
        r.Add("inspect.diag", "inspect: server.json, diag.* over HTTP after a real run, and scripts/netpi.mjs with its read-only guard", async () =>
        {
            // how tools find this run
            using var info = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(env.Home, "server.json")));
            Check.Equal(env.BaseUrl, info.RootElement.S("url"));
            Check.Equal(Env.Token, info.RootElement.S("token"));

            var sid = (await env.NewSession()).S("id")!;
            var run = await env.Run(sid, "Hello, inspector [s:echo]");
            Check.Contains(run.FinalText, "ECHO-DONE");

            // the model call: when it started, its first token, how long, the tokens
            var calls = (await env.Rpc("diag.calls", new { sessionId = sid })).Arr().ToList();
            Check.True(calls.Count >= 1, "the run's model call is recorded");
            var call = calls[0];
            Check.Equal("ok", call.S("state"));
            Check.Equal("agent", call.S("purpose"));
            // the mock may send its first delta at once: 0 ms is a valid first token
            Check.True(call.TryGetProperty("firstTokenMs", out var first) && first.ValueKind == JsonValueKind.Number, "the first token is recorded");
            Check.True(call.L("durationMs") > 0 && call.L("durationMs") >= call.L("firstTokenMs"), $"first token {call.L("firstTokenMs")} ms, {call.L("durationMs")} ms in all");
            Check.True(call.L("outputTokens") > 0, "tokens from the usage");
            var detail = await env.Rpc("diag.call", new { id = call.L("id") });
            Check.Contains(detail.P("request").S("lastUser"), "Hello, inspector");

            // the run in depth, and its timeline without the per-token events
            var deep = await env.Rpc("diag.run", new { sessionId = sid });
            Check.Equal(sid, deep.P("session").S("id"));
            Check.True(deep.Arr("messages").Any(m => m.S("role") == "assistant"), "the chat's messages");
            Check.True(deep.Arr("calls").Any(), "the chat's calls");
            var journal = (await env.Rpc("diag.journal", new { sessionId = sid, limit = 1000 })).Arr().ToList();
            Check.False(journal.Any(e => e.S("type") == "stream.delta"), "no per-token events");
            Check.True(journal.Any(e => e.S("type") == "message.added") && journal.Any(e => e.S("type") == "agent.status"), "the events that matter");

            // secrets stay inside
            var settings = await env.Rpc("diag.settings");
            Check.Equal("<secret, 18 chars>", settings.P("settings").P("providers").P("anthropic").S("apiKey"));

            var overview = await env.Rpc("diag.overview");
            Check.True(overview.P("process").L("pid") > 0);
            Check.True(overview.Arr("more").Any(m => (m.GetString() ?? "").StartsWith("diag.calls: ", StringComparison.Ordinal)), "the overview points at the other diag methods");
            Check.True(overview.P("problems").ValueKind == JsonValueKind.Array);

            // the CLI: reads freely, refuses to change anything without --write, says when nothing runs
            var (code, output, err) = await NodeAsync(env, "--home", env.Home, "diag.calls", $"sessionId={sid}", "limit=1", "--compact");
            Check.Equal(0, code, err);
            Check.Contains(output, sid);
            (code, output, err) = await NodeAsync(env, "--home", env.Home, "diag.problems");
            Check.Equal(0, code, err);
            using (var problems = JsonDocument.Parse(output)) Check.True(problems.RootElement.ValueKind == JsonValueKind.Array);
            (code, _, err) = await NodeAsync(env, "--home", env.Home, "settings.set", "path=ui.inspect", "value=1");
            Check.Equal(1, code);
            Check.Contains(err, "--write");
            (code, _, err) = await NodeAsync(env, "--home", Path.Combine(env.Root, "no-such-home"));
            Check.Equal(2, code);
            Check.Contains(err, "isn't running");
        }, 60);

        r.Add("inspect.triage", "harness: a healthy server is classified responsive, a frozen one as wedged within seconds, and its RPC timeout says so", async () =>
        {
            var healthy = await env.TriageAsync();
            Check.True(healthy.Healthy, "a working server is responsive: " + healthy.Describe());
            Check.Equal(4, healthy.Probes.Count, "the four probes were asked: " + healthy.Describe());
            Check.True(healthy.Probes.All(p => p.Ok && p.Ms < 1500), "and each answered quickly: " + healthy.Describe());
            Check.True(healthy.Vitals.Threads > 0 && healthy.Vitals.WorkingSetMb > 0, "the process vitals were read: " + healthy.Describe());

            // frozen from outside: not crashed, not slow, simply never scheduled again. This is the shape of the wedge that
            // used to end as "RPC projects.create timed out after 30000ms", six times in a row.
            var pid = env.ServerPid ?? throw new AssertException("the server is not running");
            Freeze(pid, true);
            RpcTimeoutException? timeout = null;
            var sw = Stopwatch.StartNew();
            try
            {
                try { await env.Rpc("app.info", null, 500); }
                catch (RpcTimeoutException ex) { timeout = ex; }
            }
            finally { Freeze(pid, false); }
            Check.True(timeout is not null, "an RPC to a frozen server times out");
            Check.True(sw.ElapsedMilliseconds < 6000, $"and the verdict arrives with it, not after another timeout ({sw.ElapsedMilliseconds} ms)");
            Check.Contains(timeout!.Message, "timed out after 500ms", "the message keeps the timeout");
            Check.Contains(timeout.Message, "server: wedged: the process is alive but does not even answer /api/health", "and says what is wrong with the server");
            Check.Contains(timeout.Message, "idle", "a frozen process burns no CPU");

            // and when it runs again it is fine, and the RPC works
            var back = await Wait.UntilAsync(async () => (await env.TriageAsync()) is { Healthy: true } t ? t : null, "the server answers again", 15_000);
            Check.True(back.Healthy, back.Describe());
            Check.True((await env.Rpc("app.info")).S("home") is { Length: > 0 }, "an ordinary RPC works again");
        }, 60);
        r.Add("inspect.protocol-docs", "docs: every RPC method the server registers is in docs/PROTOCOL.md, and no method row names one that is gone (idea-yvcy8b)", async () =>
        {
            var live = (await env.Rpc("rpc.list")).Arr().Select(m => m.S("method")!).ToHashSet(StringComparer.Ordinal);
            var loaded = (await env.Rpc("plugins.list")).Arr().Where(p => p.B("loaded") || p.S("state") is "loaded" or "running")
                .Select(p => p.S("id")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var doc = File.ReadAllText(Path.Combine(env.RepoRoot, "docs", "PROTOCOL.md")).Replace("\r\n", "\n");
            var (missing, stale) = ProtocolDrift(doc, live, loaded);
            Check.True(missing.Count == 0, $"{missing.Count} method(s) the server registers are not in docs/PROTOCOL.md: {string.Join(", ", missing)}");
            Check.True(stale.Count == 0, $"{stale.Count} method row(s) in docs/PROTOCOL.md name a method the server does not have: {string.Join(", ", stale)}");
        });
    }

    /// <summary>
    /// The drift between the live method list and docs/PROTOCOL.md. Missing: a registered method the document never names
    /// in backticks. Stale: a row of a method table (the first cell a backticked dotted name, outside the Events section)
    /// whose method is not registered — skipped when the row's plugin column names a plugin this server did not load.
    /// </summary>
    internal static (List<string> Missing, List<string> Stale) ProtocolDrift(string doc, HashSet<string> live, HashSet<string> loadedPlugins)
    {
        // a name alone in backticks, or one that opens a code span with its parameters (`mcp.save {id,config}`)
        var named = Regex.Matches(doc, @"`([a-z]\w*(?:\.\w+)+)[`\s]").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var missing = live.Where(m => !named.Contains(m)).Order(StringComparer.Ordinal).ToList();
        var stale = new List<string>();
        var section = "";
        foreach (var line in doc.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)) { section = line[3..].Trim(); continue; }
            if (section.StartsWith("Events", StringComparison.Ordinal) || section.StartsWith("Data shapes", StringComparison.Ordinal)
                || section.StartsWith("WebSocket", StringComparison.Ordinal) || section.StartsWith("Plugin UI", StringComparison.Ordinal)) continue;
            var row = Regex.Match(line, @"^\|\s*`([a-z]\w*(?:\.\w+)+)`\s*\|([^|]*)\|");
            // desktop.* is the WinForms shell's own (src/NetPI.Desktop), registered only when the host runs inside it
            if (!row.Success || live.Contains(row.Groups[1].Value) || row.Groups[1].Value.StartsWith("desktop.", StringComparison.Ordinal)) continue;
            var plugin = row.Groups[2].Value.Trim().Trim('`');
            if (plugin.StartsWith("netpi.", StringComparison.Ordinal) && !loadedPlugins.Contains(plugin)) continue;
            stale.Add(row.Groups[1].Value);
        }
        return (missing, stale);
    }

    /// <summary>Stops (or resumes) every thread of a process, as if it had hung: the process stays alive and answers nothing.</summary>
    private static void Freeze(int pid, bool frozen)
    {
        if (OperatingSystem.IsWindows())
        {
            using var p = Process.GetProcessById(pid);
            var rc = frozen ? NtSuspendProcess(p.Handle) : NtResumeProcess(p.Handle);
            if (rc != 0) throw new AssertException($"could not {(frozen ? "suspend" : "resume")} process {pid} (NTSTATUS {rc:x})");
        }
        else
        {
            using var kill = Process.Start("kill", [frozen ? "-STOP" : "-CONT", pid.ToString()])!;
            kill.WaitForExit();
        }
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr handle);

    private static async Task<(int Code, string Output, string Error)> NodeAsync(Env env, params string[] args)
    {
        var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(Path.Combine(env.RepoRoot, "scripts", "netpi.mjs"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await proc.WaitForExitAsync(cts.Token);
        return (proc.ExitCode, await stdout, await stderr);
    }
}
