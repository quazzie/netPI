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
        r.Add("mcp: local schema validates refs combinations constraints and rejects unsupported assertions", () => {
            var schema=JsonNode.Parse("""{"type":"object","$defs":{"city":{"type":"string","minLength":2}},"properties":{"city":{"$ref":"#/$defs/city"},"n":{"type":"integer","minimum":1}},"required":["city"],"additionalProperties":false}""")!.AsObject();
            Schema.Check(schema); Schema.Validate(schema,JsonNode.Parse("""{"city":"Oslo","n":2}"""));
            Check.Throws<McpException>(()=>Schema.Validate(schema,JsonNode.Parse("""{"city":"x"}""")));
            Check.Throws<McpException>(()=>Schema.Validate(schema,JsonNode.Parse("""{"city":"Oslo","extra":1}""")));
            Check.Throws<McpException>(()=>Schema.Check(JsonNode.Parse("""{"type":"object","unevaluatedProperties":false}""")!.AsObject()));
            Check.Throws<McpException>(()=>Schema.Check(JsonNode.Parse("""{"type":"object","properties":{"x":{"$ref":"https://example.com"}}}""")!.AsObject()));
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
        r.Add("mcp: 250 tools keep visible schema prefix constant and disclosure scoped to retained chat", async () => {
            var ctx=new FakePluginContext();
            ctx.Sessions.CreateSession(new SessionInfo {Id="ses_1"});
            ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=Config("large").Json()});
            await using var manager=new ServerManager(ctx); await manager.StartAsync([],CancellationToken.None);
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
}
