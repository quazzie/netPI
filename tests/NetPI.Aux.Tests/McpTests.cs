using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
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
        r.Add("mcp: env and header values resolve live: the user and machine registry, then the process environment, so a secret set after start needs no restart", () => {
            // Not in the registry: the process environment is the fallback, and a set in this process is visible to it.
            Environment.SetEnvironmentVariable("NETPI_MCP_ENV_PROCESS", "from-process");
            try
            {
                Check.Equal("from-process", EnvSource.Resolve("NETPI_MCP_ENV_PROCESS"));
                var c = ServerConfig.Parse("live", new JsonObject { ["command"]="dotnet", ["env"]=new JsonObject { ["A"]="NETPI_MCP_ENV_PROCESS" } }, Path.GetTempPath());
                Check.Equal("from-process", c.Secret("NETPI_MCP_ENV_PROCESS"));
                Check.Equal("a [redacted] b", c.Redact("a from-process b"));
            }
            finally { Environment.SetEnvironmentVariable("NETPI_MCP_ENV_PROCESS", null); }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { Check.Skip("the registry fallback is a Windows thing"); return; }
            const string name = "NETPI_MCP_ENV_REGISTRY";
            RegistryValue.Delete(name); // a stale one from an earlier run must not skew the test
            Check.True(EnvSource.Resolve(name) is null, "nothing set yet");
            RegistryValue.Set(name, "from-registry");
            try
            {
                Check.Equal("from-registry", EnvSource.Resolve(name), "the live registry is read, not the start-time snapshot");
                var http = ServerConfig.Parse("live", new JsonObject { ["transport"]="http", ["url"]="https://example.com/mcp", ["headerEnv"]=new JsonObject { ["Authorization"]=name } }, Path.GetTempPath());
                Check.Equal("from-registry", http.Secret(name), "a header's source resolves without a restart");
            }
            finally { RegistryValue.Delete(name); }
            Check.True(EnvSource.Resolve("NETPI_MCP_ENV_NO_SUCH_VARIABLE_XYZ") is null, "an unset variable is null");
        });
        r.Add("mcp: a token in a URL or an argument is found by its shape; words, versions and model names are not (SecretText)", () => {
            Check.True(SecretText.TokenShaped("private_zctpwlX7ZkIAr7oqdfLF88"), "ha-mcp's private path");
            Check.True(SecretText.TokenShaped("private_zctpwl-7ZkIAr7oq_dfLF88"), "split by separators the token may contain");
            Check.True(SecretText.TokenShaped("550e8400-e29b-41d4-a716-446655440000"), "a UUID");
            Check.True(SecretText.TokenShaped("3f9a2b7c4d5e6f708192a3b4c5d6e7f8"), "hex");
            Check.True(SecretText.TokenShaped("ZkqWpLmTrBvHnXcDsQeRfTgY"), "base64 letters without a digit");
            foreach (var plain in new[] { "server-filesystem", "claude-sonnet-4-5-20250929", "homeassistant", "McpServerIntegration", "v1", "node-v20.11.1-win-x64", "C:\\tools\\server.js" })
                Check.False(SecretText.TokenShaped(plain), plain);
            Check.Equal("https://ha.example:8443/<secret, 30 chars>?token=<secret, 11 chars>",
                SecretText.MaskUrl("https://ha.example:8443/private_zctpwlX7ZkIAr7oqdfLF88?token=abc12345XYZ"));
            Check.Equal("https://<secret, 4 chars>@ha.example/mcp", SecretText.MaskUrl("https://user@ha.example/mcp"));
            Check.Equal(null, SecretText.MaskUrl("http://homeassistant.local:8123/mcp"), "nothing to mask: the URL stays as written");
            Check.Equal(null, SecretText.MaskUrl("not a url"));
            var error = "POST https://ha.example:8443/private_zctpwlX7ZkIAr7oqdfLF88 failed: 404";
            Check.Equal("POST https://ha.example:8443/<secret, 30 chars> failed: 404",
                SecretText.Without(error, SecretText.UrlSecrets("https://ha.example:8443/private_zctpwlX7ZkIAr7oqdfLF88")));
        });
        r.Add("mcp: mcp.list masks a secret path, query value or token argument; mcp.config gives the editor the whole configuration", async () => {
            var ctx=new FakePluginContext();
            const string secretPath="private_zctpwlX7ZkIAr7oqdfLF88";
            var url=$"http://127.0.0.1:9/{secretPath}?token=abc12345XYZ";
            ctx.Settings.Set("mcp.servers",new JsonObject {
                ["ha"]=new JsonObject {["transport"]="http",["url"]=url,["enabled"]=false},
                ["cli"]=new JsonObject {["command"]="netpi-no-such-command",["enabled"]=false,["cwd"]=Path.GetTempPath(),
                    ["args"]=new JsonArray("--api-key","sk-live-0123456789abcdefXYZ","--token=abcDEF1234567890xyz","server-filesystem","claude-sonnet-4-5-20250929")},
            });
            var plugin=new McpPlugin();
            await plugin.StartAsync(ctx,CancellationToken.None);
            try
            {
                Check.True(ctx.RpcFake.ReadOnly["mcp.list"]); Check.False(ctx.RpcFake.ReadOnly["mcp.config"]);
                var list=(await ctx.RpcFake.CallAsync("mcp.list"))!;
                var text=list.ToJsonString();
                foreach (var secret in new[] { secretPath, "abc12345XYZ", "sk-live-0123456789abcdefXYZ", "abcDEF1234567890xyz" })
                    Check.NotContains(text,secret,"mcp.list without --write hides "+secret);
                JsonObject Server(string id)=>((JsonArray)list["servers"]!).OfType<JsonObject>().Single(s=>(string?)s["id"]==id);
                Check.Equal("http://127.0.0.1:9/<secret, 30 chars>?token=<secret, 11 chars>",(string?)Server("ha")["config"]!["url"]);
                Check.Equal(true,(bool?)Server("ha")["secretsMasked"]);
                Check.Equal("--api-key|<secret, 27 chars>|--token=<secret, 19 chars>|server-filesystem|claude-sonnet-4-5-20250929",
                    string.Join("|",((JsonArray)Server("cli")["config"]!["args"]!).Select(a=>(string?)a)),"the flag's value is masked, the names around it stay readable");
                // the editor's read: the configuration whole, so a save round-trips it unchanged
                var whole=(await ctx.RpcFake.CallAsync("mcp.config",new {id="ha"}))!;
                Check.Equal(url,(string?)whole["config"]!["url"]);
                await ctx.RpcFake.CallAsync("mcp.save",new {id="ha",config=whole["config"]});
                Check.Equal(url,(string?)ctx.Settings.GetNode("mcp.servers")!["ha"]!["url"],"the saved configuration keeps its secret path");
                await Check.ThrowsAsync<RpcException>(()=>ctx.RpcFake.CallAsync("mcp.config",new {id="nope"}));
            }
            finally { await plugin.StopAsync(CancellationToken.None); ctx.Unload(); }
        });
        r.Add("mcp: mcp.read runs a read-only tool and refuses one that may write, mcp.call runs it with typed arguments intact; both are tool calls diag lists", async () => {
            var ctx=new FakePluginContext();
            var counter=Path.Combine(ctx.Paths.TempDir,"rpc-typed.txt");
            ctx.Settings.Set("mcp.servers",new JsonObject {
                ["ro"]=(Config() with {Id="ro",ReadOnly=["weather"]}).Json(),
                ["rw"]=(Config("typed") with {Id="rw",Args=[typeof(McpTests).Assembly.Location,"--mcp-fixture","typed",counter]}).Json(),
            });
            var plugin=new McpPlugin();
            await plugin.StartAsync(ctx,CancellationToken.None);
            try
            {
                async Task Up(string id)
                {
                    var sw=Stopwatch.StartNew();
                    while (true)
                    {
                        var s=((JsonArray)(await ctx.RpcFake.CallAsync("mcp.list"))!["servers"]!).OfType<JsonObject>().FirstOrDefault(x=>(string?)x["id"]==id);
                        if ((string?)s?["status"]=="connected") return;
                        if (sw.ElapsedMilliseconds>15_000) throw new AssertException($"MCP server {id} did not connect: {s?.ToJsonString()}");
                        await Task.Delay(50);
                    }
                }
                await Up("ro"); await Up("rw");
                Check.True(ctx.RpcFake.ReadOnly["mcp.read"],"mcp.read only reads"); Check.False(ctx.RpcFake.ReadOnly["mcp.call"],"mcp.call needs --write");

                var read=(await ctx.RpcFake.CallAsync("mcp.read",new {id="ro/weather",arguments=new {city="Oslo"}}))!;
                Check.Equal("Weather: Oslo",(string?)read["content"]); Check.Equal(false,(bool?)read["isError"]); Check.Equal("ro",(string?)read["serverId"]);
                var refused=await Check.ThrowsAsync<RpcException>(()=>ctx.RpcFake.CallAsync("mcp.read",new {serverId="rw",name="weather",arguments=new {list_only=true}}));
                Check.Equal("forbidden",refused.Code); Check.Contains(refused.Message,"mcp.call");
                Check.Equal(0,File.Exists(counter) ? File.ReadAllLines(counter).Length : 0,"a refused call never reached the server");

                // typed arguments (a list of objects, an integer, a boolean, an object) arrive as they were written
                var call=(await ctx.RpcFake.CallAsync("mcp.call",new {id="rw/weather",arguments=new {list_only=true,timeout=10,views=new[] {new {title="Probe",max_columns=2}},config=new {patch=new[] {1,2}}}}))!;
                Check.Equal(false,(bool?)call["isError"]);
                Check.Equal("""{"list_only":true,"timeout":10,"views":[{"title":"Probe","max_columns":2}],"config":{"patch":[1,2]}}""",
                    JsonNode.Parse(File.ReadAllLines(counter)[0])!.ToJsonString(),"what the server received");
                // the model's own repair applies, and a schema mismatch it cannot repair is a bad request
                await ctx.RpcFake.CallAsync("mcp.call",new {id="rw/weather",arguments=new {timeout="12"}});
                Check.Equal("""{"timeout":12}""",JsonNode.Parse(File.ReadAllLines(counter)[1])!.ToJsonString());
                Check.Equal("bad_request",(await Check.ThrowsAsync<RpcException>(()=>ctx.RpcFake.CallAsync("mcp.call",new {id="rw/weather",arguments=new {timeout="soon"}}))).Code);
                Check.Equal("bad_request",(await Check.ThrowsAsync<RpcException>(()=>ctx.RpcFake.CallAsync("mcp.read",new {id="ro/weather",arguments=new {}}))).Code,"the required city is missing");
                Check.Equal("not_found",(await Check.ThrowsAsync<RpcException>(()=>ctx.RpcFake.CallAsync("mcp.read",new {id="ro/nope"}))).Code);
                Check.Equal("conflict",(await Check.ThrowsAsync<RpcException>(()=>ctx.RpcFake.CallAsync("mcp.read",new {id="ro/weather",revision="old",arguments=new {city="x"}}))).Code);

                // recorded like a model's call of the tool, without a chat: tool.start / tool.end with source "rpc"
                var starts=ctx.Bus.OfType(EventTypes.ToolStart).Select(e=>NetPiJson.ToNode(e.Data)!.AsObject()).ToList();
                var ends=ctx.Bus.OfType(EventTypes.ToolEnd).Select(e=>NetPiJson.ToNode(e.Data)!.AsObject()).ToList();
                Check.Equal(3,starts.Count,"the three calls that reached a server"); Check.Equal(3,ends.Count);
                Check.True(starts.All(s=>(string?)s["source"]=="rpc" && s["sessionId"] is null));
                Check.Equal((string?)read["id"],(string?)starts[0]["name"]); Check.Equal("ro",(string?)starts[0]["serverId"]);
                Check.Contains((string?)starts[0]["arguments"] ?? "","Oslo");
                Check.True(ends.All(e=>(bool?)e["isError"]==false && e["durationMs"] is not null));
                Check.Equal(string.Join(",",starts.Select(s=>(string?)s["callId"])),string.Join(",",ends.Select(e=>(string?)e["callId"])));
            }
            finally { await plugin.StopAsync(CancellationToken.None); ctx.Unload(); }
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
        r.Add("mcp: a server's ping is answered with an empty result, not a method-not-found error", async () => {
            await using var c=new McpConnection(Config("ping"),100000,_=>{});
            using var timeout=new CancellationTokenSource(10000);
            await c.InitializeAsync(timeout.Token);
            var schema=Tool()["inputSchema"]!.AsObject();
            var result=await c.CallAsync("weather",T.Args(new {city="Oslo"}),schema,timeout.Token);
            var text=result["content"]![0]!["text"]!.GetValue<string>();
            Check.True(text.StartsWith("pong: ",StringComparison.Ordinal),"the server saw a reply: "+text);
            var answer=JsonNode.Parse(text["pong: ".Length..])!.AsObject();
            Check.Equal("srv-ping",answer["id"]!.GetValue<string>());
            Check.True(answer["result"] is JsonObject {Count:0},"an empty result object: "+answer.ToJsonString());
            Check.True(answer["error"] is null,"no error");
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
        r.Add("mcp: 250 remote tools are registered deferred: a chat is sent none of them until it loads one, then only that one", async () => {
            var ctx=new FakePluginContext();
            ctx.Sessions.CreateSession(new SessionInfo {Id="ses_1"});
            ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=Config("large").Json()});
            await using var manager=new ServerManager(ctx); await manager.StartAsync([],CancellationToken.None);
            await Connected(manager,"fixture");
            Check.Equal(250,manager.Catalog().Count);
            string Visible(AgentInfo? agent=null)
            {
                var session=ctx.Sessions.GetSession("ses_1");
                var eligible=ToolSelection.Eligible(ctx.Tools,agent,session,ctx.Settings);
                return string.Join(",",ToolSelection.Visible(eligible,ToolExposure.For(ctx.Settings,agent,session)).Select(t=>t.Name));
            }
            Check.Equal("",Visible(),"every remote tool is deferred");
            var target=manager.Catalog().Single(t=>t.RemoteName=="weather");
            Check.NotContains(target.Definition.Description,"UNTRUSTED_SERVER");
            // an agent's tool list grants only what it names
            Check.Equal("",Visible(new AgentInfo {Id="a",SessionId="ses_1",ToolAllowlist=["read"]}));
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
        r.Add("mcp: arguments a model wrote as text are re-read against the tool's schema",async ()=>{
            var ctx=new FakePluginContext();
            ctx.Sessions.CreateSession(new SessionInfo {Id="ses_1"});
            var counter=Path.Combine(ctx.Paths.TempDir,"typed.txt");
            var config=Config("typed") with {Args=[typeof(McpTests).Assembly.Location,"--mcp-fixture","typed",counter]};
            ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=config.Json()});
            await using var manager=new ServerManager(ctx);await manager.StartAsync([],CancellationToken.None);
            await Connected(manager,"fixture");
            var tool=manager.Catalog().Single();
            JsonElement Prepare(object arguments)=>manager.Prepare(tool,JsonNode.Parse(T.Args(arguments).GetRawText())!.AsObject());

            // What a model writes when it sends JSON as text inside typed arguments.
            var prepared=Prepare(new {list_only="true",timeout="10",views="[{\"title\":\"Probe\"}]",config="{\"patch\":[\"1\",2]}",script="{\"not\":\"parsed\"}"});
            var sent=NetPiJson.ToNode(prepared)!.ToJsonString();
            Check.Contains(sent,"\"list_only\":true");Check.Contains(sent,"\"timeout\":10");
            Check.Contains(sent,"\"views\":[{\"title\":\"Probe\"}]");Check.Contains(sent,"\"patch\":[1,2]");
            // A string-typed argument is never re-read, even when it holds JSON.
            Check.Equal("""{"not":"parsed"}""",NetPiJson.ToNode(prepared)!["script"]!.GetValue<string>());
            // A list wrapped in an object crosses whole, at every level, with the scalars inside it repaired too: the
            // model's own call (the tool run directly) goes through the same repair.
            var wrappedCall=await tool.ExecuteAsync(Context(ctx),T.Args(new {views=new {item=new {title="Probe",max_columns="1"}}}),CancellationToken.None);
            Check.Contains(wrappedCall.Content,"\"views\":[{\"title\":\"Probe\",\"max_columns\":1}]");
            // The server receives the repaired types, not the text the model wrote.
            var result=await manager.CallAsync(tool,prepared,CancellationToken.None);
            Check.Contains(result.Content,"\"list_only\":true");
            var received=File.ReadAllLines(counter);
            Check.Equal("""{"views":[{"title":"Probe","max_columns":1}]}""",JsonNode.Parse(received[0])!.ToJsonString());
            Check.Equal(sent,JsonNode.Parse(received[1])!.ToJsonString());
            // Arguments that already match the schema are never rewritten, not even a number that looks like text.
            Check.Equal("""{"list_only":true,"timeout":10,"script":"10"}""",NetPiJson.ToNode(Prepare(new {list_only=true,timeout=10,script="10"}))!.ToJsonString());
            // A value no schema can repair keeps failing, and now says what it wanted.
            var bad=Check.Throws<McpException>(()=>Prepare(new {timeout="soon"}));
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
            // The shape a value takes when a model wraps it: once, and twice.
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
        r.Add("mcp: HTTP modern metadata, parameter headers, auth rejection and no replay", async () => {
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
