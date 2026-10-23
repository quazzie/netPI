using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Autonomy;
using NetPI.Guardrails;

namespace NetPI.Agent.Tests;

public static class AutonomyTests
{
    public static void Register(TestRunner t)
    {
        t.Add("autonomy: a scheduled chat's ask is refused and kept; granted, it runs without asking; the plugin stopped, it asks as before", RefuseGrantDisable);
        t.Add("autonomy: always on the card grants the rule to the chat's origin: its other chats run without asking, other origins still ask", AlwaysOnCard);
        t.Add("autonomy: a subagent's ask is refused with a grantable refusal; asks wait in a scheduled chat when autonomy.unattendedAsks is wait", SubagentAndWait);
        t.Add("autonomy: a webhook chat's changes are journaled with a copy of each file, and undo puts them back (not twice, not over later work unless forced)", JournalAndUndo);
        t.Add("autonomy: reads, and the owner's own chats, are never journaled or braked", ReadsAndOwnChats);
        t.Add("autonomy: brakes: paused refuses changes but not reads; the hourly cap stops a loop with one notice", Brakes);
        t.Add("autonomy: a scheduled chat that read outside text may not act in the browser; a webhook's is tainted from the start", Taint);
        t.Add("autonomy: origin: a subagent works for its parent's origin, and inherits its taint", Origins);
    }

    private static async Task<TestHost> StartAsync(bool autonomy = true)
    {
        var h = await TestHost.StartAsync();
        h.Settings.Set("guardrails.askReflect", JsonValue.Create(false));
        await h.StartPluginAsync(new GuardrailsPlugin());
        if (autonomy) await h.StartPluginAsync(new AutonomyPlugin());
        return h;
    }

    /// <summary>The model makes the call the user's message names (<c>tool {json}</c>), then answers "done".</summary>
    private static void Scripted(TestHost h) => h.Catalog.Handler = (r, ct) =>
    {
        if (Reply.HasToolResult(r)) return Reply.Text("done");
        var text = Reply.LastUser(r);
        var space = text.IndexOf(' ');
        return Reply.Tool(text[..space], JsonNode.Parse(text[(space + 1)..]));
    };

    private static SessionInfo Chat(TestHost h, JsonObject? meta = null, string title = "test") =>
        h.Sessions.CreateSession(new SessionInfo { Title = title, Model = "fake/solo", Meta = meta });

    private static JsonObject Scheduled(string id = "sch1") => new() { ["schedule"] = id };
    private static JsonObject Webhook(string id = "wh1") => new() { ["webhook"] = id, ["webhookName"] = "Checkmk" };

    private static async Task Run(TestHost h, string sessionId, string tool, object args)
    {
        await h.SendAsync(sessionId, tool + " " + JsonSerializer.Serialize(args));
        await h.IdleAsync(sessionId);
    }

    private static FakeTool Recorder(string name, List<string> ran, bool readOnly = false) => new(name, (ctx, args, ct) =>
    {
        lock (ran) ran.Add(args.GetRawText());
        return Task.FromResult(ToolResult.Ok($"{name} ran"));
    }, readOnly);

    private static string LastResult(TestHost h, string sessionId) =>
        h.Messages(sessionId).Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).Last().Content;

    private static async Task<JsonArray> Rpc(TestHost h, string method, object? args = null) =>
        (await h.Rpc.CallAsync(method, args ?? new { }))!.AsArray();

    private static async Task RefuseGrantDisable()
    {
        await using var h = await StartAsync();
        h.Settings.Set("guardrails.commands", new JsonArray("ask: ^git push"));
        var ran = new List<string>();
        h.AddTool(Recorder("bash", ran));
        Scripted(h);
        var s = Chat(h, Scheduled(), "Scheduled: nightly");

        await Run(h, s.Id, "bash", new { command = "git push origin main" });
        Check.Equal(0, ran.Count, "refused: nothing ran");
        Check.Equal(0, h.Bus.OfType("guard.asked").Count, "nobody was asked");
        Check.Contains(LastResult(h, s.Id), "Nobody answers questions in this chat (it works for schedule nightly)");
        var refusal = (await Rpc(h, "autonomy.refusals")).Single()!;
        Check.Equal("ask", (string?)refusal["why"]);
        Check.Equal("ask: ^git push", (string?)refusal["rule"]);
        Check.Equal("schedule:sch1", (string?)refusal["origin"]);
        Check.True(refusal["grantable"]!.GetValue<bool>(), "a guardrail's question is grantable");

        // allowed from now on, from the tab: the same call runs without asking
        await h.Rpc.InvokeAsync("autonomy.grant", new { refusalId = (string)refusal["id"]! });
        Check.True((await Rpc(h, "autonomy.refusals")).Single()!["grantedMs"] is not null, "the refusal is marked granted");
        await Run(h, s.Id, "bash", new { command = "git push origin main" });
        Check.Equal(1, ran.Count, "granted: it ran");
        var cleared = FakeBus.Data(h.Bus.OfType("guard.cleared").Last());
        Check.Equal("grant", (string?)cleared["by"]);
        Check.Equal("schedule nightly", (string?)cleared["origin"]);
        Check.Equal(1, (await Rpc(h, "autonomy.grants")).Single()!["uses"]!.GetValue<int>());

        // another schedule is another origin: refused
        var other = Chat(h, Scheduled("sch2"));
        await Run(h, other.Id, "bash", new { command = "git push origin main" });
        Check.Equal(1, ran.Count, "another origin is not granted");

        // the plugin stopped: the guardrails ask as they always did (and the grant waits in its data)
        await h.StopPluginAsync("netpi.autonomy");
        await h.SendAsync(s.Id, "bash " + JsonSerializer.Serialize(new { command = "git push origin main" }));
        await Wait.Until(() => h.Bus.OfType("guard.asked").Count >= 1, "without the plugin the call waits for the OK");
        Check.True(FakeBus.Data(h.Bus.OfType("guard.asked")[0])["grantTo"] is null, "no grant offered without the plugin");
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)FakeBus.Data(h.Bus.OfType("guard.asked")[0])["approvalId"]!, allow = false });
        await h.IdleAsync(s.Id);
        Check.Equal(1, ran.Count);
        Check.Equal("unavailable", (await Check.ThrowsAsync<RpcException>(async () =>
        {
            await h.SendAsync(s.Id, "bash " + JsonSerializer.Serialize(new { command = "git push origin main" }));
            await Wait.Until(() => h.Bus.OfType("guard.asked").Count >= 2, "asks again");
            await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)FakeBus.Data(h.Bus.OfType("guard.asked")[1])["approvalId"]!, allow = true, scope = "always" });
        })).Code, "always needs the plugin");

        // started again: the grant is still there
        await h.StartPluginAsync(new AutonomyPlugin());
        Check.Equal(1, (await Rpc(h, "autonomy.grants")).Count, "the grant survived the plugin's stop");
    }

    private static async Task AlwaysOnCard()
    {
        await using var h = await StartAsync();
        h.Settings.Set("guardrails.commands", new JsonArray("ask: ^git push"));
        var ran = new List<string>();
        h.AddTool(Recorder("bash", ran));
        Scripted(h);

        var mine = Chat(h);
        await h.SendAsync(mine.Id, "bash " + JsonSerializer.Serialize(new { command = "git push" }));
        await Wait.Until(() => h.Bus.OfType("guard.asked").Count >= 1, "the owner's chat asks");
        var asked = FakeBus.Data(h.Bus.OfType("guard.asked")[0]);
        Check.Equal("your chats", (string?)asked["grantTo"]);
        Check.Equal("your chats", (string?)(await h.Rpc.CallAsync("guard.pending", new { sessionId = mine.Id }))!.AsArray().Single()!["grantTo"], "a reconnect sees it too");
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)asked["approvalId"]!, allow = true, scope = "always" });
        await h.IdleAsync(mine.Id);
        Check.Equal(1, ran.Count);
        Check.Equal("always", (string?)FakeBus.Data(h.Bus.OfType("guard.closed").Last())["scope"]);
        Check.True(h.Sessions.GetSession(mine.Id)!.Meta?["guardrailsAllowed"] is null, "always is a grant, not a chat's allow");

        // another of the owner's chats: no question
        var another = Chat(h);
        await Run(h, another.Id, "bash", new { command = "git push" });
        Check.Equal(2, ran.Count, "granted to your chats");
        Check.Equal(1, h.Bus.OfType("guard.asked").Count);

        // a scheduled chat is another origin: refused
        var scheduled = Chat(h, Scheduled());
        await Run(h, scheduled.Id, "bash", new { command = "git push" });
        Check.Equal(2, ran.Count, "not granted to a schedule");

        // revoked: the owner's chat asks again
        await h.Rpc.InvokeAsync("autonomy.revoke", new { id = (string)(await Rpc(h, "autonomy.grants")).Single()!["id"]! });
        await h.SendAsync(another.Id, "bash " + JsonSerializer.Serialize(new { command = "git push" }));
        await Wait.Until(() => h.Bus.OfType("guard.asked").Count >= 2, "asks again after the revoke");
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)FakeBus.Data(h.Bus.OfType("guard.asked")[1])["approvalId"]!, allow = false });
        await h.IdleAsync(another.Id);
    }

    private static async Task SubagentAndWait()
    {
        await using var h = await StartAsync();
        h.Settings.Set("guardrails.commands", new JsonArray("ask: ^git push"));
        var ran = new List<string>();
        h.AddTool(Recorder("bash", ran));
        var parent = Chat(h);
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.SessionId != parent.Id)
                return Reply.HasToolResult(r) ? Reply.Text("REPORT: blocked") : Reply.Tool("bash", new { command = "git push" });
            return Reply.SpawnWaitAnswer(r, new { task = "Push it.", name = "pusher" }, "parent done");
        };
        await h.SendAsync(parent.Id, "delegate");
        var p = await h.IdleAsync(parent.Id, 15_000);
        var child = h.Runtime.Get(p.Children.Single())!;
        await h.IdleAsync(child.SessionId);
        Check.Contains(LastResult(h, child.SessionId), "it works for your chats");
        Check.Equal(0, ran.Count);
        var refusal = (await Rpc(h, "autonomy.refusals")).Single()!;
        Check.Equal("user", (string?)refusal["origin"], "a subagent of the owner's chat works for the owner's chats");
        Check.True(refusal["grantable"]!.GetValue<bool>());

        // autonomy.unattendedAsks wait: a scheduled chat's ask waits for the owner, as before
        h.Settings.Set("autonomy.unattendedAsks", JsonValue.Create("wait"));
        Scripted(h);
        var s = Chat(h, Scheduled());
        await h.SendAsync(s.Id, "bash " + JsonSerializer.Serialize(new { command = "git push" }));
        await Wait.Until(() => h.Bus.OfType("guard.asked").Count >= 1, "it waits for the OK");
        Check.Equal("schedule sch1", (string?)FakeBus.Data(h.Bus.OfType("guard.asked")[0])["grantTo"]);
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)FakeBus.Data(h.Bus.OfType("guard.asked")[0])["approvalId"]!, allow = true });
        await h.IdleAsync(s.Id);
        Check.Equal(1, ran.Count);
    }

    /// <summary>A write tool that writes its content to its path, as the real one does (relative to the chat's cwd).</summary>
    private static FakeTool Writer() => new("write", (ctx, args, ct) =>
    {
        var a = new ToolArgs(args);
        var path = ctx.ResolvePath(a.Str("path")!);
        File.WriteAllText(path, a.Str("content") ?? "");
        return Task.FromResult(ToolResult.Ok("written"));
    });

    private static async Task JournalAndUndo()
    {
        await using var h = await StartAsync();
        h.AddTool(Writer());
        Scripted(h);
        var dir = TestShared.T.TempDir("autonomy-undo");
        var file = Path.Combine(dir, "app.conf");
        var fresh = Path.Combine(dir, "new.conf");
        File.WriteAllText(file, "port=1");
        var s = Chat(h, Webhook());

        await Run(h, s.Id, "write", new { path = file, content = "port=2" });
        await Run(h, s.Id, "write", new { path = fresh, content = "x" });
        Check.Equal("port=2", File.ReadAllText(file));
        var rows = await Rpc(h, "autonomy.journal");
        Check.Equal(2, rows.Count);
        var created = rows[0]!;
        var changed = rows[1]!;
        Check.Equal("write", (string?)changed["tool"]);
        Check.Equal(file, (string?)changed["subject"]);
        Check.Equal("webhook:wh1", (string?)changed["origin"]);
        Check.Equal("webhook Checkmk", (string?)changed["label"]);
        Check.Equal(s.Id, (string?)changed["sessionId"]);
        Check.True(changed["checkpoint"]?["existed"]?.GetValue<bool>() == true, "a copy of the file as it was");
        Check.Equal(1, (await Rpc(h, "autonomy.journal", new { sessionId = s.Id, limit = 1 })).Count, "paged by session");

        // undo: the file as it was; again: refused
        var restored = await h.Rpc.CallAsync("autonomy.undo", new { id = (string)changed["id"]! });
        Check.Equal(file, (string?)restored!["restored"]![0]);
        Check.Equal("port=1", File.ReadAllText(file));
        Check.Equal("conflict", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("autonomy.undo", new { id = (string)changed["id"]! }))).Code, "not twice");

        // undo of a file the change created deletes it
        await h.Rpc.InvokeAsync("autonomy.undo", new { id = (string)created["id"]! });
        Check.False(File.Exists(fresh), "a created file is removed");

        // later work is not thrown away unless forced
        await Run(h, s.Id, "write", new { path = file, content = "port=3" });
        File.WriteAllText(file, "port=4 (the owner's edit)");
        var third = (await Rpc(h, "autonomy.journal"))[0]!;
        Check.Equal("conflict", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("autonomy.undo", new { id = (string)third["id"]! }))).Code);
        Check.Equal("port=4 (the owner's edit)", File.ReadAllText(file));
        await h.Rpc.InvokeAsync("autonomy.undo", new { id = (string)third["id"]!, force = true });
        Check.Equal("port=1", File.ReadAllText(file));
    }

    private static async Task ReadsAndOwnChats()
    {
        await using var h = await StartAsync();
        var ran = new List<string>();
        h.AddTool(Recorder("bash", ran));
        h.AddTool(Recorder("grep", ran, readOnly: true));
        Scripted(h);
        h.Settings.Set("autonomy.paused", JsonValue.Create(true));

        var scheduled = Chat(h, Scheduled());
        await Run(h, scheduled.Id, "grep", new { pattern = "x" });
        Check.Equal(1, ran.Count, "a read runs while paused");
        Check.Equal(0, (await Rpc(h, "autonomy.journal")).Count, "and is not journaled");

        var mine = Chat(h);
        await Run(h, mine.Id, "bash", new { command = "make" });
        Check.Equal(2, ran.Count, "the owner's chat is never paused");
        Check.Equal(0, (await Rpc(h, "autonomy.journal")).Count, "nor journaled");
    }

    private static async Task Brakes()
    {
        await using var h = await StartAsync();
        var ran = new List<string>();
        h.AddTool(Recorder("bash", ran));
        Scripted(h);
        var s = Chat(h, Scheduled());

        await h.Rpc.InvokeAsync("autonomy.pause", new { paused = true });
        Check.True(h.Settings.Get("autonomy.paused", false), "pause is the setting");
        await Run(h, s.Id, "bash", new { command = "systemctl restart x" });
        Check.Equal(0, ran.Count, "paused: refused");
        Check.Contains(LastResult(h, s.Id), "The owner paused changes");
        Check.True((await h.Rpc.CallAsync("autonomy.state"))!["paused"]!.GetValue<bool>());
        await h.Rpc.InvokeAsync("autonomy.pause", new { paused = false });

        h.Settings.Set("autonomy.maxChangesPerHour", JsonValue.Create(2));
        for (var i = 0; i < 4; i++) await Run(h, s.Id, "bash", new { command = "systemctl restart x" });
        Check.Equal(2, ran.Count, "the cap stops the third");
        Check.Contains(LastResult(h, s.Id), "made 2 changes in the last hour");
        var braked = h.Bus.OfType("autonomy.braked");
        Check.Equal(1, braked.Count, "one notice, not one per refused call");
        Check.Equal("schedule:sch1", (string?)FakeBus.Data(braked[0])["origin"]);

        // another origin has its own count
        var other = Chat(h, Webhook());
        await Run(h, other.Id, "bash", new { command = "uptime" });
        Check.Equal(3, ran.Count);
    }

    private static async Task Taint()
    {
        await using var h = await StartAsync();
        var ran = new List<string>();
        h.AddTool(Recorder("browser", ran));
        h.AddTool(Recorder("web_fetch", ran, readOnly: true));
        Scripted(h);

        h.AddTool(Recorder("windows", ran));
        var s = Chat(h, Scheduled());
        await Run(h, s.Id, "windows", new { action = "click", n = 3 });
        Check.Equal(1, ran.Count, "before any outside text it may act");
        Check.True(h.Sessions.GetSession(s.Id)!.Meta?["autonomy"] is null, "the desktop is not outside text");
        await Run(h, s.Id, "web_fetch", new { url = "https://example.com" });
        Check.True(h.Sessions.GetSession(s.Id)!.Meta?["autonomy"]?["tainted"]?.GetValue<bool>() == true, "tainted after an external tool");
        Check.Equal("web_fetch", (string?)h.Sessions.GetSession(s.Id)!.Meta!["autonomy"]!["taintedBy"]);
        await Run(h, s.Id, "windows", new { action = "click", n = 3 });
        Check.Equal(2, ran.Count, "after it: refused");
        Check.Contains(LastResult(h, s.Id), "read text from outside");
        await Run(h, s.Id, "browser", new { action = "open", url = "https://example.com" });
        Check.Equal(3, ran.Count, "an action not listed runs");
        await Run(h, s.Id, "browser", new { action = "click", n = 1 });
        Check.Equal(3, ran.Count, "a listed one does not");
        var refusal = (await Rpc(h, "autonomy.refusals"))[0]!;
        Check.Equal("tainted", (string?)refusal["why"]);
        Check.False(refusal["grantable"]!.GetValue<bool>(), "never grantable");
        Check.Equal("bad_request", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("autonomy.grant", new { refusalId = (string)refusal["id"]! }))).Code);

        var hook = Chat(h, Webhook());
        await Run(h, hook.Id, "browser", new { action = "type", text = "x" });
        Check.Equal(3, ran.Count, "a webhook's chat read its payload: tainted from the start");

        // the owner's chat is never limited, whatever it read
        var mine = Chat(h);
        await Run(h, mine.Id, "web_fetch", new { url = "https://example.com" });
        await Run(h, mine.Id, "browser", new { action = "click", n = 1 });
        Check.Equal(5, ran.Count);
        Check.True(h.Sessions.GetSession(mine.Id)!.Meta?["autonomy"] is null, "nothing written on the owner's chat");
    }

    private static async Task Origins()
    {
        await using var h = await TestHost.StartAsync();
        var store = h.Sessions;
        var chats = new Chats(new TestPluginContext(h, "netpi.autonomy"));
        var root = store.CreateSession(new SessionInfo { Title = "Scheduled: backups", Meta = Scheduled("s9") });
        var child = store.CreateSession(new SessionInfo { Kind = "subagent", ParentSessionId = root.Id });
        var grandchild = store.CreateSession(new SessionInfo { Kind = "subagent", ParentSessionId = child.Id });
        Check.Equal(new Origin("schedule:s9", "schedule backups"), chats.OriginOf(grandchild.Id));
        Check.True(chats.Unattended(chats.Get(root.Id)), "a scheduled chat is unattended");
        Check.False(chats.Tainted(chats.Get(grandchild.Id)), "not tainted yet");
        chats.Taint(child.Id, "web_fetch");
        Check.True(chats.Tainted(chats.Get(grandchild.Id)), "a subagent of a tainted chat is tainted");
        Check.False(chats.Tainted(chats.Get(root.Id)), "the parent is not");

        var mine = store.CreateSession(new SessionInfo { Title = "mine" });
        var helper = store.CreateSession(new SessionInfo { Kind = "subagent", ParentSessionId = mine.Id });
        Check.Equal(Origin.User, chats.OriginOf(helper.Id));
        Check.False(chats.Unattended(chats.Get(mine.Id)), "the owner's chat is watched");
        Check.True(chats.Unattended(chats.Get(helper.Id)), "its subagent is not");
    }
}
