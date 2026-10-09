using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Mcp;
namespace NetPI.Agent.Tests;

public static class DeferredToolsTests
{
    private sealed class Hook : IAgentHook
    {
        public int Order => 1;
        public List<string> Before = [], After = [];
        public bool Block;
        public string? Arguments;
        public ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call)
        {
            Before.Add(call.Name);
            return ValueTask.FromResult<ToolCallDecision?>(Block ? new ToolCallDecision { Block=true, Reason="effective target blocked" } : Arguments is not null && call.Name.StartsWith("mcp_fixture_") ? new ToolCallDecision { Arguments=Arguments } : null);
        }
        public ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result) { After.Add(call.Name);return ValueTask.CompletedTask; }
    }
    /// <summary>A plain deferred tool of some plugin: what tool_search finds and loads.</summary>
    private sealed class Deferred(string name, string description, bool deferred = true) : IAgentTool
    {
        public readonly List<string> Calls = [];
        public ToolDefinition Definition { get; } = new()
        {
            Name = name, Description = description, Deferred = deferred,
            Parameters = JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string","description":"The city"}}}""")!.AsObject(),
        };
        public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
        {
            Calls.Add(args.GetRawText());
            return Task.FromResult(ToolResult.Ok("ran " + Definition.Name));
        }
    }

    public static void Register(TestRunner t)
    {
        t.Add("deferred: tool_search loads a deferred tool for the next call, with a tools notice naming the cause", async () => {
            await using var h = await TestHost.StartAsync();
            var ctx = new TestPluginContext(h, "netpi.test");
            var weather = new Deferred("weather_lookup", "Current weather and forecast for a city.");
            var stocks = new Deferred("stock_quote", "Share prices of a listed company.");
            ctx.Tools.Register(weather); ctx.Tools.Register(stocks);
            var session = h.NewSession(); var stage = 0;
            h.Catalog.Handler = (r, ct) => stage++ switch
            {
                0 => Reply.Tools(Reply.Call("weather_lookup", new { city = "Oslo" })),
                1 => Reply.Tools(Reply.Call("tool_search", new { query = "weather forecast" })),
                2 => Reply.Tools(Reply.Call("weather_lookup", new { city = "Oslo" })),
                _ => Reply.Text("done"),
            };
            await h.SendAsync(session.Id, "weather in Oslo?"); await h.IdleAsync(session.Id);
            var requests = h.Catalog.Requests.Where(r => r.SessionId == session.Id).ToList();
            Check.Equal(4, requests.Count);
            Check.True(requests[0].Tools.Any(d => d.Name == "tool_search"), "a chat with deferred tools is sent tool_search");
            Check.False(requests[0].Tools.Any(d => d.Name == "weather_lookup"));
            Check.False(requests[1].Tools.Any(d => d.Name == "weather_lookup"));
            Check.True(requests[2].Tools.Any(d => d.Name == "weather_lookup"), "loaded from the next call");
            Check.False(requests[2].Tools.Any(d => d.Name == "stock_quote"), "only the match is loaded");
            var results = h.Messages(session.Id).SelectMany(m => m.ToolResults).ToList();
            Check.True(results[0].IsError); Check.Contains(results[0].Content, "tool_search");
            Check.Contains(results[1].Content, "weather_lookup"); Check.NotContains(results[1].Content, "stock_quote");
            Check.False(results[2].IsError); Check.Equal(1, weather.Calls.Count);
            Check.Equal("[\"weather_lookup\"]", h.Sessions.GetSession(session.Id)!.Meta?[LoadedTools.MetaKey]?.ToJsonString());
            var notice = h.Messages(session.Id).Single(m => m.MetaString("kind") == "tools");
            Check.Equal("tool-search", notice.MetaString("cause"));
            Check.Contains(notice.Text, "loaded by tool_search");

            // loaded is not a way around the chat's own switches
            await h.Rpc.CallAsync("agent.setTools", new { sessionId = session.Id, off = new[] { "weather_lookup" } });
            h.Catalog.Handler = (r, ct) => Reply.Text("done");
            await h.SendAsync(session.Id, "again"); await h.IdleAsync(session.Id);
            Check.False(h.Catalog.Requests.Last().Tools.Any(d => d.Name == "weather_lookup"));
            ctx.Unload();
        });
        t.Add("deferred: tool_search is not sent without deferred tools, and ranks by name and words", async () => {
            await using var h = await TestHost.StartAsync();
            var session = h.NewSession();
            h.Catalog.Handler = (r, ct) => Reply.Text("done");
            await h.SendAsync(session.Id, "hi"); await h.IdleAsync(session.Id);
            Check.False(h.Catalog.Requests.Last().Tools.Any(d => d.Name == "tool_search"));

            ToolDefinition Def(string name, string description) => new Deferred(name, description).Definition;
            var tools = new[]
            {
                Def("issue_create", "Open a new issue in a GitHub repository."),
                Def("pull_requests_list", "List the pull requests of a repository."),
                Def("weather_lookup", "Current weather for a city."),
            };
            Check.Equal("issue_create", ToolRanking.Rank(tools, "create issues").First().Name, "plural and stop words");
            Check.Equal("pull_requests_list", ToolRanking.Rank(tools, "pullRequest").First().Name, "camelCase splits");
            Check.Equal("weather_lookup", ToolRanking.Rank(tools, "WEATHER_LOOKUP").First().Name, "exact name");
            Check.Equal(0, ToolRanking.Rank(tools, "the of and").Count);
            Check.Equal(0, ToolRanking.Rank(tools, "spreadsheet").Count);
        });
        t.Add("deferred: runtime search schema call with 250 tools keeps two schemas and effective hooks", async () => {
            await using var h=await TestHost.StartAsync();
            var ctx=new TestPluginContext(h,"netpi.mcp");
            var config=new ServerConfig {Id="fixture",Command="dotnet",Args=[typeof(DeferredToolsTests).Assembly.Location,"--mcp-fixture","large"],Cwd=h.Workspace};
            h.Settings.Set("mcp.servers",new JsonObject {["fixture"]=config.Json()});
            await using var manager=new ServerManager(ctx);await manager.StartAsync([],CancellationToken.None);
            await Wait.Until(() => manager.Catalog().Count > 0, "mcp fixture connected");
            ctx.Tools.Register(new McpSearchTool(ctx,manager));ctx.Tools.Register(new McpCallTool(ctx,manager));
            var target=manager.Catalog().Single(x=>x.RemoteName=="weather");
            var hook=new Hook {Arguments="""{"city":"Berlin"}"""};h.Services.Register<IAgentHook>(hook);
            var session=h.NewSession();int stage=0;
            h.Catalog.Handler=(r,ct)=>stage++ switch {
                0=>Reply.Tools(Reply.Call("mcp_search",new {query="city weather"})),
                1=>Reply.Tools(Reply.Call("mcp_search",new {query=target.Definition.Name,detail="schema"})),
                2=>Reply.Tools(Reply.Call("mcp_call",new {id=target.Definition.Name,revision=target.Definition.Revision,arguments=new {city="Oslo"}})),
                _=>Reply.Text("done")
            };
            await h.SendAsync(session.Id,"Get Oslo weather");await h.IdleAsync(session.Id);
            var requests=h.Catalog.Requests.Where(r=>r.SessionId==session.Id).ToList();
            Check.Equal(4,requests.Count);
            var prefix=string.Join(",",requests[0].Tools.Select(d=>d.Name));
            Check.True(requests.All(r=>string.Join(",",r.Tools.Select(d=>d.Name))==prefix));
            Check.False(requests[0].Tools.Any(d=>d.Name==target.Definition.Name));
            Check.True(hook.Before.Contains(target.Definition.Name));Check.True(hook.After.Contains(target.Definition.Name));
            var result=h.Messages(session.Id).SelectMany(m=>m.ToolResults).Single(x=>x.Name=="mcp_call");
            Check.Contains(result.Content,"Weather: Berlin");
            var original = h.Messages(session.Id).SelectMany(m => m.ToolCalls).Single(c => c.Name == "mcp_call");
            Check.Contains(original.Arguments, "Oslo"); Check.NotContains(original.Arguments, "Berlin");
            Check.Equal(target.Definition.Name,NetPiJson.ToNode(result.Details)!["resolvedTool"]!.GetValue<string>());
            // The same provider-visible catalog can no longer invoke a concrete target switched off in this chat.
            await h.Rpc.CallAsync("agent.setTools",new {sessionId=session.Id,off=new[]{target.Definition.Name}});
            stage=0;
            h.Catalog.Handler=(r,ct)=>stage++==0?Reply.Tools(Reply.Call("mcp_call",new {id=target.Definition.Name,revision=target.Definition.Revision,arguments=new {city="Oslo"}})):Reply.Text("done");
            await h.SendAsync(session.Id,"again");await h.IdleAsync(session.Id);
            Check.True(h.Messages(session.Id).SelectMany(m=>m.ToolResults).Last().IsError);
            stage=0;
            var child = await h.Runtime.SpawnAsync(new SpawnRequest { ParentAgentId=h.Runtime.GetBySession(session.Id)!.Id, Task="call without inherited disclosure", Tools=[target.Definition.Name] });
            await h.StatusAsync(child.Id,AgentStatus.Completed);
            Check.True(h.Messages(child.SessionId).SelectMany(m=>m.ToolResults).Single().IsError);
            Check.False(h.Catalog.Requests.Last().Tools.Any(d=>d.Name==target.Definition.Name));
            Check.True(h.Catalog.Requests.Last().Tools.Any(d=>d.Name=="mcp_call"));
            await manager.DisposeAsync();ctx.Unload();
        });
        t.Add("deferred: hidden changes stay silent and a pinned removal is announced only once", async () => {
            await using var h = await TestHost.StartAsync();
            var ctx = new TestPluginContext(h, "netpi.mcp");
            await using var manager = new ServerManager(ctx);
            var config = new ServerConfig { Id="fixture", Command="dotnet", Cwd=h.Workspace };
            JsonObject Raw(string description) => new() { ["name"]="weather", ["description"]=description, ["inputSchema"]=new JsonObject { ["type"]="object" } };
            var hidden = new RemoteTool(manager, config, Raw("first"));
            var registration = ctx.Tools.Register(hidden);
            var session = h.NewSession();
            h.Catalog.Handler = (r,ct) => Reply.Text("done");
            await h.SendAsync(session.Id,"hello"); await h.IdleAsync(session.Id);
            var changed = new RemoteTool(manager,config,Raw("changed"));
            var next = ctx.Tools.Register(changed); registration.Dispose();
            ctx.Events.Publish("mcp.toolsChanged", new JsonObject { ["serverId"]="fixture", ["reason"]="catalog-refresh", ["updated"]=new JsonArray(changed.Definition.Name) });
            await h.SendAsync(session.Id,"hidden update"); await h.IdleAsync(session.Id);
            Check.False(h.Messages(session.Id).Any(m=>m.MetaString("kind")=="tools"));
            var pinned = new RemoteTool(manager,config with {Pinned=["weather"]},Raw("changed"));
            var pin = ctx.Tools.Register(pinned); next.Dispose();
            await h.SendAsync(session.Id,"pinned"); await h.IdleAsync(session.Id);
            Check.True(h.Catalog.Requests.Last().Tools.Any(d=>d.Name==pinned.Definition.Name));
            pin.Dispose();
            ctx.Events.Publish("mcp.toolsChanged",new JsonObject { ["serverId"]="fixture", ["reason"]="disabled", ["removed"]=new JsonArray(pinned.Definition.Name) });
            await h.SendAsync(session.Id,"removed");await h.IdleAsync(session.Id);
            await h.SendAsync(session.Id,"still removed");await h.IdleAsync(session.Id);
            Check.Equal(1,h.Messages(session.Id).Count(m=>m.Meta?["removed"] is JsonArray a && a.Any(n=>n?.GetValue<string>()==pinned.Definition.Name)));
            ctx.Unload();
        });
        t.Add("deferred: effective target policy blocks remote execution and schema changes make one targeted notice", async () => {
            await using var h=await TestHost.StartAsync();
            var ctx=new TestPluginContext(h,"netpi.mcp");
            var config=new ServerConfig {Id="fixture",Command="dotnet",Args=[typeof(DeferredToolsTests).Assembly.Location,"--mcp-fixture"],Cwd=h.Workspace};
            h.Settings.Set("mcp.servers",new JsonObject {["fixture"]=config.Json()});
            await using var manager=new ServerManager(ctx);await manager.StartAsync([],CancellationToken.None);
            await Wait.Until(() => manager.Catalog().Count > 0, "mcp fixture connected");
            ctx.Tools.Register(new McpSearchTool(ctx,manager));ctx.Tools.Register(new McpCallTool(ctx,manager));
            var target=manager.Catalog().Single();var session=h.NewSession();int stage=0;
            h.Catalog.Handler=(r,ct)=>stage++==0?Reply.Tools(Reply.Call("mcp_search",new {query=target.Definition.Name,detail="schema"})):Reply.Text("done");
            await h.SendAsync(session.Id,"inspect");await h.IdleAsync(session.Id);
            var hook=new Hook{Block=true};h.Services.Register<IAgentHook>(hook);stage=0;
            h.Catalog.Handler=(r,ct)=>stage++==0?Reply.Tools(Reply.Call("mcp_call",new {id=target.Definition.Name,revision=target.Definition.Revision,arguments=new {city="Oslo"}})):Reply.Text("done");
            await h.SendAsync(session.Id,"call");await h.IdleAsync(session.Id);
            var result=h.Messages(session.Id).SelectMany(m=>m.ToolResults).Last();
            Check.True(result.IsError);Check.Contains(result.Content,"effective target blocked");
            h.Settings.Set("mcp.servers",new JsonObject {["fixture"]=(config with {ReadOnly=["weather"]}).Json()});
            await manager.ReconcileAsync(CancellationToken.None);
            h.Catalog.Handler=(r,ct)=>Reply.Text("done");
            await h.SendAsync(session.Id,"changed");await h.IdleAsync(session.Id);
            await h.SendAsync(session.Id,"unchanged");await h.IdleAsync(session.Id);
            var notices=h.Messages(session.Id).Where(m=>m.Meta?["updated"] is JsonArray a && a.Count>0).ToList();
            Check.Equal(1,notices.Count);Check.Equal("remote-server",notices[0].MetaString("cause"));
            await manager.DisposeAsync();ctx.Unload();
        });
    }
}
