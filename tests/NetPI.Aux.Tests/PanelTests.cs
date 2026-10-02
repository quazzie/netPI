using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Diagnostics;
using NetPI.Work;

namespace NetPI.Aux.Tests;

public static class PanelTests
{
    public static void Register(TestRunner r)
    {
        r.Add("work: snapshot with no providing plugins → nulls", async () =>
        {
            var ctx = new FakePluginContext();
            await new WorkPlugin().StartAsync(ctx, CancellationToken.None);
            var tab = ctx.UiFake.TabList.Single();
            Check.True(tab is { Id: "work", Title: "Work", Panel: UiPanel.Right, Icon: "work", Order: 10 });
            var snap = (JsonObject)(await ctx.RpcFake.Call("work.snapshot"))!;
            foreach (var key in new[] { "agents", "runs", "processes", "usage" })
                Check.True(snap.ContainsKey(key) && snap[key] is null, key);
            Check.True(DateTimeOffset.TryParse(snap["time"].Str(), out _));
            Check.False(snap.ContainsKey("errors"));
            foreach (var gone in new[] { "physicalOwners", "resources", "ideasWork" })
                Check.False(snap.ContainsKey(gone), $"{gone} is no longer a part");
        });

        r.Add("work: snapshot aggregates available RPCs and tolerates failures", async () =>
        {
            var ctx = new FakePluginContext();
            await new WorkPlugin().StartAsync(ctx, CancellationToken.None);
            JsonElement runsParams = default;
            ctx.Rpc.Register("agents.list", (_, _) => Task.FromResult<object?>(new List<AgentSlots> { new() { Key = "test/m1", Capacity = 2, Busy = 1 } }));
            ctx.Rpc.Register("runs.list", (req, _) =>
            {
                runsParams = req.Params.Clone();
                return Task.FromResult<object?>(new List<AgentInfo> { new() { Id = "agt_1", Name = "main", Status = AgentStatus.Running } });
            });
            ctx.Rpc.Register("processes.list", (_, _) => throw new InvalidOperationException("registry gone"));
            var snap = (JsonObject)(await ctx.RpcFake.Call("work.snapshot"))!;
            Check.Equal("test/m1", snap["agents"]![0]!["key"].Str());
            Check.Equal(2, (int)snap["agents"]![0]!["capacity"]!);
            Check.Equal("running", snap["runs"]![0]!["status"].Str());
            Check.True(runsParams.GetProperty("includeFinished").GetBoolean(), "runs.list gets includeFinished:true");
            Check.True(snap["processes"] is null);
            Check.Contains(snap["errors"]!["processes"].Str(), "registry gone");
            Check.True(snap["usage"] is null);
        });

        r.Add("work: finished runs' task and result are truncated and the snapshot stays bounded", async () =>
        {
            var ctx = new FakePluginContext();
            await new WorkPlugin().StartAsync(ctx, CancellationToken.None);
            var runs = Enumerable.Range(1, 100).Select(i => new AgentInfo
            {
                Id = $"agt_{i:D3}",
                Status = AgentStatus.Completed,
                Task = $"task {i}\n" + new string('x', 5_000),
                Result = $"result {i}\n" + new string('y', 5_000),
            }).Append(new AgentInfo { Id = "agt_live", Status = AgentStatus.Running, Task = new string('z', 5_000) }).ToList();
            ctx.Rpc.Register("runs.list", (_, _) => Task.FromResult<object?>(runs));

            var snap = (JsonObject)(await ctx.RpcFake.Call("work.snapshot"))!;
            var list = (JsonArray)snap["runs"]!;
            Check.Equal(101, list.Count);
            foreach (var run in list.OfType<JsonObject>().Where(r => r["id"].Str() != "agt_live"))
            {
                Check.Equal(400, run["task"]!.Str()!.Length, $"finished {run["id"]!.Str()} task truncated");
                Check.Equal(400, run["result"]!.Str()!.Length, $"finished {run["id"]!.Str()} result truncated");
            }
            var first = (JsonObject)list[0]!;
            Check.True(first["task"]!.Str()!.StartsWith("task 1\nx") && first["task"]!.Str()!.EndsWith("…"), "truncated text keeps its start");
            var live = (JsonObject)list[100]!;
            Check.Equal(5_000, live["task"]!.Str()!.Length, "active runs stay whole");
            var size = snap.ToJsonString().Length;
            Check.True(size < 200_000, $"100 finished runs with 5 KB texts stay bounded: {size} B");
        });

        r.Add("diagnostics: snapshot shape, event details, tab and /reload command", async () =>
        {
            var ctx = new FakePluginContext(pluginId: "netpi.diagnostics");
            var pm = new FakePluginManager();
            pm.Plugins.Add(FakePluginManager.Info("netpi.retry", "Retry", 15));
            pm.Plugins.Add(FakePluginManager.Info("netpi.diagnostics", "Diagnostics", 90));
            ctx.ServicesFake.Register<IPluginManager>(pm);
            ctx.ToolsFake.Register(new FakeTool("read"));
            await new DiagnosticsPlugin().StartAsync(ctx, CancellationToken.None);
            Check.True(ctx.UiFake.TabList.Single() is { Id: "diagnostics", Title: "Diagnostics", Panel: UiPanel.Right, Icon: "bug", Order: 90 });
            Check.True(ctx.UiFake.CommandList.Single() is { Name: "reload", ArgsHint: "[pluginId]", Rpc: "diag.reload" });

            ctx.Events.Publish("agent.status", new JsonObject { ["agent"] = new JsonObject { ["id"] = "agt_1" } });
            ctx.Events.Publish("stream.delta", new JsonObject { ["text"] = "hi" }, "ses_1");
            ctx.Rpc.Register("logs.recent", (_, _) => Task.FromResult<object?>(new[] { "line 1", "line 2" }));

            var snap = (JsonObject)(await ctx.RpcFake.Call("diag.snapshot"))!;
            Check.Equal("netpi.retry", snap["plugins"]![0]!["id"].Str());
            Check.Equal("read", snap["tools"]![0]!["name"].Str());
            Check.Equal(true, (bool)snap["tools"]![0]!["active"]!);
            Check.True(((JsonArray)snap["rpc"]!).Any(m => m!["method"].Str() == "diag.snapshot"));
            var events = (JsonArray)snap["events"]!;
            Check.Equal(2, events.Count);
            Check.Equal("stream.delta", events[1]!["type"].Str());
            Check.Equal("ses_1", events[1]!["sessionId"].Str());
            Check.False(((JsonObject)events[1]!).ContainsKey("data"));
            Check.Equal("line 2", snap["logs"]![1].Str());
            Check.True((int)snap["runtime"]!["pid"]! > 0);

            var seq = (long)events[0]!["seq"]!;
            var ev = (JsonObject)(await ctx.RpcFake.Call("diag.event", new JsonObject { ["seq"] = seq }))!;
            Check.Equal("agt_1", ev["data"]!["agent"]!["id"].Str());
            var nf = await Check.ThrowsAsync<RpcException>(() => ctx.RpcFake.Call("diag.event", new JsonObject { ["seq"] = 999_999 }));
            Check.Equal("not_found", nf.Code);
        });

        r.Add("diagnostics: /reload one plugin (by id suffix) or all", async () =>
        {
            var ctx = new FakePluginContext(pluginId: "netpi.diagnostics");
            var pm = new FakePluginManager();
            pm.Plugins.Add(FakePluginManager.Info("netpi.diagnostics", "Diagnostics", 90));
            pm.Plugins.Add(FakePluginManager.Info("netpi.retry", "Retry", 15));
            pm.Plugins.Add(FakePluginManager.Info("netpi.tools.files", "File tools", 20));
            pm.Plugins.Add(FakePluginManager.Info("netpi.tools.shell", "Shell tools", 20));
            ctx.ServicesFake.Register<IPluginManager>(pm);
            await new DiagnosticsPlugin().StartAsync(ctx, CancellationToken.None);

            var one = (string?)await ctx.RpcFake.Call("diag.reload", new JsonObject { ["sessionId"] = "ses_1", ["args"] = "retry" });
            Check.Equal("Reloaded Retry (netpi.retry)", one);
            Check.Equal("netpi.retry", string.Join(",", pm.Reloaded));

            var amb = await Check.ThrowsAsync<RpcException>(() => ctx.RpcFake.Call("diag.reload", new JsonObject { ["args"] = "tools" }));
            Check.Equal("ambiguous", amb.Code);
            var nf = await Check.ThrowsAsync<RpcException>(() => ctx.RpcFake.Call("diag.reload", new JsonObject { ["args"] = "nope" }));
            Check.Equal("not_found", nf.Code);

            while (pm.Reloaded.TryDequeue(out _)) { }
            var all = (string?)await ctx.RpcFake.Call("diag.reload", new JsonObject { ["sessionId"] = "ses_1", ["args"] = "" });
            Check.Equal("Reloading 4 plugins…", all);
            for (var i = 0; i < 100 && pm.Reloaded.Count < 4; i++) await Task.Delay(20);
            Check.Equal("netpi.retry,netpi.tools.files,netpi.tools.shell,netpi.diagnostics", string.Join(",", pm.Reloaded));
            Check.Equal(1, pm.Rescans);

            var noPm = new FakePluginContext();
            await new DiagnosticsPlugin().StartAsync(noPm, CancellationToken.None);
            var un = await Check.ThrowsAsync<RpcException>(() => noPm.RpcFake.Call("diag.reload", new JsonObject()));
            Check.Equal("unavailable", un.Code);
            var snap = (JsonObject)(await noPm.RpcFake.Call("diag.snapshot"))!;
            Check.True(snap["plugins"] is null && snap["logs"] is null);
        });
    }

    private sealed class FakeTool(string name) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new() { Name = name, Description = name };
        public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct) => Task.FromResult(ToolResult.Ok("ok"));
    }
}
