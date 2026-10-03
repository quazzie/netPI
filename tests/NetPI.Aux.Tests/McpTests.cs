using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using NetPI.Mcp;

namespace NetPI.Aux.Tests;
public static class McpTests
{
    internal static ServerConfig Config(string mode = "modern") => new() {
        Id="fixture", Command="dotnet", Args=[typeof(McpTests).Assembly.Location, "--mcp-fixture", mode],
        Cwd=Path.GetTempPath(), ConnectTimeoutMs=5000, CallTimeoutMs=2000
    };
    /// <summary>Wait for a server to report connected: since the cold start connects in the background, this is what StartAsync no longer guarantees.</summary>
    internal static async Task Connected(ServerManager manager, string id, int ms = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (Status(manager, id) != "connected")
        {
            if (sw.ElapsedMilliseconds > ms) throw new AssertException($"MCP server {id} did not connect: {Status(manager, id)}");
            await Task.Delay(50);
        }
    }
    internal static string? Status(ServerManager manager, string id) =>
        (manager.Snapshot()["servers"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(s => (string?)s["id"] == id)?["status"]?.GetValue<string>();
    internal static JsonObject Tool(string name="weather") => JsonNode.Parse("""{"name":"weather","description":"Get weather for a city","inputSchema":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}}""")!.AsObject().WithName(name);
    private static JsonObject WithName(this JsonObject tool, string name) { tool["name"]=name; return tool; }
    private static ToolContext Context(FakePluginContext ctx, string session="ses_1") => new() { SessionId=session, AgentId="agt_1", CallId="call_1", Cwd=ctx.Paths.DefaultWorkspace, Services=ctx.Services, Events=ctx.Events };
    public static void Register(TestRunner r)
    {
        r.Add("mcp: config rejects credentials and preserves repeated process arguments", () => {
            var c = ServerConfig.Parse("a", new JsonObject { ["command"]="dotnet", ["args"]=new JsonArray("a","a") }, Path.GetTempPath());
            Check.Equal(2, c.Args.Length);
            Check.Throws<RpcException>(() => ServerConfig.Parse("a", new JsonObject { ["transport"]="http", ["url"]="https://user:secret@example.com" }, Path.GetTempPath()));
            Check.Throws<RpcException>(() => ServerConfig.Parse("a", new JsonObject { ["transport"]="http", ["url"]="https://example.com", ["headerEnv"]=new JsonObject { ["Mcp-Name"]="TOKEN" } }, Path.GetTempPath()));
        });
        r.Add("mcp: names and revisions remain stable and disambiguate case and punctuation", () => {
            var ctx=new FakePluginContext(); var manager=new ServerManager(ctx); var c=Config();
            var a=new RemoteTool(manager,c,Tool("A-b")); var b=new RemoteTool(manager,c,Tool("a_b"));
            Check.False(a.Definition.Name==b.Definition.Name); Check.True(a.Definition.Name.Length<=64);
            Check.Equal(a.Definition.Revision,new RemoteTool(manager,c,Tool("A-b")).Definition.Revision);
            Check.False(a.Definition.ReadOnly); Check.True(a.Definition.Deferred);
            Check.False(a.Definition.Revision==new RemoteTool(manager,c with { ReadOnly=["A-b"] },Tool("A-b")).Definition.Revision);
            ctx.Unload();
        });
        r.Add("mcp: a tool is read-only when its server says so (readOnlyHint) or the config lists it, and only then", () => {
            var ctx=new FakePluginContext(); var manager=new ServerManager(ctx); var c=Config();
            RemoteTool Annotated(string name, string annotations) { var t=Tool(name); t["annotations"]=JsonNode.Parse(annotations); return new RemoteTool(manager,c,t); }
            Check.True(Annotated("get","""{"readOnlyHint":true}""").Definition.ReadOnly);
            Check.False(Annotated("set","""{"readOnlyHint":false,"destructiveHint":true}""").Definition.ReadOnly);
            Check.False(Annotated("quiet","""{"title":"Quiet"}""").Definition.ReadOnly);
            Check.False(Annotated("odd","""{"readOnlyHint":"true"}""").Definition.ReadOnly);
            Check.False(new RemoteTool(manager,c,Tool("plain")).Definition.ReadOnly);
            Check.True(new RemoteTool(manager,c with { ReadOnly=["listed"] },Tool("listed")).Definition.ReadOnly);
            Check.False(Annotated("get","""{"readOnlyHint":true}""").Definition.Revision==new RemoteTool(manager,c,Tool("get")).Definition.Revision);
            ctx.Unload();
        });
        r.Add("mcp: local schema validates refs combinations constraints and rejects unsupported assertions", () => {
            var schema=JsonNode.Parse("""{"type":"object","$defs":{"city":{"type":"string","minLength":2}},"properties":{"city":{"$ref":"#/$defs/city"},"n":{"type":"integer","minimum":1}},"required":["city"],"additionalProperties":false}""")!.AsObject();
            Schema.Check(schema); Schema.Validate(schema,JsonNode.Parse("""{"city":"Oslo","n":2}"""));
            Check.Throws<McpException>(()=>Schema.Validate(schema,JsonNode.Parse("""{"city":"x"}""")));
            Check.Throws<McpException>(()=>Schema.Validate(schema,JsonNode.Parse("""{"city":"Oslo","extra":1}""")));
            Check.Throws<McpException>(()=>Schema.Check(JsonNode.Parse("""{"type":"object","unevaluatedProperties":false}""")!.AsObject()));
            Check.Throws<McpException>(()=>Schema.Check(JsonNode.Parse("""{"type":"object","properties":{"x":{"$ref":"https://example.com"}}}""")!.AsObject()));
            var named=JsonNode.Parse("""{"type":"object","properties":{"locks":{"type":"object","propertyNames":{"enum":["front","back"]},"additionalProperties":{"type":"string"}}}}""")!.AsObject();
            Schema.Check(named); Schema.Validate(named,JsonNode.Parse("""{"locks":{"front":"on"}}"""));
            Check.Throws<McpException>(()=>Schema.Validate(named,JsonNode.Parse("""{"locks":{"garage":"on"}}""")));
            var none=JsonNode.Parse("""{"type":"object","properties":{"locks":{"type":"object"}},"propertyNames":false}""")!.AsObject();
            Schema.Check(none); Check.Throws<McpException>(()=>Schema.Validate(none,JsonNode.Parse("""{"locks":{}}""")));
            Check.Throws<McpException>(()=>Schema.Check(JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"object","propertyNames":{"unevaluatedProperties":false}}}}""")!.AsObject()));
        });
        r.Add("mcp: result mapping retains structured data and images without leaking binary into text", () => {
            var result=ResultAdapter.Convert(JsonNode.Parse("""{"content":[{"type":"text","text":"hello"},{"type":"image","mimeType":"image/png","data":"AQID"},{"type":"audio","mimeType":"audio/wav","data":"SEVDUkVU"},{"type":"resource_link","name":"doc","uri":"https://example.com/doc"}],"structuredContent":{"ok":true},"isError":true}""")!.AsObject(),"s","t",100);
            Check.True(result.IsError); Check.Equal(1,result.Images!.Count);
            Check.Contains(result.Content,"hello"); Check.Contains(result.Content,"{\"ok\":true}");
            Check.NotContains(result.Content,"SEVDUkVU"); Check.NotContains(NetPiJson.ToElement(result.Details).GetRawText(),"SEVDUkVU");
        });
        foreach(var mode in new[]{"modern","legacy"})
        r.Add("mcp: stdio "+mode+" discovery calls concurrency cancellation and cleanup", async () => {
            await using var c=new McpConnection(Config(mode),100000,_=>{});
            using var timeout=new CancellationTokenSource(10000);
            await c.InitializeAsync(timeout.Token); Check.Equal(mode=="modern",c.Modern);
            var tools=await c.ListAsync(100,100000,timeout.Token); Check.Equal(1,tools.Count);
            var schema=tools[0]["inputSchema"]!.AsObject();
            var calls=await Task.WhenAll(Enumerable.Range(0,8).Select(i=>c.CallAsync("weather",T.Args(new {city="city"+i}),schema,timeout.Token)));
            for(var i=0;i<8;i++) Check.Contains(calls[i].ToJsonString(),"city"+i);
            using var cancel=new CancellationTokenSource(50);
            await Check.ThrowsAsync<OperationCanceledException>(()=>c.CallAsync("weather",T.Args(new {city="slow"}),schema,cancel.Token));
            Check.Contains((await c.CallAsync("weather",T.Args(new {city="after"}),schema,timeout.Token)).ToJsonString(),"after");
        });
        r.Add("mcp: bounded framing malformed stdout and catalog limits fail explicitly", async () => {
            await Check.ThrowsAsync<McpException>(()=>Protocol.ReadLineAsync(new StringReader(new string('x',100)),50,CancellationToken.None));
            await using var c=new McpConnection(Config("large"),1000000,_=>{});
            using var timeout=new CancellationTokenSource(10000);
            await c.InitializeAsync(timeout.Token);
            await Check.ThrowsAsync<McpException>(()=>c.ListAsync(5,1000000,timeout.Token));
            var schema=Tool()["inputSchema"]!.AsObject();
            await Check.ThrowsAsync<Exception>(()=>c.CallAsync("weather",T.Args(new {city="malformed"}),schema,timeout.Token));
        });
        r.Add("mcp: a query that shares no word with any tool finds the closest by meaning (embeddings), and says so", async () => {
            foreach (var withEmbeddings in new[] { false, true })
            {
                var ctx=new FakePluginContext();
                ctx.Sessions.CreateSession(new SessionInfo {Id="ses_1"});
                // "forecast" and "rainy" mean weather to this embedder; the word ranking has never heard of them.
                if (withEmbeddings) ctx.ServicesFake.Register<IEmbeddingService>(new Synonyms());
                ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=Config().Json()});
                await using var manager=new ServerManager(ctx); await manager.StartAsync([],CancellationToken.None);
                await Connected(manager,"fixture");
                var search=new McpSearchTool(ctx,manager); ctx.Tools.Register(search);
                var result=JsonNode.Parse((await search.ExecuteAsync(Context(ctx),T.Args(new {query="rainy forecast"}),CancellationToken.None)).Content)!;
                Check.Equal(!withEmbeddings,(bool)result["noMatch"]!);
                if (withEmbeddings) { Check.Contains(result["match"]!.GetValue<string>(),"meaning"); Check.Contains(result["results"]!.ToJsonString(),"weather"); }
                else Check.True(result["match"] is null);
            }
        });
        r.Add("mcp: 250 tools keep visible schema prefix constant and disclosure scoped to retained chat", async () => {
            var ctx=new FakePluginContext();
            ctx.Sessions.CreateSession(new SessionInfo {Id="ses_1"});
            ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=Config("large").Json()});
            await using var manager=new ServerManager(ctx); await manager.StartAsync([],CancellationToken.None);
            await Connected(manager,"fixture");
            var search=new McpSearchTool(ctx,manager); var call=new McpCallTool(ctx,manager);
            ctx.Tools.Register(search);ctx.Tools.Register(call);
            Check.Equal(250,manager.Catalog().Count);
            var eligible=ToolSelection.Eligible(ctx.Tools,null,ctx.Sessions.GetSession("ses_1"));
            Check.Equal("mcp_call,mcp_search",string.Join(",",ToolSelection.Visible(eligible).Select(t=>t.Name)));
            var target=manager.Catalog().Single(t=>t.RemoteName=="weather");
            var context=Context(ctx);
            var envelope=T.Args(new {id=target.Definition.Name,revision=target.Definition.Revision,arguments=new {city="Oslo"}});
            await Check.ThrowsAsync<McpException>(()=>call.ResolveAsync(context,envelope,CancellationToken.None).AsTask());
            var disclosure=await search.ExecuteAsync(context,T.Args(new {query=target.Definition.Name,detail="schema"}),CancellationToken.None);
            Check.Contains(disclosure.Content,"city"); Check.NotContains(disclosure.Content,"UNTRUSTED_SERVER");
            var msg=ctx.Sessions.AppendMessage("ses_1",new ChatMessage {Role=MessageRole.Tool,Parts=[new ToolResultPart {Name="mcp_search",CallId="search",Content=disclosure.Content,Details=NetPiJson.ToNode(disclosure.Details)}]});
            var resolved=await call.ResolveAsync(context,envelope,CancellationToken.None);
            Check.Equal(target.Definition.Name,resolved.ToolName);
            await Check.ThrowsAsync<McpException>(()=>call.ResolveAsync(Context(ctx,"other"),envelope,CancellationToken.None).AsTask());
            ctx.Sessions.CreateSession(new SessionInfo {Id="fork"});
            ctx.Sessions.AppendMessage("fork",new ChatMessage {Role=MessageRole.Tool,Parts=[new ToolResultPart {Name="mcp_search",CallId="search",Content=disclosure.Content,Details=NetPiJson.ToNode(disclosure.Details)}]});
            await call.ResolveAsync(Context(ctx,"fork"),envelope,CancellationToken.None);
            ctx.Sessions.MarkCompacted("ses_1",msg.Seq);
            await Check.ThrowsAsync<McpException>(()=>call.ResolveAsync(context,envelope,CancellationToken.None).AsTask());
            await call.ResolveAsync(Context(ctx,"fork"),envelope,CancellationToken.None);
            var agent=new AgentInfo {Id="a",SessionId="ses_1",ToolAllowlist=["mcp_call"]};
            Check.False(ToolSelection.Eligible(ctx.Tools,agent,ctx.Sessions.GetSession("ses_1")).Contains(target));
            await manager.DisposeAsync();ctx.Unload();Check.Equal(0,ctx.Tools.All.Count);
        });
        r.Add("mcp: a cold start does not wait for a slow server, which connects in the background",async () => {
            var ctx=new FakePluginContext();
            ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=(Config("slow") with {ConnectTimeoutMs=15_000}).Json()});
            await using var manager=new ServerManager(ctx);
            var sw=Stopwatch.StartNew();
            await manager.StartAsync([],CancellationToken.None);
            Check.True(sw.ElapsedMilliseconds<2_000,$"cold start returned in {sw.ElapsedMilliseconds} ms (the fixture answers after 2500 ms)");
            Check.Equal(0,manager.Catalog().Count,"the catalog is still empty right away");
            await Connected(manager,"fixture");
            Check.Equal(1,manager.Catalog().Count,"and the tools arrive once the connection lands");
            ctx.Unload();
        });
        r.Add("mcp: a swap that loses a healthy server fails the start, so the old plugin is kept",async () => {
            var ctx=new FakePluginContext();
            ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=(Config() with {Command="netpi-no-such-command"}).Json()});
            await using var manager=new ServerManager(ctx);
            var ex=await Check.ThrowsAsync<McpException>(()=>manager.StartAsync(new() {"fixture"},CancellationToken.None));
            Check.Contains(ex.Message,"previous plugin must be kept");
            ctx.Unload();
        });
        r.Add("mcp: arguments a model wrote as text are re-read against the discovered schema",async ()=>{
            var ctx=new FakePluginContext();
            ctx.Sessions.CreateSession(new SessionInfo {Id="ses_1"});
            var counter=Path.Combine(ctx.Paths.TempDir,"typed.txt");
            var config=Config("typed") with {Args=[typeof(McpTests).Assembly.Location,"--mcp-fixture","typed",counter]};
            ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=config.Json()});
            await using var manager=new ServerManager(ctx);await manager.StartAsync([],CancellationToken.None);
            await Connected(manager,"fixture");
            var search=new McpSearchTool(ctx,manager);var call=new McpCallTool(ctx,manager);
            ctx.Tools.Register(search);ctx.Tools.Register(call);
            var tool=manager.Catalog().Single();var context=Context(ctx);
            var disclosure=await search.ExecuteAsync(context,T.Args(new {query=tool.Definition.Name,detail="schema"}),CancellationToken.None);
            ctx.Sessions.AppendMessage("ses_1",new ChatMessage {Role=MessageRole.Tool,Parts=[new ToolResultPart {Name="mcp_search",CallId="search",Content=disclosure.Content,Details=NetPiJson.ToNode(disclosure.Details)}]});
            async Task<ResolvedToolCall> Resolve(object arguments)=>await call.ResolveAsync(context,
                T.Args(new {id=tool.Definition.Name,revision=tool.Definition.Revision,arguments}),CancellationToken.None);

            // What a model writes when the remote schema only arrived after the call envelope was built.
            var resolved=await Resolve(new {list_only="true",timeout="10",views="[{\"title\":\"Probe\"}]",config="{\"patch\":[\"1\",2]}",script="{\"not\":\"parsed\"}"});
            var sent=NetPiJson.ToNode(resolved.Arguments)!.ToJsonString();
            Check.Contains(sent,"\"list_only\":true");Check.Contains(sent,"\"timeout\":10");
            Check.Contains(sent,"\"views\":[{\"title\":\"Probe\"}]");Check.Contains(sent,"\"patch\":[1,2]");
            // A string-typed argument is never re-read, even when it holds JSON.
            Check.Equal("""{"not":"parsed"}""",NetPiJson.ToNode(resolved.Arguments)!["script"]!.GetValue<string>());
            // A list wrapped in an object crosses whole, at every level, with the scalars inside it repaired too.
            var wrapped=await Resolve(new {views=new {item=new {title="Probe",max_columns="1"}}});
            Check.Equal("""{"views":[{"title":"Probe","max_columns":1}]}""",NetPiJson.ToNode(wrapped.Arguments)!.ToJsonString());
            var wrappedCall=await manager.CallAsync(tool,wrapped.Arguments,CancellationToken.None);
            Check.Contains(wrappedCall.Content,"\"views\":[{\"title\":\"Probe\",\"max_columns\":1}]");
            // The server receives the repaired types, not the text the model wrote.
            var result=await manager.CallAsync(tool,resolved.Arguments,CancellationToken.None);
            Check.Contains(result.Content,"\"list_only\":true");
            var received=File.ReadAllLines(counter);
            Check.Equal("""{"views":[{"title":"Probe","max_columns":1}]}""",JsonNode.Parse(received[0])!.ToJsonString());
            Check.Equal(sent,JsonNode.Parse(received[1])!.ToJsonString());
            // Arguments that already match the schema are never rewritten, not even a number that looks like text.
            Check.Equal("""{"list_only":true,"timeout":10,"script":"10"}""",NetPiJson.ToNode((await Resolve(new {list_only=true,timeout=10,script="10"})).Arguments)!.ToJsonString());
            // A value no schema can repair keeps failing, and now says what it wanted.
            var bad=await Check.ThrowsAsync<McpException>(()=>Resolve(new {timeout="soon"}));
            Check.Contains(bad.Message,"integer");Check.Contains(bad.Message,"string");
            await manager.DisposeAsync();ctx.Unload();
        });
        r.Add("mcp: a list the model wrapped in an object is unwrapped only where the schema allows nothing else",()=>{
            var schema=JsonNode.Parse(
                """
                {"type":"object","$defs":{"card":{"type":"object","properties":{"type":{"type":"string"},"max_columns":{"type":"integer"}}}},
                 "properties":{
                   "views":{"type":"array","items":{"$ref":"#/$defs/card"}},
                   "cards":{"type":"array","items":{"$ref":"#/$defs/card"}},
                   "loose":{"type":["array","object"],"items":{"type":"integer"}},
                   "meta":{"type":"object","properties":{"item":{"type":"string"}}},
                   "grid":{"type":"array","items":{"type":"array","items":{"type":"integer"}}}}}
                """)!.AsObject();
            // The shape ha-mcp received: a one-property object where the schema says array, at two nesting levels.
            var args=JsonNode.Parse("""{"views":{"item":{"type":"sections","max_columns":"1"}},"cards":{"item":[{"type":"heading"},{"type":"entities"}]}}""")!.AsObject();
            var repaired=Coercion.Repair(schema,args);
            Check.Equal("""{"views":[{"type":"sections","max_columns":1}],"cards":[{"type":"heading"},{"type":"entities"}]}""",repaired!.ToJsonString());
            // A list of lists keeps its nesting: the wrapper goes, the inner array stays an element.
            Check.Equal("""{"grid":[[1,2],[3]]}""",Coercion.Repair(schema,JsonNode.Parse("""{"grid":{"item":[[1,2],[3]]}}""")!.AsObject())!.ToJsonString());
            // Left alone: an object where an object is allowed, a schema that permits either shape, and an
            // ambiguous two-property wrapper.
            Check.Equal(null,Coercion.Repair(schema,JsonNode.Parse("""{"meta":{"item":"kept"}}""")!.AsObject()));
            Check.Equal(null,Coercion.Repair(schema,JsonNode.Parse("""{"loose":{"item":1}}""")!.AsObject()));
            Check.Equal(null,Coercion.Repair(schema,JsonNode.Parse("""{"views":{"item":{"type":"a"},"other":1}}""")!.AsObject()));
            Check.Equal(null,Coercion.Repair(schema,JsonNode.Parse("""{"views":{"element":{"type":"a"}}}""")!.AsObject()));
        });
        r.Add("mcp: a wrapped list is unwrapped under an anyOf schema, however many times it was wrapped",()=>{
            // ha_list_floors_areas: fields is anyOf[string, array of string, null].
            var schema=JsonNode.Parse(
                """
                {"type":"object","additionalProperties":false,"properties":{
                  "fields":{"anyOf":[{"type":"string"},{"items":{"type":"string"},"type":"array"},{"type":"null"}],"default":null,"description":"x"},
                  "views":{"anyOf":[{"type":"array","items":{"$ref":"#/$defs/card"}},{"type":"null"}]},
                  "meta":{"type":"object","properties":{"item":{"type":"string"}}}},
                 "$defs":{"card":{"type":"object","properties":{"title":{"type":"string"}}}}}
                """)!.AsObject();
            // The shape a value takes inside mcp_call's own envelope: wrapped once, and wrapped twice.
            foreach(var wrapped in new[]{"{\"item\":[\"success\"]}","{\"item\":{\"item\":[\"success\"]}}"})
            {
                var args=JsonNode.Parse("{\"fields\":"+wrapped+"}")!.AsObject();
                var repaired=Coercion.Repair(schema,args);
                Check.Equal("""{"fields":["success"]}""",repaired?.ToJsonString()??"NULL");
                Schema.Validate(schema,repaired!);
            }
            // A wrapped object becomes a one-element list, again as deep as the wrappers go.
            Check.Equal("""{"views":[{"title":"Probe"}]}""",Coercion.Repair(schema,
                JsonNode.Parse("""{"views":{"item":{"item":{"item":{"title":"Probe"}}}}}""")!.AsObject())!.ToJsonString());
            // Still untouched: an object where the schema says object, and a wrapper chain that never reaches a value.
            Check.Equal(null,Coercion.Repair(schema,JsonNode.Parse("""{"meta":{"item":{"item":"kept"}}}""")!.AsObject()));
            Check.Equal(null,Coercion.Repair(schema,JsonNode.Parse("""{"views":{"item":"kept","other":1}}""")!.AsObject()));
            // The outer wrapper goes because the schema says array; what it held is the element, ambiguity and all.
            Check.Equal("""{"views":[{"item":{"title":"x"},"other":1}]}""",Coercion.Repair(schema,
                JsonNode.Parse("""{"views":{"item":{"item":{"title":"x"},"other":1}}}""")!.AsObject())!.ToJsonString());
        });
        r.Add("mcp: a number NetPI wrote itself validates as a number, and a type error names what it wanted",()=>{
            var schema=JsonNode.Parse("""{"type":"object","properties":{"n":{"type":"integer"},"x":{"type":"number","maximum":10}}}""")!.AsObject();
            Schema.Validate(schema,new JsonObject {["n"]=JsonValue.Create(7L),["x"]=JsonValue.Create(2.5d)});
            Check.Throws<McpException>(()=>Schema.Validate(schema,new JsonObject {["n"]=JsonValue.Create(7L),["x"]=JsonValue.Create(20d)}));
            Check.Contains(Check.Throws<McpException>(()=>Schema.Validate(schema,new JsonObject {["n"]="7"})).Message,
                "$.n: expected integer, got string");
        });
        r.Add("mcp: HTTP modern metadata headers pagination auth rejection and no replay", async () => {
            int calls=0; string? header=null;
            await using var web=await LocalWeb.StartAsync(app=> app.Run(async http => {
                if(http.Request.Method!="POST"){http.Response.StatusCode=405;return;}
                var request=(await JsonNode.ParseAsync(http.Request.Body))!.AsObject();
                var method=request["method"]!.GetValue<string>(); var p=request["params"]!;
                if(p["_meta"]?["io.modelcontextprotocol/protocolVersion"]?.GetValue<string>()!="2026-07-28") throw new Exception("metadata missing");
                JsonObject result;
                if(method=="server/discover")result=new JsonObject {["supportedVersions"]=new JsonArray("2026-07-28"),["capabilities"]=new JsonObject {["tools"]=new JsonObject()}};
                else if(method=="tools/list")result=new JsonObject {["tools"]=new JsonArray(Tool())};
                else { calls++; header=http.Request.Headers["Mcp-Param-City"]; result=new JsonObject {["content"]=new JsonArray(new JsonObject {["type"]="text",["text"]="ok"})};}
                await http.Response.WriteAsJsonAsync(new JsonObject {["jsonrpc"]="2.0",["id"]=request["id"]!.DeepClone(),["result"]=result});
            }));
            var config=Config() with {Transport="http",Url=web.Url};
            await using var c=new McpConnection(config,100000,_=>{});
            await c.InitializeAsync(CancellationToken.None); await c.ListAsync(10,100000,CancellationToken.None);
            var schema=Tool()["inputSchema"]!.AsObject();schema["properties"]!["city"]!["x-mcp-header"]="City";
            await c.CallAsync("weather",T.Args(new {city="Göteborg"}),schema,CancellationToken.None);
            Check.Equal(1,calls);Check.Contains(header,"=?base64?");
            await using var auth=await LocalWeb.StartAsync(app=>app.Run(http=>{http.Response.StatusCode=401;return Task.CompletedTask;}));
            await using var rejected=new McpConnection(config with {Url=auth.Url},100000,_=>{});
            await Check.ThrowsAsync<McpHttpException>(()=>rejected.InitializeAsync(CancellationToken.None));
        });
    }

    /// <summary>An embedder that knows two synonyms: "forecast" and "rainy" are weather (a bag of words otherwise).</summary>
    private sealed class Synonyms : IEmbeddingService
    {
        public string? Model => "synonyms";
        public bool Available => true;
        public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct) =>
            Task.FromResult(new EmbeddingResult("synonyms", 64, request.Texts
                .Select(t => EmbeddingsTests.BagOfWords.Vector(t.ToLowerInvariant().Replace("forecast", "weather").Replace("rainy", "weather"))).ToList()));
    }
}
