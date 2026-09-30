using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.E2E;

/// <summary>Playwright smoke test of the real web UI against the real server + mock model (tests/NetPI.E2E/ui/smoke.mjs).</summary>
public static class UiTests
{
    public static void Register(TestRunner r, Env env)
    {
        r.Add("ui: send [s:tools] in the browser, streamed text + tool rows, plugin tabs, no console errors (screenshots)", async () =>
        {
            var p = await env.NewProject("ui-demo", CoreTests.Seed);
            var s = await env.NewSession(projectId: p.S("id"), title: "UI smoke");
            // a session with no messages is transient (not listed): give the prepared session one
            await env.Run(s.S("id")!, "hello [s:echo]");
            await SeedIdeas(env, p.S("id")!, s.S("id")!);
            var script = Path.Combine(env.RepoRoot, "tests", "NetPI.E2E", "ui", "smoke.mjs");
            var outDir = Path.Combine(env.RepoRoot, "tests", "NetPI.E2E", "screenshots");
            var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { script, "--url", env.BaseUrl, "--token", Env.Token, "--session", "UI smoke", "--out", outDir })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { proc.Kill(true); throw new AssertException("ui smoke timed out"); }
            var output = await stdout;
            var err = await stderr;
            foreach (var line in output.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal))) Console.WriteLine("      " + line.Trim());
            var json = output.Split('\n').LastOrDefault(l => l.StartsWith("{\"ok\"", StringComparison.Ordinal));
            Check.True(json is not null, "smoke output: " + output + err);
            using var doc = JsonDocument.Parse(json!);
            foreach (var c in doc.RootElement.Arr("checks"))
                Check.True(c.B("ok"), $"ui check '{c.S("name")}' {c.S("detail")}");
            Check.Equal(0, proc.ExitCode, "smoke exit code; stderr: " + err);
            Check.Equal("ALPHA-UI line\nbeta line\ngamma line\n", File.ReadAllText(Path.Combine(p.S("path")!, "notes.txt")), "the UI run edited the file");
            Env.Log($"screenshots: {outDir}");
        }, 180);
    }

    /// <summary>Two ideas in the session's project, so the smoke can check the calm overview: status groups, one line per
    /// idea, the summary and meta row only once a card is open (idea-43oruq). Seeded through the RPC, which is how the
    /// backlog is filled now: writing an ideas.json into the home does nothing (the cutover has already run).</summary>
    private static async Task SeedIdeas(Env env, string projectId, string sessionId)
    {
        await env.Rpc("ideas.add", new { projectId, sessionId, idea = new { title = "Smoke idea A", summary = "SMOKE-SUMMARY-A", status = "in-progress" } });
        await env.Rpc("ideas.add", new { projectId, sessionId, idea = new { title = "Smoke idea B", summary = "SMOKE-SUMMARY-B", status = "open" } });
    }
}
