using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Schedules;

namespace NetPI.Aux.Tests;

/// <summary>
/// The <c>schedules</c> tool: the model-facing surface (idea-1y1174's tool half). It calls the plugin's own RPCs, so
/// these tests check the model text (Content, no JSON), the Details shape, the action inference, and the tool-side
/// guards that the RPCs deliberately leave to the user's tab: a schedule's own chat cannot grow the schedule set,
/// and the set is capped. An add is a proposal that waits for the user's decision on the card in the chat
/// (<c>schedule.proposed</c> / <c>schedules.decide</c>): allow enables it, discard deletes it, a steer leaves it a
/// proposal in the tab, a reload of the plugin leaves the wait alone, and a subagent's add does not wait at all. The
/// call's wait is held by the test's runtime (<see cref="SchedulesTests.RecordingRuntime.HoldAsync"/>), as the real one does.
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

    /// <summary>The tool's result; for a call that waits for the user, the result it gets when that wait ends.</summary>
    private static async Task<ToolResult> Call(SchedulesTests.Env env, object args, SessionInfo? session = null, ProjectInfo? project = null)
    {
        var context = Tc(env, session, project);
        var result = await Tool(env).ExecuteAsync(context, T.Args(args), CancellationToken.None);
        return result.Wait is { } wait ? await env.Runtime.HoldAsync(context, "schedules", wait) : result;
    }

    /// <summary>The proposal a just-started add is waiting on (the newest open one; a steer closed its own already).</summary>
    private static async Task<string> ProposedId(SchedulesTests.Env env)
    {
        for (var i = 0; i < 500; i++)
        {
            var open = await Proposals(env);
            if (open.Count > 0) return open[open.Count - 1]!["id"]!.GetValue<string>()!;
            await Task.Delay(10);
        }
        throw new Exception("no open schedule proposal");
    }

    /// <summary>An add that waits for the user's decision, with that decision made over the RPC (allow / discard).</summary>
    private static async Task<ToolResult> AddDecided(SchedulesTests.Env env, object args, string decision, SessionInfo? session = null, ProjectInfo? project = null)
    {
        var t = Task.Run(() => Call(env, args, session, project));
        var id = await ProposedId(env);
        await env.Ctx.RpcFake.CallAsync("schedules.decide", new { id, decision });
        return await t;
    }

    /// <summary>The proposals that wait now (<c>schedules.proposals</c>), by session.</summary>
    private static async Task<JsonArray> Proposals(SchedulesTests.Env env, string sessionId = "ses_tool") =>
        (JsonArray)((await env.Ctx.RpcFake.CallAsync("schedules.proposals", new { sessionId }))!);

    private static JsonNode Details(ToolResult r) => (JsonNode)JsonSerializer.SerializeToNode(r.Details)!;

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
            env.Runtime.Steer = true;   // the decision is not the point here: steer the waits away
            var withProject = await Call(env, new { action = "add", prompt = "Morning check", cadence = new { kind = "every", minutes = 60 } }, project: env.Project);
            Check.False(withProject.IsError, withProject.Content);
            Check.Equal(env.Project?.Id, S(Details(withProject)["schedule"]!["projectId"]), "the session's project is the default");

            var none = await Call(env, new { action = "add", prompt = "No project", cadence = new { kind = "every", minutes = 60 }, projectId = "" }, project: env.Project);
            Check.False(none.IsError, none.Content);
            Check.True(Details(none)["schedule"]!["projectId"] is null, "\"\" means no project");
            env.Runtime.Steer = false;
            var allowed = await AddDecided(env, new { action = "add", prompt = "Echo", cadence = new { kind = "every", minutes = 60 } }, "allow");
            Check.Contains(allowed.Content, "Now is", "the echo lets the model compute a future once time");
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

        // A schedule runs unattended with every tool: a model may pause one, but what it changes waits for the user (idea-96bh9b).
        r.Add("schedules tool: update pauses; a change of what runs is a proposal the user enables, and the tool never enables", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            var id = S((await env.Add(new { kind = "every", minutes = 60 }))["id"])!;
            var paused = await Call(env, new { action = "update", id, enabled = false });
            Check.False(paused.IsError, paused.Content);
            Check.Contains(paused.Content, "paused");
            var resume = await Call(env, new { action = "resume", id });
            Check.True(resume.IsError, "resuming is the user's");
            Check.Contains(resume.Content, "up to the user");

            await env.Ctx.RpcFake.CallAsync("schedules.update", new { id, enabled = true });   // the user resumes it
            var changed = await Call(env, new { action = "update", id, prompt = "Something else" });
            Check.False(changed.IsError, changed.Content);
            Check.Contains(changed.Content, "proposal");
            var s = await env.Get(id);
            Check.False(s["enabled"]!.GetValue<bool>(), "a change of what runs pauses it");
            Check.Equal("session:ses_tool", S(s["proposedBy"]), "as the calling chat's proposal");
            var cadence = await Call(env, new { action = "update", id, cadence = new { kind = "every", minutes = 30 } });
            Check.False(cadence.IsError, cadence.Content);
            Check.False((await env.Get(id))["enabled"]!.GetValue<bool>(), "a cadence change does not resume it either");

            await env.Ctx.RpcFake.CallAsync("schedules.update", new { id, enabled = true });   // the user's OK
            s = await env.Get(id);
            Check.True(s["enabled"]!.GetValue<bool>() && s["proposedBy"] is null, "enabling it is the approval");
            var renamed = await Call(env, new { action = "update", id, name = "Renamed" });
            Check.False(renamed.IsError, renamed.Content);
            Check.True((await env.Get(id))["enabled"]!.GetValue<bool>(), "a new name does not change what runs");
        });

        r.Add("schedules tool: an add is a proposal that waits: allow enables it, discard deletes it", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            var allowed = await AddDecided(env, new { action = "add", prompt = "Daily digest", cadence = new { kind = "every", minutes = 60 }, enabled = true }, "allow");
            Check.False(allowed.IsError, allowed.Content);
            Check.Contains(allowed.Content, "allowed");
            var s = Details(allowed)["schedule"]!;
            Check.True(s["enabled"]!.GetValue<bool>(), "allowed: it runs from now on");
            Check.Equal(null, s["proposedBy"], "the user's OK: the proposal mark is gone with the enable");
            Check.Equal("allowed", S(Details(allowed)["status"]));
            env.Time.Advance(TimeSpan.FromHours(3));
            Check.Equal(1, await env.Tick(), "an allowed schedule runs");

            var discarded = await AddDecided(env, new { action = "add", prompt = "Gone", cadence = new { kind = "every", minutes = 60 } }, "discard");
            Check.False(discarded.IsError, discarded.Content);
            Check.Contains(discarded.Content, "discarded");
            Check.Contains(discarded.Content, "deleted");
            await Check.ThrowsAsync<RpcException>(() => env.Get(S(Details(discarded)["schedule"]!["id"])!), "discarded: it is gone");

            // a proposal nobody decides yet stays paused and listed as proposed, the old way
            var t = Task.Run(() => Call(env, new { action = "add", prompt = "Undecided", cadence = new { kind = "every", minutes = 60 } }));
            var pid = await ProposedId(env);
            var list = await Proposals(env);
            Check.Equal(1, list.Count, "the proposal waits for the decision");
            var id = S(list[0]!["schedule"]!["id"])!;
            Check.False((await env.Get(id))["enabled"]!.GetValue<bool>(), "a proposal does not run on its own");
            Check.Equal("session:ses_tool", S((await env.Get(id))["proposedBy"]));
            Check.Contains((await Call(env, new { action = "list" })).Content, "proposed");
            env.Time.Advance(TimeSpan.FromHours(3));
            Check.Equal(0, await env.Tick(), "an undecided proposal does not run");
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.CallAsync("schedules.update",
                new { id, enabled = true, proposedBy = "session:ses_tool" }), "a proposer cannot enable one through the RPC either");
            await env.Ctx.RpcFake.CallAsync("schedules.decide", new { id = pid, decision = "discard" });
            Check.False((await t).IsError, "the discard unblocks the waiting call");
            await Check.ThrowsAsync<RpcException>(() => env.Get(id), "and the schedule is gone");
        });

        r.Add("schedules tool: a steer ends the wait; the proposal stays in the tab", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            env.Runtime.Steer = true;
            var steered = await Call(env, new { action = "add", prompt = "Stays", cadence = new { kind = "every", minutes = 60 } });
            env.Runtime.Steer = false;
            Check.False(steered.IsError, steered.Content);
            Check.Contains(steered.Content, "wrote a new message");
            Check.Contains(steered.Content, "stays a proposal");
            var id = S(Details(steered)["schedule"]!["id"])!;
            Check.False((await env.Get(id))["enabled"]!.GetValue<bool>(), "the proposal stays paused");
            Check.Equal("session:ses_tool", S((await env.Get(id))["proposedBy"]));
            var none = await Proposals(env);
            Check.Equal(0, none.Count, "the card is closed: nothing waits any more");
        });

        r.Add("schedules tool: a subagent's add has no card: it stays a proposal in the tab", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            env.Runtime.Agents.Add(new AgentInfo { Id = "agt_7", IsSubagent = true });
            var added = await Call(env, new { action = "add", prompt = "Sub", cadence = new { kind = "every", minutes = 60 } });
            Check.False(added.IsError, added.Content);
            Check.Contains(added.Content, "until the user enables it", "the old text: tell the parent, no card");
            Check.Contains(added.Content, "Now is");
            Check.False(Details(added)["schedule"]!["enabled"]!.GetValue<bool>(), "paused, as a proposal");
            var none = await Proposals(env);
            Check.Equal(0, none.Count, "no card: nothing waits");
        });

        r.Add("schedules tool: a reload of the plugin leaves the wait alone; the new version decides it", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            var t = Task.Run(() => Call(env, new { action = "add", prompt = "Late", cadence = new { kind = "every", minutes = 60 } }));
            var pid = await ProposedId(env);
            await env.Plugin.StopAsync(CancellationToken.None);
            await env.RestartAsync();   // a new version of the plugin (a new context on the same home)
            Check.False(t.IsCompleted, "nothing was withdrawn: the call still waits");
            Check.Equal(pid, S((await Proposals(env))[0]!["id"]), "the new version lists the wait");
            await env.Ctx.RpcFake.CallAsync("schedules.decide", new { id = pid, decision = "allow" });
            var r = await t;
            Check.False(r.IsError, r.Content);
            Check.Contains(r.Content, "allowed");
            Check.True((await env.Get(S(Details(r)["schedule"]!["id"])!))["enabled"]!.GetValue<bool>(), "allowed after the reload: it runs");
            Check.Equal(0, (await Proposals(env)).Count, "the wait is gone");
        });

        r.Add("schedules RPC: decide refuses the unknown and the already decided, and lists nothing then", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.CallAsync("schedules.decide", new { id = "schq_nope", decision = "allow" }), "no such proposal");

            var t = Task.Run(() => Call(env, new { action = "add", prompt = "Once", cadence = new { kind = "every", minutes = 60 } }));
            var pid = await ProposedId(env);
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.CallAsync("schedules.decide", new { id = pid, decision = "nope" }), "the decision is allow or discard");
            await env.Ctx.RpcFake.CallAsync("schedules.decide", new { id = pid, decision = "allow" });
            Check.False((await t).IsError, "the allow unblocks the waiting call");
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.CallAsync("schedules.decide", new { id = pid, decision = "allow" }), "decided: it is gone");
            Check.Equal(0, (await Proposals(env)).Count);
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

            // a subagent of the run inherits its tools, and its own session carries no schedule mark (idea-96bh9b)
            var child = env.Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "worker", Kind = "subagent", ParentSessionId = digest.Id });
            var childAdd = await Call(env, new { action = "add", prompt = "x", cadence = new { kind = "every", minutes = 60 } }, session: child);
            Check.True(childAdd.IsError, "the run's subagent cannot grow the set either");
            Check.Contains(childAdd.Content, "cannot create");
            Check.True((await Call(env, new { action = "run", id = other }, session: child)).IsError, "nor start a schedule");
        });

        r.Add("schedules tool: the set is capped, so a runaway add loop stops at the cap", async () =>
        {
            var env = await new SchedulesTests.Env().StartAsync();
            env.Runtime.Steer = true;   // the cap is the point: steer the decisions away
            for (var i = 0; i < SchedulesTool.MaxSchedules; i++)
                Check.False((await Call(env, new { action = "add", prompt = $"loop {i}", cadence = new { kind = "every", minutes = 60 } })).IsError, $"add {i + 1}");
            var over = await Call(env, new { action = "add", prompt = "one too many", cadence = new { kind = "every", minutes = 60 } });
            env.Runtime.Steer = false;
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
