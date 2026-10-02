using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.E2E;

/// <summary>Playwright smoke test of the real web UI against the real server + mock model (tests/NetPI.E2E/ui/smoke.mjs).</summary>
public static class UiTests
{
    public static void Register(TestRunner r, Env env)
    {
        r.Add("ui.mcp-panel", "ui: MCP catalog controls fit narrow panels and failed saves retain edits", async () =>
        {
            var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(Path.Combine(env.RepoRoot, "tests", "NetPI.E2E", "ui", "mcp-smoke.mjs"));
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync(); var stderr = proc.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch { if (!proc.HasExited) proc.Kill(true); throw; }
            Check.Equal(0, proc.ExitCode, await stdout + await stderr);
        });
        r.Add("ui.files-mount", "ui: the Files tab's teardown runs — unmounting it stops the focus listener, and three remounts leave none behind (idea-1zs9go)", async () =>
        {
            // No server needed: the script mounts the committed plugin bundle itself (the one that ships) with a stub
            // ctx, then unmounts it the way PluginTabHost does — a plugin load bumps UiVersion and remounts the tab.
            var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(Path.Combine(env.RepoRoot, "tests", "NetPI.E2E", "ui", "files-mount.mjs"));
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync(); var stderr = proc.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch { if (!proc.HasExited) proc.Kill(true); throw; }
            var output = await stdout;
            var err = await stderr;
            foreach (var line in output.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal))) Console.WriteLine("      " + line.Trim());
            var json = output.Split('\n').LastOrDefault(l => l.StartsWith("{\"ok\"", StringComparison.Ordinal));
            Check.True(json is not null, "files-mount output: " + output + err);
            using var doc = JsonDocument.Parse(json!);
            var failedChecks = doc.RootElement.Arr("checks").Where(c => !c.B("ok")).Select(c => $"ui check '{c.S("name")}' {c.S("detail")}").ToList();
            Check.True(failedChecks.Count == 0, $"{failedChecks.Count} ui check(s) failed:\n      " + string.Join("\n      ", failedChecks)
                + (doc.RootElement.Arr("errors").Any() ? "\n      browser errors: " + string.Join(" | ", doc.RootElement.Arr("errors").Select(e => e.GetString())) : ""));
            Check.Equal(0, proc.ExitCode, "files-mount exit code; stderr: " + err);
        }, 120);
        r.Add("ui.idea-conflict", "ui: the Ideas tab saves with the revision its editor was opened on — a stale save is a conflict, not an overwrite, and an open editor survives a status change (idea-c3hihl)", async () =>
        {
            // No server needed: the script mounts the committed plugin bundle itself with a stub ctx whose
            // ideas.update enforces the same expectedRevision rule as the host.
            var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(Path.Combine(env.RepoRoot, "tests", "NetPI.E2E", "ui", "idea-conflict.mjs"));
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync(); var stderr = proc.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch { if (!proc.HasExited) proc.Kill(true); throw; }
            var output = await stdout;
            var err = await stderr;
            foreach (var line in output.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal))) Console.WriteLine("      " + line.Trim());
            var json = output.Split('\n').LastOrDefault(l => l.StartsWith("{\"ok\"", StringComparison.Ordinal));
            Check.True(json is not null, "idea-conflict output: " + output + err);
            using var doc = JsonDocument.Parse(json!);
            var failedChecks = doc.RootElement.Arr("checks").Where(c => !c.B("ok")).Select(c => $"ui check '{c.S("name")}' {c.S("detail")}").ToList();
            Check.True(failedChecks.Count == 0, $"{failedChecks.Count} ui check(s) failed:\n      " + string.Join("\n      ", failedChecks)
                + (doc.RootElement.Arr("errors").Any() ? "\n      browser errors: " + string.Join(" | ", doc.RootElement.Arr("errors").Select(e => e.GetString())) : ""));
            Check.Equal(0, proc.ExitCode, "idea-conflict exit code; stderr: " + err);
        }, 120);
        r.Add("ui.smoke", "ui: send [s:tools] in the browser, streamed text + tool rows, plugin tabs, no console errors (screenshots)", async () =>
        {
            var p = await env.NewProject("ui-demo", CoreTests.Seed);
            var s = await env.NewSession(projectId: p.S("id"), title: "UI smoke");
            // a session with no messages is transient (not listed): give the prepared session one
            await env.Run(s.S("id")!, "hello [s:echo]");
            await SeedIdeas(env, p.S("id")!, s.S("id")!);
            var script = Path.Combine(env.RepoRoot, "tests", "NetPI.E2E", "ui", "smoke.mjs");
            var outDir = env.ScreenshotDir;
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
            // every failed check at once: stopping at the first made a run of ~25 s reveal one problem at a time
            var failedChecks = doc.RootElement.Arr("checks").Where(c => !c.B("ok")).Select(c => $"ui check '{c.S("name")}' {c.S("detail")}").ToList();
            Check.True(failedChecks.Count == 0, $"{failedChecks.Count} ui check(s) failed:\n      " + string.Join("\n      ", failedChecks)
                + (doc.RootElement.Arr("errors").Any() ? "\n      browser errors: " + string.Join(" | ", doc.RootElement.Arr("errors").Select(e => e.GetString())) : ""));
            Check.Equal(0, proc.ExitCode, "smoke exit code; stderr: " + err);
            Check.Equal("ALPHA-UI line\nbeta line\ngamma line\n", File.ReadAllText(Path.Combine(p.S("path")!, "notes.txt")), "the UI run edited the file");
            Env.Log($"screenshots: {outDir}");
        }, 180);
        r.Add("ui.idea-image", "ui: a failed image attach toasts its error and the Image button comes back — no undefined toast, attaching resets (idea-r7j411)", async () =>
        {
            var p = await env.NewProject("idea-img", CoreTests.Seed);
            var s = await env.NewSession(projectId: p.S("id"), title: "Idea image");
            await env.Run(s.S("id")!, "hello [s:echo]");
            var script = Path.Combine(env.RepoRoot, "tests", "NetPI.E2E", "ui", "idea-image.mjs");
            var outDir = env.ScreenshotDir;
            var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { script, "--url", env.BaseUrl, "--token", Env.Token, "--session", "Idea image", "--out", outDir })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { if (!proc.HasExited) proc.Kill(true); throw new AssertException("ui idea-image timed out"); }
            var output = await stdout;
            var err = await stderr;
            foreach (var line in output.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal))) Console.WriteLine("      " + line.Trim());
            var json = output.Split('\n').LastOrDefault(l => l.StartsWith("{\"ok\"", StringComparison.Ordinal));
            Check.True(json is not null, "idea-image output: " + output + err);
            using var doc = JsonDocument.Parse(json!);
            var failedChecks = doc.RootElement.Arr("checks").Where(c => !c.B("ok")).Select(c => $"ui check '{c.S("name")}' {c.S("detail")}").ToList();
            Check.True(failedChecks.Count == 0, $"{failedChecks.Count} ui check(s) failed:\n      " + string.Join("\n      ", failedChecks)
                + (doc.RootElement.Arr("errors").Any() ? "\n      browser errors: " + string.Join(" | ", doc.RootElement.Arr("errors").Select(e => e.GetString())) : ""));
            Check.Equal(0, proc.ExitCode, "idea-image exit code; stderr: " + err);
            Env.Log($"screenshots: {outDir}");
        }, 120);
        r.Add("ui.pinned-sessions", "ui: pin a session — a Pinned group above the recency groups, and unpinning removes it (idea-k8nghc)", async () =>
        {
            // Unique per run: a shared server (shards, -Repeat) keeps sessions from earlier runs, so a fixed title
            // would make the "exactly one such row" assertions race. NewProject already suffixes the project.
            var stamp = Guid.NewGuid().ToString("N")[..6];
            var p = await env.NewProject("pin-demo", CoreTests.Seed);
            var s = await env.NewSession(projectId: p.S("id"), title: $"Pin me {stamp}");
            await env.Run(s.S("id")!, "hello [s:echo]");
            // a second, newer session: the pinned one must be recognisable as the exception in the recency order
            var n = await env.NewSession(projectId: p.S("id"), title: $"Just newer {stamp}");
            await env.Run(n.S("id")!, "hi [s:echo]");
            var script = Path.Combine(env.RepoRoot, "tests", "NetPI.E2E", "ui", "pin.mjs");
            var outDir = env.ScreenshotDir;
            var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { script, "--url", env.BaseUrl, "--token", Env.Token, "--session", $"Pin me {stamp}", "--out", outDir })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { if (!proc.HasExited) proc.Kill(true); throw new AssertException("ui pinned-sessions timed out"); }
            var output = await stdout;
            var err = await stderr;
            foreach (var line in output.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal))) Console.WriteLine("      " + line.Trim());
            var json = output.Split('\n').LastOrDefault(l => l.StartsWith("{\"ok\"", StringComparison.Ordinal));
            Check.True(json is not null, "pin output: " + output + err);
            using var doc = JsonDocument.Parse(json!);
            var failedChecks = doc.RootElement.Arr("checks").Where(c => !c.B("ok")).Select(c => $"ui check '{c.S("name")}' {c.S("detail")}").ToList();
            Check.True(failedChecks.Count == 0, $"{failedChecks.Count} ui check(s) failed:\n      " + string.Join("\n      ", failedChecks)
                + (doc.RootElement.Arr("errors").Any() ? "\n      browser errors: " + string.Join(" | ", doc.RootElement.Arr("errors").Select(e => e.GetString())) : ""));
            Check.Equal(0, proc.ExitCode, "pin exit code; stderr: " + err);
            Env.Log($"screenshots: {outDir}");
        }, 120);
        r.Add("ui.remove-agent", "ui: settings → agents → remove agent — a confirmed removal removes the agent from the list and the settings document", async () =>
        {
            // Unique per run: a shared server (shards, -Repeat) keeps settings from earlier runs, so a fixed id
            // could collide with a leftover. The C# side seeds it; the script drives the UI and cleans up after.
            var agent = $"e2e-rm-{Guid.NewGuid().ToString("N")[..6]}";
            await env.Rpc("settings.set", new { path = $"agents.{agent}", value = new { model = CoreTests.Qwen, use = "removed by the ui.remove-agent test" } });
            try
            {
                var script = Path.Combine(env.RepoRoot, "tests", "NetPI.E2E", "ui", "remove-agent.mjs");
                var outDir = env.ScreenshotDir;
                var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in new[] { script, "--url", env.BaseUrl, "--token", Env.Token, "--agent", agent, "--out", outDir })
                    psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi)!;
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { if (!proc.HasExited) proc.Kill(true); throw new AssertException("ui remove-agent timed out"); }
                var output = await stdout;
                var err = await stderr;
                foreach (var line in output.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal))) Console.WriteLine("      " + line.Trim());
                var json = output.Split('\n').LastOrDefault(l => l.StartsWith("{\"ok\"", StringComparison.Ordinal));
                Check.True(json is not null, "remove-agent output: " + output + err);
                using var d = JsonDocument.Parse(json!);
                foreach (var line in d.RootElement.Arr("dbg").Select(e => e.GetString())) Console.WriteLine("      [dbg] " + line);
                var failedChecks = d.RootElement.Arr("checks").Where(c => !c.B("ok")).Select(c => $"ui check '{c.S("name")}' {c.S("detail")}").ToList();
                Check.True(failedChecks.Count == 0, $"{failedChecks.Count} ui check(s) failed:\n      " + string.Join("\n      ", failedChecks)
                    + (d.RootElement.Arr("errors").Any() ? "\n      browser errors: " + string.Join(" | ", d.RootElement.Arr("errors").Select(e => e.GetString())) : ""));
                Check.Equal(0, proc.ExitCode, "remove-agent exit code; stderr: " + err);
                Env.Log($"screenshots: {outDir}");
            }
            finally
            {
                await env.Rpc("settings.set", new { path = $"agents.{agent}", value = (object?)null });
            }
        }, 120);
        r.Add("ui.work-tab", "ui: work tab — one list of agents with a row per instance, no Runs, Model capacity, Physical owners or Idea checks sections; the agent name opens Settings → Agents on its dialog", async () =>
        {
            // The agent the script clicks on, unique per run (shared servers keep settings between runs).
            var agent = $"e2e-wt-{Guid.NewGuid().ToString("N")[..6]}";
            await env.Rpc("settings.set", new { path = $"agents.{agent}", value = new { model = CoreTests.Qwen, use = "the e2e work-tab test's agent" } });
            try
            {
                var script = Path.Combine(env.RepoRoot, "tests", "NetPI.E2E", "ui", "work-tab.mjs");
                var outDir = env.ScreenshotDir;
                var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in new[] { script, "--url", env.BaseUrl, "--token", Env.Token, "--agent", agent, "--out", outDir })
                    psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi)!;
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { if (!proc.HasExited) proc.Kill(true); throw new AssertException("ui work-tab timed out"); }
                var output = await stdout;
                var err = await stderr;
                foreach (var line in output.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal))) Console.WriteLine("      " + line.Trim());
                var json = output.Split('\n').LastOrDefault(l => l.StartsWith("{\"ok\"", StringComparison.Ordinal));
                Check.True(json is not null, "work-tab output: " + output + err);
                using var d = JsonDocument.Parse(json!);
                var failedChecks = d.RootElement.Arr("checks").Where(c => !c.B("ok")).Select(c => $"ui check '{c.S("name")}' {c.S("detail")}").ToList();
                Check.True(failedChecks.Count == 0, $"{failedChecks.Count} ui check(s) failed:\n      " + string.Join("\n      ", failedChecks)
                    + (d.RootElement.Arr("errors").Any() ? "\n      browser errors: " + string.Join(" | ", d.RootElement.Arr("errors").Select(e => e.GetString())) : ""));
                Check.Equal(0, proc.ExitCode, "work-tab exit code; stderr: " + err);
                Env.Log($"screenshots: {outDir}");
            }
            finally
            {
                await env.Rpc("settings.set", new { path = $"agents.{agent}", value = (object?)null });
            }
        }, 120);
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
