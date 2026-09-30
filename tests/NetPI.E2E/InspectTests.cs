using System.Diagnostics;
using System.Text.Json;

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
    }

    private static async Task<(int Code, string Output, string Error)> NodeAsync(Env env, params string[] args)
    {
        var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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
