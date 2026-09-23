using System.Text.Json;

namespace NetPI.E2E;

/// <summary>Graceful shutdown during a run, restart on the same home, persistence. Runs last.</summary>
public static class ShutdownTests
{
    public static void Register(TestRunner r, Env env)
    {
        r.Add("shutdown: SIGTERM during a run exits cleanly; restart keeps sessions, messages and projects", async () =>
        {
            var sessionsBefore = (await env.Rpc("sessions.list", new { limit = 500 })).Arr().ToList();
            var projectsBefore = (await env.Rpc("projects.list")).Arr().Count();
            var s = await env.NewSession(title: "interrupted");
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            await env.Rpc("agent.send", new { sessionId = sid, text = "slow [s:slow ms=8000]" });
            await env.Client.WaitFor(mark, e => e.Type == "stream.delta" && e.Sid == sid, "streaming", 20_000);

            // Graceful on Unix (SIGTERM). On Windows the process is killed, so only persistence is checked.
            var graceful = !OperatingSystem.IsWindows();
            var code = await env.StopServerAsync(TimeSpan.FromSeconds(20));
            var log = env.ReadServerLog();
            if (graceful)
            {
                Check.Equal(0, code, "exit code after SIGTERM");
                Check.Contains(log, "Stopping...");
            }
            Check.NotContains(log, "Unhandled exception");

            await env.StartServerAsync();
            var sessionsAfter = (await env.Rpc("sessions.list", new { limit = 500 })).Arr().ToList();
            Check.Equal(sessionsBefore.Count + 1, sessionsAfter.Count, "sessions persisted");
            Check.Equal(projectsBefore, (await env.Rpc("projects.list")).Arr().Count(), "projects persisted");
            foreach (var before in sessionsBefore)
            {
                var after = sessionsAfter.First(x => x.S("id") == before.S("id"));
                Check.Equal(before.L("messageCount"), after.L("messageCount"), $"message count of {before.S("id")}");
            }
            var msgs = (await env.Rpc("sessions.messages", new { id = sid })).Arr("messages").ToList();
            if (graceful) Check.Equal("aborted", msgs.Last(m => m.S("role") == "assistant").S("stopReason"), "the interrupted answer was saved as aborted");
            var agent = await env.Rpc("agent.get", new { sessionId = sid });
            Check.True(agent.ValueKind == JsonValueKind.Null || agent.S("status") is "idle" or "failed", "not running after restart: " + agent.GetRawText());

            var run = await env.Run(sid, "welcome back [s:echo]");
            Check.Contains(run.FinalText, "ECHO-DONE");
            var plugins = await env.Rpc("plugins.list");
            Check.True(plugins.Arr().All(p => p.S("state") == "running"), "all plugins running after restart");
        }, 120);
    }
}
