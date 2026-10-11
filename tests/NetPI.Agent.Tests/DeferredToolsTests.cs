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
        t.Add("deferred: tool_search loads a deferred tool for the next call, a call by name runs and loads one, with tools notices naming the cause", async () => {
            await using var h = await TestHost.StartAsync();
            var ctx = new TestPluginContext(h, "netpi.test");
            var weather = new Deferred("weather_lookup", "Current weather and forecast for a city.");
            var stocks = new Deferred("stock_quote", "Share prices of a listed company.");
            ctx.Tools.Register(weather); ctx.Tools.Register(stocks);
            var session = h.NewSession(); var stage = 0;
            h.Catalog.Handler = (r, ct) => stage++ switch
            {
                0 => Reply.Tools(Reply.Call("tool_search", new { query = "weather forecast" })),
                1 => Reply.Tools(Reply.Call("weather_lookup", new { city = "Oslo" })),
                2 => Reply.Tools(Reply.Call("stock_quote", new { city = "ACME" })),
                _ => Reply.Text("done"),
            };
            await h.SendAsync(session.Id, "weather in Oslo?"); await h.IdleAsync(session.Id);
            var requests = h.Catalog.Requests.Where(r => r.SessionId == session.Id).ToList();
            Check.Equal(4, requests.Count);
            Check.True(requests[0].Tools.Any(d => d.Name == "tool_search"), "a chat with deferred tools is sent tool_search");
            Check.False(requests[0].Tools.Any(d => d.Name == "weather_lookup"));
            Check.True(requests[1].Tools.Any(d => d.Name == "weather_lookup"), "loaded from the next call");
            Check.False(requests[1].Tools.Any(d => d.Name == "stock_quote"), "only the match is loaded");
            Check.False(requests[2].Tools.Any(d => d.Name == "stock_quote"));
            Check.True(requests[3].Tools.Any(d => d.Name == "stock_quote"), "a call by name loads the tool");
            var results = h.Messages(session.Id).SelectMany(m => m.ToolResults).ToList();
            Check.Contains(results[0].Content, "weather_lookup"); Check.NotContains(results[0].Content, "stock_quote");
            Check.False(results[1].IsError); Check.Equal(1, weather.Calls.Count);
            Check.False(results[2].IsError, "a deferred tool called by name runs"); Check.Equal(1, stocks.Calls.Count);
            Check.Equal("[\"stock_quote\",\"weather_lookup\"]", h.Sessions.GetSession(session.Id)!.Meta?[LoadedTools.MetaKey]?.ToJsonString());
            var notices = h.Messages(session.Id).Where(m => m.MetaString("kind") == "tools").ToList();
            Check.Equal(2, notices.Count);
            Check.True(notices.All(n => n.MetaString("cause") == "loaded"));
            Check.Contains(notices[0].Text, "New: weather_lookup (loaded for this chat)");

            // loaded is not a way around the chat's own switches
            await h.Rpc.CallAsync("agent.setTools", new { sessionId = session.Id, off = new[] { "weather_lookup" } });
            h.Catalog.Handler = (r, ct) => Reply.Text("done");
            await h.SendAsync(session.Id, "again"); await h.IdleAsync(session.Id);
            Check.False(h.Catalog.Requests.Last().Tools.Any(d => d.Name == "weather_lookup"));
            ctx.Unload();
        });
        t.Add("deferred: the default tools.deferred keeps built-in tools out until used, the prompt names them, a subagent's list declares them", async () => {
            await using var h = await TestHost.StartAsync();
            h.Settings.Set(ToolExposure.Setting, null);   // the default list (the test host declares everything)
            var ctx = new TestPluginContext(h, "netpi.test");
            var fetch = new Deferred("web_fetch", "Fetch a web page as Markdown.", deferred: false);
            var lsp = new Deferred("lsp", "Diagnostics, definitions and symbols from a language server.", deferred: false);
            var notes = new Deferred("read_notes", "Read the notes.", deferred: false);
            ctx.Tools.Register(fetch); ctx.Tools.Register(lsp); ctx.Tools.Register(notes);
            var session = h.NewSession(); var stage = 0;
            h.Catalog.Handler = (r, ct) => r.SessionId != session.Id ? Reply.Text("child done")
                : stage++ == 0 ? Reply.Tools(Reply.Call("web_fetch", new { city = "https://example.com" })) : Reply.Text("done");
            await h.SendAsync(session.Id, "fetch it"); await h.IdleAsync(session.Id);
            var requests = h.Catalog.Requests.Where(r => r.SessionId == session.Id).ToList();
            var first = requests[0].Tools.Select(d => d.Name).ToList();
            Check.True(first.Contains("read_notes") && first.Contains("tool_search"), string.Join(",", first));
            Check.False(first.Contains("web_fetch") || first.Contains("lsp") || first.Contains("agent_spawn"), string.Join(",", first));
            Check.Contains(requests[0].SystemPrompt, "More tools, not loaded yet");
            Check.Contains(requests[0].SystemPrompt, "  - web_fetch: Fetch a web page as Markdown.");
            Check.Contains(requests[0].SystemPrompt, "  - lsp: Diagnostics");
            Check.Equal(1, fetch.Calls.Count);
            Check.True(requests[1].Tools.Any(d => d.Name == "web_fetch"), "called by name: declared from the next call");
            Check.False(requests[1].Tools.Any(d => d.Name == "lsp"));

            // a subagent given lsp by name has it declared from its first call
            var child = await h.Runtime.SpawnAsync(new SpawnRequest { ParentAgentId = h.Runtime.GetBySession(session.Id)!.Id, Task = "check the code", Tools = ["lsp", "read_notes"] });
            await h.StatusAsync(child.Id, AgentStatus.Completed);
            var childTools = h.Catalog.Requests.First(r => r.SessionId == child.SessionId).Tools.Select(d => d.Name).ToList();
            Check.True(childTools.Contains("lsp"), string.Join(",", childTools));
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
        t.Add("deferred: one MCP tool among 250 is loaded with tool_search and called directly, through the policy hooks", async () => {
            await using var h=await TestHost.StartAsync();
            var ctx=new TestPluginContext(h,"netpi.mcp");
            var config=new ServerConfig {Id="fixture",Command="dotnet",Args=[typeof(DeferredToolsTests).Assembly.Location,"--mcp-fixture","large"],Cwd=h.Workspace};
            h.Settings.Set("mcp.servers",new JsonObject {["fixture"]=config.Json()});
            await using var manager=new ServerManager(ctx);await manager.StartAsync([],CancellationToken.None);
            await Wait.Until(() => manager.Catalog().Count > 0, "mcp fixture connected");
            var target=manager.Catalog().Single(x=>x.RemoteName=="weather");
            var hook=new Hook {Arguments="""{"city":"Berlin"}"""};h.Services.Register<IAgentHook>(hook);
            var session=h.NewSession();int stage=0;
            h.Catalog.Handler=(r,ct)=>stage++ switch {
                0=>Reply.Tools(Reply.Call("tool_search",new {query=target.Definition.Name,limit=1})),
                1=>Reply.Tools(Reply.Call(target.Definition.Name,new {city="Oslo"})),
                _=>Reply.Text("done")
            };
            await h.SendAsync(session.Id,"Get Oslo weather");await h.IdleAsync(session.Id);
            var requests=h.Catalog.Requests.Where(r=>r.SessionId==session.Id).ToList();
            Check.Equal(3,requests.Count);
            Check.False(requests[0].Tools.Any(d=>d.Name==target.Definition.Name), "none of the 250 is sent before it is loaded");
            Check.True(requests[0].Tools.Any(d=>d.Name=="tool_search"));
            Check.Equal(1,requests[1].Tools.Count(d=>d.Category=="mcp"), "only the loaded one: " + string.Join(",",requests[1].Tools.Select(d=>d.Name)));
            Check.True(hook.Before.Contains(target.Definition.Name));Check.True(hook.After.Contains(target.Definition.Name));
            var result=h.Messages(session.Id).SelectMany(m=>m.ToolResults).Single(x=>x.Name==target.Definition.Name);
            Check.Contains(result.Content,"Weather: Berlin", "the hook's arguments are what the server received");
            // switched off in this chat: a call by name no longer reaches it
            await h.Rpc.CallAsync("agent.setTools",new {sessionId=session.Id,off=new[]{target.Definition.Name}});
            stage=1;
            await h.SendAsync(session.Id,"again");await h.IdleAsync(session.Id);
            Check.True(h.Messages(session.Id).SelectMany(m=>m.ToolResults).Last().IsError);
            Check.False(h.Catalog.Requests.Last().Tools.Any(d=>d.Name==target.Definition.Name));
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
        t.Add("deferred: a policy hook blocks a remote call, and a loaded tool's schema change makes one targeted notice", async () => {
            await using var h=await TestHost.StartAsync();
            var ctx=new TestPluginContext(h,"netpi.mcp");
            var config=new ServerConfig {Id="fixture",Command="dotnet",Args=[typeof(DeferredToolsTests).Assembly.Location,"--mcp-fixture"],Cwd=h.Workspace};
            h.Settings.Set("mcp.servers",new JsonObject {["fixture"]=config.Json()});
            await using var manager=new ServerManager(ctx);await manager.StartAsync([],CancellationToken.None);
            await Wait.Until(() => manager.Catalog().Count > 0, "mcp fixture connected");
            var target=manager.Catalog().Single();var session=h.NewSession();int stage=0;
            h.Catalog.Handler=(r,ct)=>stage++==0?Reply.Tools(Reply.Call("tool_search",new {query=target.Definition.Name,limit=1})):Reply.Text("done");
            await h.SendAsync(session.Id,"load");await h.IdleAsync(session.Id);
            var hook=new Hook{Block=true};h.Services.Register<IAgentHook>(hook);stage=0;
            h.Catalog.Handler=(r,ct)=>stage++==0?Reply.Tools(Reply.Call(target.Definition.Name,new {city="Oslo"})):Reply.Text("done");
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
