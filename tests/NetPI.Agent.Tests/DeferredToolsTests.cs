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
    private sealed class Deferred(string name, string description, bool deferred = true, string? schema = null) : IAgentTool
    {
        public readonly List<string> Calls = [];
        public ToolDefinition Definition { get; } = new()
        {
            Name = name, Description = description, Deferred = deferred,
            Parameters = JsonNode.Parse(schema ?? """{"type":"object","properties":{"city":{"type":"string","description":"The city"}}}""")!.AsObject(),
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
                3 => Reply.Tools(Reply.Call("call_tool", new { name = "tool_search", arguments = new { query = "zzz" } })),
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
            Check.False(results[3].IsError, "naming tool_search runs the search with those arguments: " + results[3].Content);
            Check.Contains(results[3].Content, "No tool matches");
            Check.Equal(0, h.Messages(session.Id).Count(m => m.MetaString("kind") == "tools"), "no tools notice: the tools did not change");
            Check.Equal(null, h.Sessions.GetSession(session.Id)!.Meta?["toolsLoaded"], "a search changes nothing about the chat");
            // a policy block applies to the inner tool
            hook.Block = true; stage = 1; weather.Calls.Clear();
            await h.SendAsync(session.Id, "again"); await h.IdleAsync(session.Id);
            Check.Equal(0, weather.Calls.Count); Check.Contains(h.Messages(session.Id).SelectMany(m => m.ToolResults).Last(r => r.Name == "call_tool").Content, "effective target blocked");
            ctx.Unload();
        });
        t.Add("arguments: one missing final bracket is closed (call_tool's inner arguments too), a cut-off call is not and says so", async () => {
            Check.Equal("""{"a":"x}"}""", NetPI.Runtime.ToolBatch.CloseOneBracket("""{"a":"x}" """));
            Check.Equal("""{"a":[1,2]}""", NetPI.Runtime.ToolBatch.CloseOneBracket("""{"a":[1,2]"""));
            Check.Equal(null, NetPI.Runtime.ToolBatch.CloseOneBracket("""{"a":"open string"""), "ends inside a string");
            Check.Equal(null, NetPI.Runtime.ToolBatch.CloseOneBracket("""{"a":[1,"""), "two open and a trailing comma");
            Check.Equal(null, NetPI.Runtime.ToolBatch.CloseOneBracket("""{"a":1,"""), "a trailing comma");
            Check.Equal(null, NetPI.Runtime.ToolBatch.CloseOneBracket("""{"a":1}"""), "nothing missing");

            await using var h = await TestHost.StartAsync();
            var ctx = new TestPluginContext(h, "netpi.test");
            var weather = new Deferred("weather_lookup", "Current weather and forecast for a city.");
            ctx.Tools.Register(weather);
            var session = h.NewSession(); var stage = 0;
            h.Catalog.Handler = (r, ct) => stage++ switch
            {
                // the inner arguments as a string without their final brace (seen from a local model), and a direct call short of one
                0 => Reply.Tools(Reply.Call("call_tool", new { name = "weather_lookup", arguments = """{"city": "Oslo" """ }),
                                 Reply.Call("weather_lookup", """{"city": "Bergen" """)),
                _ => Reply.Text("done"),
            };
            await h.SendAsync(session.Id, "weather?"); await h.IdleAsync(session.Id);
            var results = h.Messages(session.Id).SelectMany(m => m.ToolResults).ToList();
            Check.True(results.All(r => !r.IsError), string.Join(" | ", results.Select(r => r.Content)));
            Check.Equal("""{"city": "Oslo"}|{"city": "Bergen"}""", string.Join("|", weather.Calls));
            var direct = h.Messages(session.Id).SelectMany(m => m.ToolCalls).Single(c => c.Name == "weather_lookup");
            Check.Equal("""{"city": "Bergen"}""", direct.Arguments, "the stored call holds valid JSON");

            // the same call from a response cut off at its output limit is not guessed at, and the message says why
            var message = NetPI.Runtime.ToolBatch.InvalidJson("edit", Error("""{"a":"x" """), outputLimit: true);
            Check.Contains(message, "output limit");
            message = NetPI.Runtime.ToolBatch.InvalidJson("edit", Error("""{"a":"x" """), outputLimit: false);
            Check.Contains(message, "closing brackets"); Check.NotContains(message, "too long");
            ctx.Unload();

            static JsonException Error(string json)
            {
                try { JsonDocument.Parse(json); }
                catch (JsonException ex) { return ex; }
                throw new InvalidOperationException("parsed");
            }
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
            // the infrastructure ignores the off-list: switching it off must not strand the deferred tools
            await h.Rpc.CallAsync("agent.setTools",new {sessionId=session.Id,off=new[]{"call_tool","tool_search"}});
            await h.SendAsync(session.Id,"third");await h.IdleAsync(session.Id);
            Check.True(h.Catalog.Requests.Last().Tools.Any(d=>d.Name=="call_tool") && h.Catalog.Requests.Last().Tools.Any(d=>d.Name=="tool_search"));
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
        t.Add("deferred: a model that writes call_tool's envelope twice still reaches the tool; a shape it cannot prove is answered with the shape", async () => {
            await using var h = await TestHost.StartAsync();
            var ctx = new TestPluginContext(h, "netpi.test");
            var weather = new Deferred("weather_lookup", "Current weather and forecast for a city.");
            var proxy = new Deferred("rpc_proxy", "Call a remote tool by name.",
                schema: """{"type":"object","properties":{"name":{"type":"string"},"arguments":{"type":"object"}},"required":["name","arguments"]}""");
            ctx.Tools.Register(weather); ctx.Tools.Register(proxy);
            var session = h.NewSession(); var stage = 0;
            h.Catalog.Handler = (r, ct) => stage++ switch
            {
                // the envelope written twice: the tool inside is the one the model meant, at both levels
                0 => Reply.Tools(Reply.Call("call_tool", new { name = "weather_lookup", arguments = new { name = "weather_lookup", arguments = new { city = "Oslo" } } })),
                1 => Reply.Tools(Reply.Call("call_tool", new { name = "call_tool", arguments = new { name = "weather_lookup", arguments = new { city = "Bergen" } } })),
                // the envelope one level deep, with no name of its own
                2 => Reply.Tools(Reply.Call("call_tool", new { arguments = new { name = "weather_lookup", arguments = new { city = "Turin" } } })),
                // not a repeat: the inner name is another tool, so nothing is proved and the tool answers with its shape
                3 => Reply.Tools(Reply.Call("call_tool", new { name = "weather_lookup", arguments = new { name = "rpc_proxy", arguments = new { city = "Gdynia" } } })),
                // a tool that really takes a name and an arguments object: its own pair is left alone
                4 => Reply.Tools(Reply.Call("call_tool", new { name = "rpc_proxy", arguments = new { name = "remote_tool", arguments = new { city = "Oslo" } } })),
                _ => Reply.Text("done"),
            };
            await h.SendAsync(session.Id, "weather?"); await h.IdleAsync(session.Id);
            var results = h.Messages(session.Id).SelectMany(m => m.ToolResults).ToList();
            Check.Equal("""{"city":"Oslo"}|{"city":"Bergen"}|{"city":"Turin"}""", string.Join("|", weather.Calls), "each nested envelope ran the tool");
            Check.True(results[3].IsError, results[3].Content);
            Check.Contains(results[3].Content, "was reached with call_tool's own envelope");
            Check.Contains(results[3].Content, """call_tool {"name": "weather_lookup", "arguments": {"city": …}}""", results[3].Content);
            Check.Equal(3, weather.Calls.Count, "the answered call did not run");
            Check.False(results[4].IsError, results[4].Content);
            Check.Equal("""{"name":"remote_tool","arguments":{"city":"Oslo"}}""", proxy.Calls.Single(), "its own name/arguments pair");
            ctx.Unload();
        });
        t.Add("deferred: a call_tool without a name is inferred from its arguments, tool_search runs through it, call_tool keeps its error", async () => {
            await using var h = await TestHost.StartAsync();
            var ctx = new TestPluginContext(h, "netpi.test");
            var todos = new Deferred("todo_write", "Write the todo list.", schema: """{"type":"object","properties":{"items":{"type":"array","description":"The items"}},"required":["items"]}""");
            var spawn = new Deferred("agent_spawn", "Spawn a subagent for a task.", schema: """{"type":"object","properties":{"agent":{"type":"string"},"task":{"type":"string"}},"required":["agent","task"]}""");
            var notes = new Deferred("read_notes", "Read the notes.", schema: """{"type":"object","properties":{"note_path":{"type":"string"}}}""");
            var more = new Deferred("read_more", "Read more notes.", schema: """{"type":"object","properties":{"note_path":{"type":"string"}}}""");
            var weather = new Deferred("weather_lookup", "Current weather and forecast for a city.");
            ctx.Tools.Register(todos); ctx.Tools.Register(spawn); ctx.Tools.Register(notes); ctx.Tools.Register(more); ctx.Tools.Register(weather);
            var session = h.NewSession(); var stage = 0;
            h.Catalog.Handler = (r, ct) => stage++ switch
            {
                // one tool takes exactly these arguments: the name is inferred
                0 => Reply.Tools(Reply.Call("call_tool", new { arguments = new { items = new[] { new { text = "ship it", status = "open" } } } })),
                // both required properties present: inferred too
                1 => Reply.Tools(Reply.Call("call_tool", new { arguments = new { agent = "qwen38-27b", task = "check the code" } })),
                // two tools take them (neither declares required): the answer names both
                2 => Reply.Tools(Reply.Call("call_tool", new { arguments = new { note_path = "notes.md" } })),
                // no tool takes them: the hint is tool_search
                3 => Reply.Tools(Reply.Call("call_tool", new { arguments = new { zzz = 1 } })),
                // an extra key no schema declares: no match
                4 => Reply.Tools(Reply.Call("call_tool", new { arguments = new { city = "Oslo", extra = 1 } })),
                // naming tool_search runs the search with those arguments
                5 => Reply.Tools(Reply.Call("call_tool", new { name = "tool_search", arguments = new { query = "weather" } })),
                // naming call_tool without an envelope inside: the error stays
                6 => Reply.Tools(Reply.Call("call_tool", new { name = "call_tool", arguments = new { query = "x" } })),
                _ => Reply.Text("done"),
            };
            await h.SendAsync(session.Id, "todos?"); await h.IdleAsync(session.Id);
            var results = h.Messages(session.Id).SelectMany(m => m.ToolResults).ToList();
            Check.False(results[0].IsError, results[0].Content);
            Check.Equal("call_tool", results[0].Name, "the result belongs to the call the model made");
            Check.Equal("""{"items":[{"text":"ship it","status":"open"}]}""", todos.Calls.Single(), "the inferred tool ran");
            Check.False(results[1].IsError, results[1].Content);
            Check.Equal("""{"agent":"qwen38-27b","task":"check the code"}""", spawn.Calls.Single());
            Check.True(results[2].IsError, results[2].Content);
            Check.Contains(results[2].Content, """call_tool needs "name". Tools that take these arguments: read_more, read_notes.""");
            Check.True(results[3].IsError); Check.Contains(results[3].Content, "No tool takes these arguments; find it with tool_search.");
            Check.True(results[4].IsError); Check.Contains(results[4].Content, "No tool takes these arguments; find it with tool_search.");
            Check.False(results[5].IsError, results[5].Content);
            Check.Contains(results[5].Content, "weather_lookup"); Check.Contains(results[5].Content, "schema:");
            Check.True(results[6].IsError); Check.Contains(results[6].Content, "call it directly");
            // the inferred call is stored with its name: the readers see the tool that ran
            var stored = h.Messages(session.Id).SelectMany(m => m.ToolCalls).ToList();
            Check.Equal("call_tool", stored[0].Name);
            Check.Equal("todo_write", NetPI.ToolCalls.EffectiveName(stored[0]), "the reader names the inner tool");
            Check.Contains(stored[0].Arguments, "{\"name\":\"todo_write\",\"arguments\":{\"items\":");
            ctx.Unload();
        });
    }
}
