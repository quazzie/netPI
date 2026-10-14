using System.Diagnostics;
using System.Text.Json;

namespace NetPI.E2E;

/// <summary>
/// The UI journeys: each one is a node script in tests/NetPI.E2E/ui (Playwright, on ui/lib.mjs), run by
/// <see cref="RunUiScript"/> against this shard's real server and MockLlm: something a person does and would notice
/// broken. What the server does is the C# tests' and the UI's pure logic web/test's (docs/TESTING.md, "UI journeys").
/// </summary>
public static class UiTests
{
    public static void Register(TestRunner r, Env env)
    {
        r.Add("ui.rpc-contract", "ui: every method the UI calls by name exists in the server's rpc.list (idea-yvcy8b)", async () =>
        {
            await RunUiScript(env, "rpc-contract.mjs", 45);
        }, 60);
        r.Add("ui.chat", "ui journey: open a chat, have the agent edit a file — the answer streams, the tool rows finish, the edit's diff, the final answer; every side-panel tab opens", async () =>
        {
            var p = await env.NewProject("ui-chat", CoreTests.Seed);
            var title = $"UI chat {Guid.NewGuid().ToString("N")[..6]}";
            var s = await env.NewSession(projectId: p.S("id"), title: title);
            // a chat with no messages is transient (not listed): give it one
            await env.Run(s.S("id")!, "hello [s:echo]");
            await RunUiScript(env, "chat.mjs", 300, "--session-id", s.S("id")!, "--session", title);
            Check.Equal("ALPHA-UI line\nbeta line\ngamma line\n", File.ReadAllText(Path.Combine(p.S("path")!, "notes.txt")), "the run the page started edited the file");
        }, 330);
        // The script makes its own chat; what it watches waits at MockLlm gates.
        r.Add("ui.chat-control", "ui journey: steer a running chat (Enter, a steer chip), queue a follow-up (Alt+Enter, a queued chip) and stop an answer with Esc (what was written stays)", () =>
            RunUiScript(env, "chat-control.mjs", 300), 330);
        // The script makes its own project and chat; the thinking waits at a MockLlm gate, the command for a file it writes.
        r.Add("ui.tools", "ui journey: the agent at work — thinking shown and opened while it streams, a shell command's output in its row while it runs, a dropped stream retried without its discarded text", () =>
            RunUiScript(env, "tools.mjs", 300), 330);
        // The script makes its own chat; it drops and restores the page's socket (tapSocket), the server keeps running.
        r.Add("ui.reconnect", "ui journey: the page loses its connection and gets it back — reconnecting shown, a run that ended meanwhile shows its stored answer and no half-written one, a streaming answer goes on with no gap, a new run reaches the page", () =>
            RunUiScript(env, "reconnect.mjs", 300), 330);
        // The script sets up its own agent (qwen, two instances) and removes it; its runs wait at MockLlm gates.
        r.Add("ui.work", "ui journey: the Work tab — an agent's two instances busy, a third chat queued and cancelled from the tab (it never starts), a finished run's row freed while the other keeps its row", () =>
            RunUiScript(env, "work.mjs", 300), 330);
        // The script changes settings and changes them back; keepsSettings fails it when the document differs after. It
        // fails one profiles.list call on purpose (tapSocket.intercept).
        r.Add("ui.settings", "ui journey: settings — a number saved, reset and refused out of range; an agent added from the model search, its instances set, removed after a confirm; a chat's profile picker stays open through a failed reload", () =>
            RunUiScript(env, "settings.mjs", 300), 330);
        // The script makes its own chat.
        r.Add("ui.asks", "ui journey: what a chat asks the user — the ask_user card survives a page reload, an option answers it, so does an answer typed in the message box, and a two-question card is sent with its button; the run goes on with the answer", () =>
            RunUiScript(env, "asks.mjs", 300), 330);
        // The script makes its own project; it starts with no chat tabs open (freshTabs).
        r.Add("ui.new-chat", "ui journey: a new chat from the start screen in a project — the chevron picks the project and starts the chat in it, the first answer lists it in the sessions panel, and the main button then starts the next chat in that project", () =>
            RunUiScript(env, "new-chat.mjs", 300), 330);
    }

    /// <summary>
    /// Runs ui/<paramref name="script"/> with node against this shard's server and mock (<c>--url --token --mock --app --work --out</c>,
    /// then <paramref name="args"/>) and echoes its check lines. It fails on a failed check, a missing result line (the script
    /// died), a non-zero exit or the timeout, naming every failed check, the browser's errors and stderr at once: stopping at
    /// the first made a run of ~25 s reveal one problem at a time. The runner's --timeout-scale stretches the timeout and goes
    /// to the script as NETPI_UI_SLOW, where it is the least its own waits are stretched by (lib.mjs SLOW).
    /// </summary>
    private static async Task RunUiScript(Env env, string script, int timeoutSeconds, params string[] args)
    {
        var name = Path.GetFileNameWithoutExtension(script);
        var scale = Math.Max(1, env.Options.TimeoutScale);
        timeoutSeconds = (int)Math.Ceiling(timeoutSeconds * scale);
        var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["NETPI_UI_SLOW"] = scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.ArgumentList.Add(Path.Combine(env.RepoRoot, "tests", "NetPI.E2E", "ui", script));
        foreach (var a in new[] { "--url", env.BaseUrl, "--token", Env.Token, "--mock", env.MockUrl, "--app", env.AppDir, "--work", env.Projects, "--out", env.ScreenshotDir }.Concat(args))
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { if (!proc.HasExited) proc.Kill(true); throw new AssertException($"ui {name} timed out after {timeoutSeconds} s"); }
        var output = await stdout;
        var err = await stderr;
        foreach (var line in output.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal))) Console.WriteLine("      " + line.Trim());
        var json = output.Split('\n').LastOrDefault(l => l.StartsWith("{\"ok\"", StringComparison.Ordinal));
        Check.True(json is not null, $"{name} printed no result line: " + output + err);
        using var doc = JsonDocument.Parse(json!);
        var browserErrors = string.Join(" | ", doc.RootElement.Arr("errors").Select(e => e.GetString()));
        var failed = doc.RootElement.Arr("checks").Where(c => !c.B("ok")).Select(c => $"ui check '{c.S("name")}' {c.S("detail")}").ToList();
        Check.True(failed.Count == 0, $"{failed.Count} ui check(s) failed:\n      " + string.Join("\n      ", failed)
            + (browserErrors.Length > 0 ? "\n      browser errors: " + browserErrors : ""));
        Check.Equal(0, proc.ExitCode, $"{name} exit code; browser errors: {browserErrors}; stderr: {err}");
        Env.Log($"screenshots: {env.ScreenshotDir}");
    }
}
