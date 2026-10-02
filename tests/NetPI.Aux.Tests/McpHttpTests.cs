using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NetPI.Mcp;
namespace NetPI.Aux.Tests;

public static class McpHttpTests
{
    private static JsonObject Result(JsonObject request, JsonObject result) => new() {["jsonrpc"]="2.0",["id"]=request["id"]!.DeepClone(),["result"]=result};
    private static JsonObject Discover() => new() {["supportedVersions"]=new JsonArray(Protocol.Modern),["capabilities"]=new JsonObject {["tools"]=new JsonObject {["listChanged"]=true}}};
    private static ServerConfig Config(string url)=>new(){Id="http",Transport="http",Url=url};
    public static void Register(TestRunner r)
    {
        r.Add("mcp http: legacy caller cancellation sends the protocol signal without replay",async ()=>{
            var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var signalled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls=0;
            await using var web=await LocalWeb.StartAsync(app=>app.Run(async h=>{
                var request=(await JsonNode.ParseAsync(h.Request.Body))!.AsObject();
                var method=request["method"]!.GetValue<string>();
                if(method=="server/discover"){await h.Response.WriteAsJsonAsync(new JsonObject {["jsonrpc"]="2.0",["id"]=request["id"]!.DeepClone(),["error"]=new JsonObject {["code"]=-32601,["message"]="unknown"}});return;}
                if(method=="initialize"){await h.Response.WriteAsJsonAsync(Result(request,new JsonObject {["protocolVersion"]=Protocol.Legacy,["capabilities"]=new JsonObject {["tools"]=new JsonObject()}}));return;}
                if(method=="notifications/cancelled"){signalled.TrySetResult();h.Response.StatusCode=202;return;}
                if(method=="notifications/initialized"){h.Response.StatusCode=202;return;}
                calls++;started.TrySetResult();
                try {await Task.Delay(Timeout.Infinite,h.RequestAborted);}catch(OperationCanceledException){}
            }));
            await using var c=new McpConnection(Config(web.Url),100000,_=>{});
            await c.InitializeAsync(CancellationToken.None);
            using var cancel=new CancellationTokenSource();
            var call=c.CallAsync("weather",T.Args(new {city="Oslo"}),McpTests.Tool()["inputSchema"]!.AsObject(),cancel.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(()=>call);
            await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));Check.Equal(1,calls);
        });
        r.Add("mcp http: a discovery probe that outlasts its window falls back to initialize",async ()=>{
            int probes=0,initialized=0;
            await using var web=await LocalWeb.StartAsync(app=>app.Run(async h=>{
                var request=(await JsonNode.ParseAsync(h.Request.Body))!.AsObject();
                var method=request["method"]!.GetValue<string>();
                if(method=="server/discover"){probes++;try{await Task.Delay(TimeSpan.FromSeconds(3),h.RequestAborted);}catch(OperationCanceledException){}return;}
                if(method=="initialize"){initialized++;await h.Response.WriteAsJsonAsync(Result(request,new JsonObject {["protocolVersion"]=Protocol.Legacy,["capabilities"]=new JsonObject {["tools"]=new JsonObject()}}));return;}
                if(method=="notifications/initialized"){h.Response.StatusCode=202;return;}
                h.Response.ContentType="text/event-stream";
                await h.Response.WriteAsync(": keepalive\n\ndata: "+Result(request,new JsonObject {["content"]=new JsonArray(new JsonObject {["type"]="text",["text"]="legacy OK"})}).ToJsonString()+"\n\n");
            }));
            await using var c=new McpConnection(Config(web.Url),100000,_=>{});
            await c.InitializeAsync(CancellationToken.None);
            var result=await c.CallAsync("weather",T.Args(new {city="Oslo"}),McpTests.Tool()["inputSchema"]!.AsObject(),CancellationToken.None);
            Check.Contains(result.ToJsonString(),"legacy OK");Check.Equal(1,probes);Check.Equal(1,initialized);Check.False(c.Modern);
        });
        r.Add("mcp http: SSE response subscription correlation acknowledgement and cancellation",async ()=>{
            var listened=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var web=await LocalWeb.StartAsync(app=>app.Run(async h=>{
                var request=(await JsonNode.ParseAsync(h.Request.Body))!.AsObject();
                var method=request["method"]!.GetValue<string>();
                Check.Equal(Protocol.Modern,h.Request.Headers["MCP-Protocol-Version"].ToString());
                Check.Equal(method,h.Request.Headers["Mcp-Method"].ToString());
                if(method=="server/discover"){await h.Response.WriteAsJsonAsync(Result(request,Discover()));return;}
                h.Response.ContentType="text/event-stream";
                if(method=="subscriptions/listen")
                {
                    Check.True(request["params"]?["notifications"]?["toolsListChanged"]?.GetValue<bool>()==true);
                    JsonObject Notice(string name,JsonNode id)=>new(){["jsonrpc"]="2.0",["method"]=name,["params"]=new JsonObject {["notifications"]=new JsonObject {["toolsListChanged"]=true},["_meta"]=new JsonObject {[Protocol.MetaPrefix+"subscriptionId"]=id.DeepClone()}}};
                    await h.Response.WriteAsync("data: "+Notice("notifications/subscriptions/acknowledged",request["id"]!).ToJsonString()+"\n\n");
                    await h.Response.WriteAsync("data: "+Notice("notifications/tools/list_changed",JsonValue.Create(9999)!).ToJsonString()+"\n\n");
                    await h.Response.WriteAsync("data: "+Notice("notifications/tools/list_changed",request["id"]!).ToJsonString()+"\n\n");
                    await h.Response.Body.FlushAsync();listened.TrySetResult();
                    try{await Task.Delay(Timeout.Infinite,h.RequestAborted);}catch(OperationCanceledException){}
                }
                else await h.Response.WriteAsync(": keepalive\n\ndata: "+Result(request,new JsonObject {["content"]=new JsonArray(new JsonObject {["type"]="text",["text"]="SSE OK"})}).ToJsonString()+"\n\n");
            }));
            await using var c=new McpConnection(Config(web.Url),100000,_=>{});
            await c.InitializeAsync(CancellationToken.None);
            var result=await c.CallAsync("weather",T.Args(new {city="Oslo"}),McpTests.Tool()["inputSchema"]!.AsObject(),CancellationToken.None);
            Check.Contains(result.ToJsonString(),"SSE OK");
            int notices=0;var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            c.Transport.Notification+=_=>{Interlocked.Increment(ref notices);received.TrySetResult();};
            using var cancellation=new CancellationTokenSource();
            var listener=c.Transport.ListenAsync(true,cancellation.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));await listened.Task;
            cancellation.Cancel();await Check.ThrowsAsync<OperationCanceledException>(()=>listener);
            Check.Equal(1,notices);
        });
        r.Add("mcp http: legacy initialization session headers loss and delete without replay",async ()=>{
            int calls=0,initialized=0;bool deleted=false;
            await using var web=await LocalWeb.StartAsync(app=>app.Run(async h=>{
                if(h.Request.Method=="DELETE"){Check.Equal("owned-session",h.Request.Headers["Mcp-Session-Id"].ToString());deleted=true;h.Response.StatusCode=204;return;}
                var request=(await JsonNode.ParseAsync(h.Request.Body))!.AsObject();
                var method=request["method"]!.GetValue<string>();
                if(method=="server/discover"){await h.Response.WriteAsJsonAsync(new JsonObject {["jsonrpc"]="2.0",["id"]=request["id"]!.DeepClone(),["error"]=new JsonObject {["code"]=-32601,["message"]="unknown"}});return;}
                Check.Equal(Protocol.Legacy,h.Request.Headers["MCP-Protocol-Version"].ToString());
                if(method=="initialize")
                {
                    initialized++;h.Response.Headers["Mcp-Session-Id"]="owned-session";
                    await h.Response.WriteAsJsonAsync(Result(request,new JsonObject {["protocolVersion"]=Protocol.Legacy,["capabilities"]=new JsonObject {["tools"]=new JsonObject()}}));return;
                }
                Check.Equal("owned-session",h.Request.Headers["Mcp-Session-Id"].ToString());
                if(method=="notifications/initialized"){h.Response.StatusCode=202;return;}
                calls++;h.Response.StatusCode=404;
            }));
            var c=new McpConnection(Config(web.Url),100000,_=>{});
            await c.InitializeAsync(CancellationToken.None);
            Check.False(c.Modern);Check.Equal(1,initialized);
            await Check.ThrowsAsync<McpHttpException>(()=>c.CallAsync("weather",T.Args(new {city="Oslo"}),McpTests.Tool()["inputSchema"]!.AsObject(),CancellationToken.None));
            Check.Equal(1,calls);Check.Equal(1,initialized);
            await c.DisposeAsync();Check.True(deleted);
        });
        r.Add("mcp http: modern version error and input_required are explicit and do not downgrade or replay",async ()=>{
            int requests=0;
            await using var web=await LocalWeb.StartAsync(app=>app.Run(async h=>{
                requests++;var request=(await JsonNode.ParseAsync(h.Request.Body))!.AsObject();
                await h.Response.WriteAsJsonAsync(new JsonObject {["jsonrpc"]="2.0",["id"]=request["id"]!.DeepClone(),["error"]=new JsonObject {["code"]=-32022,["message"]="unsupported modern revision"}});
            }));
            await using var c=new McpConnection(Config(web.Url),100000,_=>{});
            await Check.ThrowsAsync<McpException>(()=>c.InitializeAsync(CancellationToken.None));Check.Equal(1,requests);
            Check.Throws<McpException>(()=>Protocol.Result(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"result":{"resultType":"input_required"}}""")!.AsObject()));
            Check.Throws<McpException>(()=>Schema.Check(JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"object","x-mcp-header":"X"}}}""")!.AsObject(),true));
            Check.Throws<McpException>(()=>Schema.Check(JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"string","x-mcp-header":"X"},"y":{"type":"string","x-mcp-header":"x"}}}""")!.AsObject(),true));
        });
        r.Add("mcp http: complete pagination and partial failed refresh retain known tools",async ()=>{
            bool fail=false;int pages=0;
            await using var web=await LocalWeb.StartAsync(app=>app.Run(async h=>{
                var request=(await JsonNode.ParseAsync(h.Request.Body))!.AsObject();
                if(request["method"]!.GetValue<string>()=="server/discover"){var d=Discover();d["capabilities"]!["tools"]!["listChanged"]=false;await h.Response.WriteAsJsonAsync(Result(request,d));return;}
                pages++;
                if(request["params"]?["cursor"] is null){await h.Response.WriteAsJsonAsync(Result(request,new JsonObject {["tools"]=new JsonArray(McpTests.Tool("first")),["nextCursor"]="next"}));return;}
                if(fail){h.Response.StatusCode=503;return;}
                await h.Response.WriteAsJsonAsync(Result(request,new JsonObject {["tools"]=new JsonArray(McpTests.Tool("second"))}));
            }));
            var ctx=new FakePluginContext();ctx.Settings.Set("mcp.servers",new JsonObject {["http"]=Config(web.Url).Json()});
            await using var manager=new ServerManager(ctx);await manager.StartAsync([],CancellationToken.None);
            await McpTests.Connected(manager,"http");
            Check.Equal(2,manager.Catalog().Count);Check.Equal(2,pages);
            var before=manager.Catalog().Select(t=>t.Definition.Name).ToArray();fail=true;
            await Check.ThrowsAsync<McpException>(()=>manager.CommandAsync("http",false,CancellationToken.None));
            Check.Equal(string.Join(",",before),string.Join(",",manager.Catalog().Select(t=>t.Definition.Name)));
            ctx.Unload();
        });
    }
}
