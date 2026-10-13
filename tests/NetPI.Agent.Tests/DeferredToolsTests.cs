using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Mcp;
namespace NetPI.Agent.Tests;

public static class DeferredToolsTests
{
    private const double ToolSearchRelative = 0.4;
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
        t.Add("deferred: finding a tool never changes the request's tools; tool_search shows schemas, call_tool runs through the hooks", async () => {
            await using var h = await TestHost.StartAsync();
            var ctx = new TestPluginContext(h, "netpi.test");
            var weather = new Deferred("weather_lookup", "Current weather and forecast for a city.");
            var stocks = new Deferred("stock_quote", "Share prices of a listed company.");
            var hook = new Hook(); h.Services.Register<IAgentHook>(hook);
            ctx.Tools.Register(weather); ctx.Tools.Register(stocks);
            var session = h.NewSession(); var stage = 0;
            h.Catalog.Handler = (r, ct) => stage++ switch
            {
                0 => Reply.Tools(Reply.Call("tool_search", new { query = "weather forecast" })),
                1 => Reply.Tools(Reply.Call("call_tool", new { name = "weather_lookup", arguments = new { city = "Oslo" } })),
                2 => Reply.Tools(Reply.Call("call_tool", new { name = "nope", arguments = new { } })),
                3 => Reply.Tools(Reply.Call("call_tool", new { name = "tool_search", arguments = new { query = "x" } })),
                _ => Reply.Text("done"),
            };
            await h.SendAsync(session.Id, "weather in Oslo?"); await h.IdleAsync(session.Id);
            var requests = h.Catalog.Requests.Where(r => r.SessionId == session.Id).ToList();
            Check.Equal(5, requests.Count);
            var first = string.Join(",", requests[0].Tools.Select(d => d.Name));
            Check.True(first.Contains("call_tool") && first.Contains("tool_search"), first);
            foreach (var r in requests) Check.Equal(first, string.Join(",", r.Tools.Select(d => d.Name)), "loading leaves the tools untouched");
            var results = h.Messages(session.Id).SelectMany(m => m.ToolResults).ToList();
            Check.Contains(results[0].Content, "weather_lookup"); Check.Contains(results[0].Content, "schema:"); Check.Contains(results[0].Content, "\"city\"");
            Check.NotContains(results[0].Content, "stock_quote", "only the match");
            Check.False(results[1].IsError); Check.Equal("""{"city":"Oslo"}""", weather.Calls.Single());
            Check.Equal("call_tool", results[1].Name, "the result belongs to the call the model made");
            Check.True(hook.Before.Contains("weather_lookup") && hook.After.Contains("weather_lookup"), "the hooks see the inner tool");
            Check.True(results[2].IsError); Check.Contains(results[2].Content, "Unknown tool 'nope'");
            Check.True(results[3].IsError); Check.Contains(results[3].Content, "call it directly");
            Check.Equal(0, h.Messages(session.Id).Count(m => m.MetaString("kind") == "tools"), "no tools notice: the tools did not change");
            Check.Equal(null, h.Sessions.GetSession(session.Id)!.Meta?["toolsLoaded"], "a search changes nothing about the chat");
            // a policy block applies to the inner tool
            hook.Block = true; stage = 1; weather.Calls.Clear();
            await h.SendAsync(session.Id, "again"); await h.IdleAsync(session.Id);
            Check.Equal(0, weather.Calls.Count); Check.Contains(h.Messages(session.Id).SelectMany(m => m.ToolResults).Last(r => r.Name == "call_tool").Content, "effective target blocked");
            ctx.Unload();
        });
        t.Add("deferred: tool_search returns only real matches", async () => {
            var tools = new[] { new Deferred("a_forecast", "Weather forecast for a city.").Definition, new Deferred("b_weather", "Weather forecast forecast forecast.").Definition, new Deferred("c_city", "Notes about the weather in a city.").Definition };
            Check.True(ToolRanking.Rank(tools, "weather forecast").Any(d => d.Name == "c_city"), "without the cut, the one-word overlap matches");
            Check.False(ToolRanking.Rank(tools, "weather forecast", ToolSearchRelative).Any(d => d.Name == "c_city"), "a one-word overlap is not a match");
            await Task.CompletedTask;
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
            Check.Contains(requests[0].SystemPrompt, "More tools, not in your tool list");
            Check.Contains(requests[0].SystemPrompt, "  - web_fetch: Fetch a web page as Markdown.");
            Check.Contains(requests[0].SystemPrompt, "  - lsp: Diagnostics");
            Check.Equal(1, fetch.Calls.Count);
            Check.False(requests[1].Tools.Any(d => d.Name == "web_fetch"), "called by name: the request's tools do not change");
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
        t.Add("deferred: one MCP tool among 250 is found with tool_search and run with call_tool, through the policy hooks", async () => {
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
                1=>Reply.Tools(Reply.Call("call_tool",new {name=target.Definition.Name,arguments=new {city="Oslo"}})),
                _=>Reply.Text("done")
            };
            await h.SendAsync(session.Id,"Get Oslo weather");await h.IdleAsync(session.Id);
            var requests=h.Catalog.Requests.Where(r=>r.SessionId==session.Id).ToList();
            Check.Equal(3,requests.Count);
            Check.True(requests[0].Tools.Any(d=>d.Name=="tool_search") && requests[0].Tools.Any(d=>d.Name=="call_tool"));
            foreach (var r in requests) Check.Equal(0,r.Tools.Count(d=>d.Category=="mcp"), "none of the 250 is ever sent: " + string.Join(",",r.Tools.Select(d=>d.Name)));
            Check.Contains(h.Messages(session.Id).SelectMany(m=>m.ToolResults).First().Content, target.Definition.Name);
            Check.True(hook.Before.Contains(target.Definition.Name));Check.True(hook.After.Contains(target.Definition.Name));
            var result=h.Messages(session.Id).SelectMany(m=>m.ToolResults).Single(x=>x.Name=="call_tool");
            Check.Contains(result.Content,"Weather: Berlin", "the hook's arguments are what the server received");
            // switched off in this chat: call_tool no longer reaches it
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
        t.Add("deferred: a policy hook blocks a remote call made with call_tool, and a deferred tool's schema change makes no notice", async () => {
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
            h.Catalog.Handler=(r,ct)=>stage++==0?Reply.Tools(Reply.Call("call_tool",new {name=target.Definition.Name,arguments=new {city="Oslo"}})):Reply.Text("done");
            await h.SendAsync(session.Id,"call");await h.IdleAsync(session.Id);
            var result=h.Messages(session.Id).SelectMany(m=>m.ToolResults).Last();
            Check.True(result.IsError);Check.Contains(result.Content,"effective target blocked");
            h.Settings.Set("mcp.servers",new JsonObject {["fixture"]=(config with {ReadOnly=["weather"]}).Json()});
            await manager.ReconcileAsync(CancellationToken.None);
            h.Catalog.Handler=(r,ct)=>Reply.Text("done");
            await h.SendAsync(session.Id,"changed");await h.IdleAsync(session.Id);
            await h.SendAsync(session.Id,"unchanged");await h.IdleAsync(session.Id);
            var notices=h.Messages(session.Id).Where(m=>m.Meta?["updated"] is JsonArray a && a.Count>0).ToList();
            Check.Equal(0,notices.Count,"the tool is not in the request: nothing to tell the model");
            await manager.DisposeAsync();ctx.Unload();
        });
    }
}
