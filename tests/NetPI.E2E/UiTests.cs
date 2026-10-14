using System.Diagnostics;
using System.Text.Json;

namespace NetPI.E2E;

/// <summary>
/// The browser tests: each one is a node script in tests/NetPI.E2E/ui (Playwright), run by <see cref="RunUiScript"/> against
/// this shard's real server and MockLlm. Most share ui/lib.mjs; the ones that say "no server needed" mount a committed plugin
/// bundle in a fixture page instead.
/// </summary>
public static class UiTests
{
    public static void Register(TestRunner r, Env env)
    {
        r.Add("ui.mcp-panel", "ui: MCP catalog controls fit narrow panels and failed saves retain edits", async () =>
        {
            // No server needed: a fixture page with a stub ctx.
            await RunUiScript(env, "mcp-smoke.mjs", 45);
        });
        r.Add("ui.rpc-contract", "ui: every method the UI calls by name exists in the server's rpc.list (idea-yvcy8b)", async () =>
        {
            await RunUiScript(env, "rpc-contract.mjs", 45);
        }, 60);
        r.Add("ui.files-mount", "ui: the Files tab's teardown runs — unmounting it stops the focus listener, and three remounts leave none behind (idea-1zs9go)", async () =>
        {
            // No server needed: the script mounts the committed plugin bundle itself (the one that ships) with a stub
            // ctx, then unmounts it the way PluginTabHost does — a plugin load bumps UiVersion and remounts the tab.
            await RunUiScript(env, "files-mount.mjs", 90);
        }, 120);
        r.Add("ui.idea-conflict", "ui: an Ideas card opens the idea dialog on the current revision (idea-c3hihl), a status change claims none, and the play button starts a chat", async () =>
        {
            // No server needed: the script mounts the committed plugin bundle itself with a stub ctx whose
            // ideas.update enforces the same expectedRevision rule as the host.
            await RunUiScript(env, "idea-conflict.mjs", 90);
        }, 120);
        r.Add("ui.idea-meaning", "ui: the Ideas tab's ≈ toggle ranks ideas by meaning (ideas.similar) when the server has embeddings, is absent without them, and switching it off is word search again (idea-61wg9p)", async () =>
        {
            // No server needed: the script mounts the committed plugin bundle with a stub ctx that answers ideas.similar.
            await RunUiScript(env, "idea-meaning.mjs", 90);
        }, 120);
        r.Add("ui.schedules-tab", "ui: the Schedules tab - row states (ran/skipped/missed/running/paused/finished), pause, the finished-once guard, the confirm delete, the edit form's update payload, the schedules.changed reload and the 230px narrow panel (idea-1y1174)", async () =>
        {
            // No server needed: the script mounts the committed plugin bundle with a stub ctx that answers schedules.*.
            await RunUiScript(env, "schedules-tab.mjs", 90);
        }, 120);
        r.Add("ui.webhooks-tab", "ui: the Webhooks tab - the receiver banner, row states, the add form's payload, the one-time token box with its curl line, Preview/Send of a payload, pause, the confirm delete and re-key, the webhooks.changed reload and the 230px narrow panel", async () =>
        {
            // No server needed: the script mounts the committed plugin bundle with a stub ctx that answers webhooks.*.
            await RunUiScript(env, "webhooks-tab.mjs", 90);
        }, 120);
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
        // The script changes settings and changes them back; keepsSettings fails it when the document differs after.
        r.Add("ui.settings-next", "ui journey: settings — a number saved, reset and refused out of range; an agent added from the model search, its instances set, removed after a confirm", () =>
            RunUiScript(env, "settings.next.mjs", 300), 330);
        // The script makes its own chat.
        r.Add("ui.asks", "ui journey: what a chat asks the user — the ask_user card survives a page reload, an option answers it, so does an answer typed in the message box, and a two-question card is sent with its button; the run goes on with the answer", () =>
            RunUiScript(env, "asks.mjs", 300), 330);
        // The script makes its own project; it starts with no chat tabs open (freshTabs).
        r.Add("ui.new-chat", "ui journey: a new chat from the start screen in a project — the chevron picks the project and starts the chat in it, the first answer lists it in the sessions panel, and the main button then starts the next chat in that project", () =>
            RunUiScript(env, "new-chat.mjs", 300), 330);
        r.Add("ui.smoke", "ui: send [s:tools] in the browser, streamed text + tool rows, plugin tabs, no console errors (screenshots)", async () =>
        {
            var p = await env.NewProject("ui-demo", CoreTests.Seed);
            var s = await env.NewSession(projectId: p.S("id"), title: "UI smoke");
            // a session with no messages is transient (not listed): give the prepared session one
            await env.Run(s.S("id")!, "hello [s:echo]");
            await SeedIdeas(env, p.S("id")!, s.S("id")!);
            await RunUiScript(env, "smoke.mjs", 150, "--session", "UI smoke");
            Check.Equal("ALPHA-UI line\nbeta line\ngamma line\n", File.ReadAllText(Path.Combine(p.S("path")!, "notes.txt")), "the UI run edited the file");
        }, 180);
        r.Add("ui.idea-image", "ui: a failed image attach toasts its error and the Image button comes back — no undefined toast, attaching resets (idea-r7j411)", async () =>
        {
            var p = await env.NewProject("idea-img", CoreTests.Seed);
            var s = await env.NewSession(projectId: p.S("id"), title: "Idea image");
            await env.Run(s.S("id")!, "hello [s:echo]");
            await RunUiScript(env, "idea-image.mjs", 90, "--session", "Idea image");
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
            await RunUiScript(env, "pin.mjs", 90, "--session", $"Pin me {stamp}");
        }, 120);
        r.Add("ui.close-empty-session", "ui: closing a chat with no messages takes it out of the sessions list; one with messages keeps its row", async () =>
        {
            // Unique per run: a shared server (shards, -Repeat) keeps sessions from earlier runs, so the script counts rows
            // and finds the seeded one by a title nothing else has.
            var stamp = Guid.NewGuid().ToString("N")[..6];
            var keep = await env.NewSession(title: $"Keep me {stamp}");
            await env.Run(keep.S("id")!, "hello [s:echo]");
            await RunUiScript(env, "close-empty-session.mjs", 90, "--session", $"Keep me {stamp}");
        }, 120);

        r.Add("ui.start-project", "ui: the start screen's project button is a split button — its name starts a session in that project, its chevron picks another one", async () =>
        {
            // Unique per run: a shared server (shards, -Repeat) keeps projects between runs, so the button's label and the
            // picker's rows would race a fixed project name.
            var name = $"e2e-split-{Guid.NewGuid().ToString("N")[..6]}";
            await env.NewProject(name);
            await RunUiScript(env, "start-project.mjs", 120, "--project", name);
        }, 150);

        r.Add("ui.projects", "ui: projects — the composer's project picker attaches and changes the project with a notice, new chats start without one, the start screen remembers the last project, the projects dialog (keyboard, its sessions and instruction files, rename, remove confirm, a project made from the picker) and the folder picker", async () =>
        {
            // The script makes its own projects and chats (names unique per run) and removes them again.
            await RunUiScript(env, "projects.mjs", 150);
        }, 180);

        r.Add("ui.settings", "ui: settings — fields with defaults, reset and range, agents (added from the model search, a price set in the dialog, switched off, renamed), the budget, providers, the built-in texts, tools and plugins; tools switched off per chat; a profile with its tools, a project's default and the chat's picker", async () =>
        {
            // The script changes settings and changes them back; keepsSettings fails it when the document differs after.
            await RunUiScript(env, "settings.mjs", 180);
        }, 210);

        r.Add("ui.sessions-panel", "ui: side panels and the sessions list — resizing and folding, project favorites in the chevron's picker, no tab overflows at 230px, the archived section pages past 200 newer chats (archivedOnly), and the groups' defaults, stored state, reload and search", async () =>
        {
            // The script makes its own chats (one forked 261 times) and a project, and removes them again.
            await RunUiScript(env, "sessions-panel.mjs", 150);
        }, 180);

        r.Add("ui.panel-tabs", "ui: drag a side-panel tab to the other panel and back between two tabs; the arrangement is stored (localStorage and ui.state) and survives a reload from the host copy", async () =>
        {
            // The script puts back the layout it found.
            await RunUiScript(env, "panel-tabs.mjs", 60);
        }, 90);

        r.Add("ui.ideas-tab", "ui: the Ideas tab and the idea dialog — cards, status, filing with a pasted image, sections, send to chat, reorder, delete, Ctrl+I and /idea, the tab's project, a save after another writer, refining with an agent, tags, ideas.changed — and the start screen's ideas", async () =>
        {
            // The script makes its own project, chats and ideas and removes them; it switches the Ideas plugin off and on.
            await RunUiScript(env, "ideas-tab.mjs", 180);
        }, 210);

        r.Add("ui.diagnostics-tab", "ui: the Diagnostics tab — a plugin that fails to load listed first, reload, events, logs, the context with skills and tool changes, and the Calls view polling single-flight with slow answers and stopping when it is left", async () =>
        {
            // The script writes a broken plugin into this server's app copy and takes it out again; it switches the shell plugin off and on.
            await RunUiScript(env, "diagnostics-tab.mjs", 150);
        }, 180);

        r.Add("ui.files-tab", "ui: the Files tab — select vs open (files.open answered by the test), @ mention, the git line and its changes, the context menu, search, no files.scope per message, and a workspace switch that drops a late answer and reruns the search", async () =>
        {
            // The script makes its own git project (two commits ahead of an upstream) and a second one, and removes them.
            await RunUiScript(env, "files-tab.mjs", 150);
        }, 180);

        r.Add("ui.mcp-tab", "ui: the MCP tab against real servers — the fixture's tools (one rejected), a server that fails, a tool's details, read-only from the hint and the server list, the filter, and a switch that saves the configuration", async () =>
        {
            // The servers are this suite's MCP fixture in its "ui" mode and a command that does not exist; the script removes them.
            await RunUiScript(env, "mcp-tab.mjs", 120, "--fixture", typeof(McpTests).Assembly.Location);
        }, 150);

        r.Add("ui.composer", "ui: the composer and the chat tabs — the agent picker (new agent, any available), commands and skills, mentions, pickers that stay open while a reply streams, middle-click, tab dots (running, unread, idle, error) and the failed call's request id in Diagnostics, narrow widths, drag to reorder, drafts that survive the chat cache", async () =>
        {
            // The script makes its own agents, project (with skills) and chats and removes them; replies it watches wait at MockLlm gates.
            await RunUiScript(env, "composer.mjs", 240);
        }, 270);

        r.Add("ui.chat-history", "ui: a chat's history — a long chat pages back with a still anchor, caps at 200, pages forward and back to the latest, the context ring and its compactions, a page answer held while a message is committed does not drop it, and deleting a chat or one subagent", async () =>
        {
            // The script makes its own chats (one of about 230 messages: 110 cheap tool steps and two compactions) and removes them.
            await RunUiScript(env, "chat-history.mjs", 180);
        }, 210);

        r.Add("ui.chat-decisions", "ui: what a chat asks the user — ask_user cards (options and typed answers), a guardrail rule that asks before a push (allow, allow in this chat), fork from an answer and before a message, a goal set with /goal paused, resumed and achieved, and plan mode (the pill, revise, save as idea, approve, the offer to plan, cancel, approve in a new chat)", async () =>
        {
            // The script makes its own git project (pushes go to a bare repository of its own), chats and guardrail rule, and removes them.
            await RunUiScript(env, "chat-decisions.mjs", 240);
        }, 270);

        r.Add("ui.chat-run", "ui: a chat run as a person watches it — the status line and the prompt row, compaction refused while busy, a retry banner and stream.reset, live shell output, steer and queue chips, a subagent's report kept internal, folded steps with diffs and turn numbers; the showcase chat's prompt, notices as sent, a two-file edit and a command's changed files; /compact, Esc, images, a chat that never jumps, and the compaction banner", async () =>
        {
            // The script makes its own git project and chats and removes them; what it watches waits at MockLlm gates.
            await RunUiScript(env, "chat-run.mjs", 300);
        }, 330);

        r.Add("ui.remove-agent", "ui: settings → agents → remove agent — a confirmed removal removes the agent from the list and the settings document", async () =>
        {
            // Unique per run: a shared server (shards, -Repeat) keeps settings from earlier runs, so a fixed id
            // could collide with a leftover. The C# side seeds it; the script drives the UI and cleans up after.
            var agent = $"e2e-rm-{Guid.NewGuid().ToString("N")[..6]}";
            await env.Rpc("settings.set", new { path = $"agents.{agent}", value = new { model = CoreTests.Qwen, use = "removed by the ui.remove-agent test" } });
            try { await RunUiScript(env, "remove-agent.mjs", 90, "--agent", agent); }
            finally { await env.Rpc("settings.set", new { path = $"agents.{agent}", value = (object?)null }); }
        }, 120);
        // The script sets up its own agents (qwen, and gemma-4, which the mock lists as not loaded) and puts them back.
        r.Add("ui.work-tab", "ui: work tab — real runs on an agent's two instances (a foreground command, a background subagent), a run waiting for one and one waiting for any agent; rows that stay put as jobs end; an agent that is not loaded and its switch; a background command's output and kill; the agent's name opens its dialog", () =>
            RunUiScript(env, "work-tab.mjs", 150), 180);

        // The script makes its own project and chats; the ssh client is this runner's own exe (FakeSsh.cs), and the Claude CLI a
        // script it writes. web.*, ssh.* and providers.claudeCli are put back after.
        r.Add("ui.tool-cards", "ui: tool calls as a person reads them — thinking opened while it streams, the plan above the composer, web search, fetch and a screenshot, an image in the chat, no tools notice for deferred tools called by name; ssh run, read, edit and copy through a stand-in client; the Claude CLI's own tools done while its call runs; file links that ask first; an answer's HTML and images kept as text and links", () =>
            RunUiScript(env, "tool-cards.mjs", 180, "--ssh", Path.ChangeExtension(typeof(UiTests).Assembly.Location, OperatingSystem.IsWindows() ? ".exe" : null)), 210);

        // The script edits a plugin bundle in this server's own app copy (and puts it back), drops and restores the page's socket
        // (tapSocket), switches the runtime, context and compaction plugins off and on, and prices an agent; the settings are
        // put back after.
        r.Add("ui.connection", "ui: the page's link to the server — a plugin tab that remounts on a new bundle, its error state and Retry; a run that ends while the socket is down (no stale partial answer after the reconnect); reconnecting and resubscribing; the composer and Diagnostics without the runtime, context and compaction plugins, and back; the notifications for the desktop app (finished, the budget ask in an open chat and in one that is not, a click opens the chat)", () =>
            RunUiScript(env, "connection.mjs", 180), 210);

        // vite from the repo's node_modules, on a free port, in front of this server.
        r.Add("ui.dev-proxy", "ui: the vite dev server (npm run dev) proxies the socket, the API and the plugin bundles to the server, and ?token= signs the page in", () =>
            RunUiScript(env, "dev-proxy.mjs", 90), 120);

        // A paid agent (a price on a Claude model) and a free one; the budget and the agents are put back after.
        r.Add("ui.usage-budget", "ui: the Usage tab (period total, 30 days, agents and models, the chats that cost most, another period after a resetDay move), a chat's cost with its subagents, the budget on the Work tab as the limits change, the budget pill, a chat the budget stopped going over", () =>
            RunUiScript(env, "usage-budget.mjs", 150), 180);
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

    /// <summary>Two ideas in the session's project, so the smoke can check the calm overview: status groups, one line per
    /// idea, the summary and meta row only once a card is open (idea-43oruq). Seeded through the RPC, which is how the
    /// backlog is filled now: writing an ideas.json into the home does nothing (the cutover has already run).</summary>
    private static async Task SeedIdeas(Env env, string projectId, string sessionId)
    {
        await env.Rpc("ideas.add", new { projectId, sessionId, idea = new { title = "Smoke idea A", summary = "SMOKE-SUMMARY-A", status = "in-progress" } });
        await env.Rpc("ideas.add", new { projectId, sessionId, idea = new { title = "Smoke idea B", summary = "SMOKE-SUMMARY-B", status = "open" } });
    }
}
