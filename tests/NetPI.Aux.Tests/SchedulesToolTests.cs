using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Schedules;

namespace NetPI.Aux.Tests;

/// <summary>
/// The <c>schedules</c> tool: the model-facing surface (idea-1y1174's tool half). It calls the plugin's own RPCs, so
/// these tests check the model text (Content, no JSON), the Details shape, the action inference, and the tool-side
/// guards that the RPCs deliberately leave to the user's tab: a schedule's own chat cannot grow the schedule set,
/// and the set is capped.
/// </summary>
public static class SchedulesToolTests
{
    private static SchedulesTool Tool(SchedulesTests.Env env) => new(env.Ctx);

    private static ToolContext Tc(SchedulesTests.Env env, SessionInfo? session = null, ProjectInfo? project = null) => new()
    {
        SessionId = session?.Id ?? "ses_tool", AgentId = "agt_7", CallId = "call_1",
        Cwd = project?.Path ?? Path.GetTempPath(), Project = project,
        Services = env.Ctx.Services, Events = env.Ctx.Events,
    };

    private static async Task<ToolResult> Call(SchedulesTests.Env env, object args, SessionInfo? session = null, ProjectInfo? project = null) =>
        await Tool(env).ExecuteAsync(Tc(env, session, project), T.Args(args), CancellationToken.None);

    public static void Register(TestRunner r)
    {
        r.Add("schedules tool: list is plain text the model reads, with the full objects in Details", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            var empty = await Call(env, new { action = "list" });
            Check.Contains(empty.Content, "No schedules. Add one with action add.");
            Check.False(empty.IsError);
            Check.Contains(((string)JsonSerializer.Serialize(empty.Details))!, "\"kind\":\"schedules\"");

            var a = S((await env.Add(new { kind = "every", minutes = 60 }, extra: new { name = "Hourly" }))["id"])!;
            var b = S((await env.Add(new { kind = "daily", time = "07:30", tz = "UTC" }))["id"])!;
            var list = await Call(env, new { action = "list" });
            Check.Contains(list.Content, "2 schedules (2 enabled)");
            Check.Contains(list.Content, $"{a} · Hourly · every 60 min · enabled");
            Check.Contains(list.Content, $"{b} ·");
            Check.Contains(list.Content, "now ", "the machine's now, so a once time can be computed");
            Check.NotContains(list.Content, "{", "Content is plain text; the JSON is Details only");
            Check.NotContains(list.Content, "}");
            var details = (JsonNode)JsonSerializer.SerializeToNode(list.Details)!;
            Check.Equal("schedules", S(details["kind"]));
            var schedules = details["schedules"]!.AsArray().Select(s => s!.AsObject()).ToList();
            Check.Equal(string.Join(",", new[] { a, b }.Order(StringComparer.Ordinal)),
                string.Join(",", schedules.Select(s => S(s["id"])).Order(StringComparer.Ordinal)), "both schedules, as the full objects");
            var hourly = schedules.Single(s => S(s["id"]) == a);
            Check.Equal("Hourly", S(hourly["name"]));
            Check.Equal("every", S(hourly["cadence"]!["kind"]));
            Check.True(S(details["now"]) is { Length: > 0 }, "and the machine's now");
        });

        r.Add("schedules tool: add defaults the project to the calling session's, \"\" clears it, and the result echoes now", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            var withProject = await Call(env, new { action = "add", prompt = "Morning check", cadence = new { kind = "every", minutes = 60 } }, project: env.Project);
            Check.False(withProject.IsError, withProject.Content);
            Check.Equal(env.Project?.Id, S(((JsonNode)JsonSerializer.SerializeToNode(withProject.Details)!)["schedule"]!["projectId"]), "the session's project is the default");

            var none = await Call(env, new { action = "add", prompt = "No project", cadence = new { kind = "every", minutes = 60 }, projectId = "" }, project: env.Project);
            Check.False(none.IsError, none.Content);
            Check.True(((JsonNode)JsonSerializer.SerializeToNode(none.Details)!)["schedule"]!["projectId"] is null, "\"\" means no project");
            Check.Contains(withProject.Content, "Now is", "the echo lets the model compute a future once time");
        });

        r.Add("schedules tool: a bad add is the server's refusal, passed through", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            var past = await Call(env, new { action = "add", prompt = "x", cadence = new { kind = "once", at = env.Time.Now.AddMinutes(-1).ToString("O") } });
            Check.True(past.IsError);
            Check.Contains(past.Content, "in the future");

            var shortEvery = await Call(env, new { action = "add", prompt = "x", cadence = new { kind = "every", minutes = 1 } });
            Check.True(shortEvery.IsError);
            Check.Contains(shortEvery.Content, "at least 5");

            var badZone = await Call(env, new { action = "add", prompt = "x", cadence = new { kind = "daily", time = "07:00", tz = "Mars/Olympus" } });
            Check.True(badZone.IsError);
            Check.Contains(badZone.Content, "unknown time zone");

            var agents = new JsonArray
            {
                new JsonObject { ["key"] = "local", ["available"] = true, ["free"] = true },
            };
            env.Ctx.Rpc.Register("agents.list", (_, _) => Task.FromResult<object?>(agents.DeepClone()));
            var noAgent = await Call(env, new { action = "add", prompt = "x", cadence = new { kind = "every", minutes = 60 }, agent = "nobody" });
            Check.True(noAgent.IsError);
            Check.Contains(noAgent.Content, "nobody");
        });

        r.Add("schedules tool: update pauses, and a cadence change alone resumes a paused schedule (the server behaviour the form works around)", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            var id = S((await env.Add(new { kind = "every", minutes = 60 }))["id"])!;
            var paused = await Call(env, new { action = "update", id, enabled = false });
            Check.False(paused.IsError, paused.Content);
            Check.Contains(paused.Content, "paused");

            var resumed = await Call(env, new { action = "update", id, cadence = new { kind = "every", minutes = 30 } });
            Check.False(resumed.IsError, resumed.Content);
            Check.Contains(resumed.Content, "enabled", "the server resumes on a cadence change");
        });

        r.Add("schedules tool: run starts a chat, and an unavailable agent is a skipped error the model reads as a refusal", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            var id = S((await env.Add(new { kind = "every", minutes = 60 }))["id"])!;
            var started = await Call(env, new { action = "run", id });
            Check.False(started.IsError, started.Content);
            Check.Contains(started.Content, "Started");
            Check.Contains(started.Content, "chat ");

            var agents = new JsonArray { new JsonObject { ["key"] = "local", ["available"] = false, ["unavailable"] = "the model is not loaded", ["free"] = true } };
            env.Ctx.Rpc.Register("agents.list", (_, _) => Task.FromResult<object?>(agents.DeepClone()));
            env.Ctx.Rpc.Register("agents.use", (_, _) => Task.FromResult<object?>(null));
            var sid = S((await env.Add(new { kind = "every", minutes = 60 }, extra: new { agent = "local" }))["id"])!;
            var skipped = await Call(env, new { action = "run", id = sid });
            Check.True(skipped.IsError, "a skip is an error result, not a success");
            Check.Contains(skipped.Content, "Skipped:");
            Check.Contains(skipped.Content, "not loaded");
        });

        r.Add("schedules tool: delete is up to the user, in the tab", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            var id = S((await env.Add(new { kind = "every", minutes = 60 }))["id"])!;
            var refused = await Call(env, new { action = "delete", id });
            Check.True(refused.IsError);
            Check.Contains(refused.Content, "up to the user");
            Check.Contains(refused.Content, "enabled false");
            var still = await env.Get(id);
            Check.Equal(id, S(still["id"]), "nothing was deleted");
        });

        r.Add("schedules tool: a schedule's own chat cannot grow the set, and may only pause itself", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            var own = S((await env.Add(new { kind = "every", minutes = 60 }, extra: new { name = "Digest" }))["id"])!;
            var other = S((await env.Add(new { kind = "every", minutes = 60 }))["id"])!;
            var digest = env.Ctx.SessionsFake.CreateSession(new SessionInfo
            {
                Title = "Scheduled: Digest", ProjectId = env.Project.Id,
                Meta = new JsonObject { ["schedule"] = own },
            });

            Check.False((await Call(env, new { action = "list" }, session: digest)).IsError, "reading stays open");
            Check.False((await Call(env, new { action = "get", id = own }, session: digest)).IsError);
            var noAdd = await Call(env, new { action = "add", prompt = "x", cadence = new { kind = "every", minutes = 60 } }, session: digest);
            Check.True(noAdd.IsError);
            Check.Contains(noAdd.Content, "cannot create");
            var noRun = await Call(env, new { action = "run", id = own }, session: digest);
            Check.True(noRun.IsError);
            Check.Contains(noRun.Content, "cannot start");

            var selfPause = await Call(env, new { action = "update", id = own, enabled = false }, session: digest);
            Check.False(selfPause.IsError, selfPause.Content);
            var notOwn = await Call(env, new { action = "update", id = other, enabled = false }, session: digest);
            Check.True(notOwn.IsError);
            Check.Contains(notOwn.Content, "only pause its own");
            var selfChange = await Call(env, new { action = "update", id = own, prompt = "new" }, session: digest);
            Check.True(selfChange.IsError, "anything but pausing itself is refused");
        });

        r.Add("schedules tool: the set is capped, so a runaway add loop stops at the cap", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            for (var i = 0; i < SchedulesTool.MaxSchedules; i++)
                Check.False((await Call(env, new { action = "add", prompt = $"loop {i}", cadence = new { kind = "every", minutes = 60 } })).IsError, $"add {i + 1}");
            var over = await Call(env, new { action = "add", prompt = "one too many", cadence = new { kind = "every", minutes = 60 } });
            Check.True(over.IsError);
            Check.Contains(over.Content, "50");
        });

        r.Add("schedules tool: reads are read-only, everything else is not — including the inferred actions", () =>
        {
            var env = new SchedulesTests.Env();
            var tool = Tool(env);
            Check.True(tool.IsReadOnly(T.Args(new { action = "list" })));
            Check.True(tool.IsReadOnly(T.Args(new { action = "get", id = "x" })));
            Check.True(tool.IsReadOnly(T.Args(new { action = "history", id = "x" })));
            Check.True(tool.IsReadOnly(T.Args(new { })), "no action, no fields: list");
            Check.True(tool.IsReadOnly(T.Args(new { id = "x" })), "an id alone: get");
            Check.False(tool.IsReadOnly(T.Args(new { action = "add", prompt = "x", cadence = new { kind = "every", minutes = 60 } })));
            Check.False(tool.IsReadOnly(T.Args(new { action = "run", id = "x" })));
            Check.False(tool.IsReadOnly(T.Args(new { action = "update", id = "x", enabled = false })));
            Check.False(tool.IsReadOnly(T.Args(new { id = "x", prompt = "y" })), "an id plus a change infers update");
            Check.False(tool.IsReadOnly(T.Args(new { action = "pause", id = "x" })), "a pause is a write");
        });
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
