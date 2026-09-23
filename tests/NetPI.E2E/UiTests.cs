using System.Diagnostics;
using System.Text.Json;

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
}
