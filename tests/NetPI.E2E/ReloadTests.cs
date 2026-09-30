using System.Text.Json;

namespace NetPI.E2E;

/// <summary>Hot reload while the server runs: file-triggered, RPC-triggered, and reloading the agent runtime mid-run.</summary>
public static class ReloadTests
{
    private static async Task<JsonElement> Plugin(Env env, string id) =>
        (await env.Rpc("plugins.list")).Arr().First(p => p.S("id") == id);

    public static void Register(TestRunner r, Env env)
    {
        r.Add("reload.file-change", "hot reload: overwriting NetPI.Nudge.dll reloads the plugin, the old context is collected, nudges still work", async () =>
        {
            var before = await Plugin(env, "netpi.nudge");
            var mark = env.Client.Mark();
            var dll = Path.Combine(env.AppDir, "plugins", "NetPI.Nudge", "NetPI.Nudge.dll");
            var tmp = dll + ".copy";
            File.Copy(dll, tmp, overwrite: true);
            File.Copy(tmp, dll, overwrite: true); // same bytes, new mtime: what a rebuild looks like to the watcher
            File.Delete(tmp);
            File.SetLastWriteTimeUtc(dll, DateTime.UtcNow);

            await env.Client.WaitFor(mark, e => e.Type == "plugins.changed", "plugins.changed", 15_000);
            var after = await Wait.UntilAsync(async () =>
            {
                var p = await Plugin(env, "netpi.nudge");
                return p.L("loadCount") > before.L("loadCount") && p.S("state") == "running" ? (object)p : null;
            }, "netpi.nudge reloaded", 15_000);
            var reloaded = (JsonElement)after;
            Check.Equal(before.L("loadCount") + 1, reloaded.L("loadCount"));
            Check.Equal("running", reloaded.S("state"));
            var unloaded = await env.Client.WaitFor(mark, e => e.Type == "plugins.unloaded" && e.D.S("id") == "netpi.nudge", "plugins.unloaded", 30_000);
            Check.True(unloaded.D.B("collected"), "the previous load context was garbage collected");
            // other plugins untouched
            Check.Equal(1L, (await Plugin(env, "netpi.runtime")).L("loadCount"));

            var s = await env.NewSession();
            var run = await env.Run(s.S("id")!, "Analyze [s:cutoff]");
            Check.Equal("nudge", run.Role("notice").Single().P("meta").S("kind"));
            Check.Contains(run.FinalText, "NUDGE-RESUMED");
        });

        r.Add("reload.all-plugins", "hot reload: plugins.reload of every provider/tool/hook plugin, then a full tool run still works", async () =>
        {
            var mark = env.Client.Mark();
            foreach (var id in new[] { "netpi.providers.aiproxy", "netpi.tools.files", "netpi.tools.shell", "netpi.retry", "netpi.toolrepair", "netpi.context", "netpi.agents" })
            {
                Check.True((await env.Rpc("plugins.reload", new { id }, 60_000)).GetBoolean(), "reload " + id);
                Check.Equal("running", (await Plugin(env, id)).S("state"), id + " running");
            }
            var models = await env.Rpc("models.list");
            Check.True(models.Arr("models").Any(m => m.S("ref") == CoreTests.Qwen), "models still listed after reloading the provider");
            var p = await env.NewProject("reload", CoreTests.Seed);
            var s = await env.NewSession(projectId: p.S("id"));
            var run = await env.Run(s.S("id")!, "Edit [s:tools file=notes.txt old=beta new=BETA-RELOADED]");
            Check.Contains(run.FinalText, "TOOLS-DONE");
            Check.Contains(File.ReadAllText(Path.Combine(p.S("path")!, "notes.txt")), "BETA-RELOADED");
            var unloaded = await Wait.UntilAsync(async () =>
            {
                await Task.CompletedTask;
                var evs = env.Client.Since(mark, e => e.Type == "plugins.unloaded");
                return evs.Count >= 7 ? evs : null;
            }, "7 plugins.unloaded events", 40_000);
            var leaks = unloaded.Where(e => !e.D.B("collected")).Select(e => e.D.S("id")).ToList();
            Check.True(leaks.Count == 0, "all old load contexts collected, leaking: " + string.Join(", ", leaks));
        }, 150);

        r.Add("reload.runtime-midrun", "hot reload: reloading the agent runtime mid-run ends the run cleanly; the session continues afterwards", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            await env.Rpc("agent.send", new { sessionId = sid, text = "slow [s:slow ms=6000]" });
            await env.Client.WaitFor(mark, e => e.Type == "stream.delta" && e.Sid == sid, "streaming", 20_000);
            Check.True((await env.Rpc("plugins.reload", new { id = "netpi.runtime" }, 60_000)).GetBoolean());
            Check.Equal("running", (await Plugin(env, "netpi.runtime")).S("state"));
            var page = await env.Rpc("sessions.messages", new { id = sid, limit = 50 });
            var msgs = page.Arr("messages").ToList();
            Check.Equal("aborted", msgs.Last(m => m.S("role") == "assistant").S("stopReason"), "partial answer kept as aborted");
            var after = await env.Rpc("agent.get", new { sessionId = sid });
            Check.True(after.ValueKind == JsonValueKind.Null || after.S("status") is "idle" or "failed", "agent not running after reload: " + after.GetRawText());
            var run = await env.Run(sid, "again [s:echo]");
            Check.Contains(run.FinalText, "ECHO-DONE");
            var unloaded = await env.Client.WaitFor(mark, e => e.Type == "plugins.unloaded" && e.D.S("id") == "netpi.runtime", "agent plugin unloaded", 30_000);
            Check.True(unloaded.D.B("collected"), "the old agent runtime was collected");
        }, 120);
    }
}
