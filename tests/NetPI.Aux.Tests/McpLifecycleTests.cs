using System.Text.Json.Nodes;
using NetPI.Mcp;
namespace NetPI.Aux.Tests;
public static class McpLifecycleTests
{
    public static void Register(TestRunner r)
    {
        r.Add("mcp lifecycle: modern stdio notifications refresh once per real definition change",async ()=>{
            var ctx=new FakePluginContext();ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=McpTests.Config("subscribe").Json()});
            var updated=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub=ctx.Events.Subscribe("mcp.toolsChanged",e=>{if(e.As<JsonObject>()?["updated"] is JsonArray a && a.Count>0) updated.TrySetResult();});
            await using var manager=new ServerManager(ctx);await manager.StartAsync([],CancellationToken.None);
            await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await manager.CommandAsync("fixture",false,CancellationToken.None);
            Check.Contains(manager.Catalog().Single().Definition.Description,"UPDATED");
            Check.Equal(1,ctx.Bus.Events.Count(e=>e.Type=="mcp.toolsChanged" && e.As<JsonObject>()?["updated"] is JsonArray a && a.Count>0));
            ctx.Unload();
        });
        r.Add("mcp lifecycle: transport loss reconnects retaining tool id and never replays an executed call",async ()=>{
            var ctx=new FakePluginContext();var counter=Path.Combine(ctx.Paths.TempDir,"calls.txt");
            var config=McpTests.Config() with {Args=[typeof(McpTests).Assembly.Location,"--mcp-fixture","modern",counter]};
            ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=config.Json()});
            var reconnected=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub=ctx.Events.Subscribe("mcp.serverChanged",e=>{
                var d=e.As<JsonObject>();if(d?["status"]?.GetValue<string>()=="connected" && d["generation"]!.GetValue<long>()>1)reconnected.TrySetResult();
            });
            await using var manager=new ServerManager(ctx);await manager.StartAsync([],CancellationToken.None);
            await McpTests.Connected(manager,"fixture");
            var tool=manager.Catalog().Single();var revision=tool.Definition.Revision;
            var result=await manager.CallAsync(tool,T.Args(new {city="malformed"}),CancellationToken.None);
            Check.True(result.IsError);
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Equal(tool.Definition.Name,manager.Catalog().Single().Definition.Name);
            Check.Equal(revision,manager.Catalog().Single().Definition.Revision);
            Check.Equal(1,File.ReadAllLines(counter).Length);
            // Configuration changes retain the full UI inventory so excluded tools can be exposed again.
            ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=(config with {Tools=[]}).Json()});
            await manager.ReconcileAsync(CancellationToken.None);Check.Equal(0,manager.Catalog().Count);Check.Equal(1,manager.Inventory("fixture").Count);
            ctx.Settings.Set("mcp.servers",new JsonObject {["fixture"]=(config with {Enabled=false}).Json()});
            await manager.ReconcileAsync(CancellationToken.None);Check.Equal(0,manager.Catalog().Count);
            ctx.Unload();
        });
        r.Add("mcp: a command for a stopped server fails at once, not on the caller's timeout",async ()=>{
            var ctx=new FakePluginContext();
            ctx.Settings.Set("mcp.servers",new JsonObject { ["fixture"] = McpTests.Config().Json() });
            var manager=new ServerManager(ctx);
            await manager.StartAsync([],CancellationToken.None);
            await McpTests.Connected(manager,"fixture");
            await manager.DisposeAsync();   // every server is retired: its worker will never answer a command
            var sw=System.Diagnostics.Stopwatch.StartNew();
            var ex=await Check.ThrowsAsync<Exception>(()=>manager.CommandAsync("fixture",true,CancellationToken.None));
            Check.True(sw.Elapsed<TimeSpan.FromSeconds(2),"fails at once, not on the caller's timeout: "+sw.Elapsed);
            Check.True(ex is McpException or RpcException,ex.GetType().Name);
            ctx.Unload();
        });
        r.Add("mcp: an unreadable catalog entry is named in the rejected list and its failure is logged, not swallowed",async ()=>{
            var ctx=new FakePluginContext();
            ctx.Settings.Set("mcp.servers",new JsonObject { ["fixture"] = McpTests.Config("ui").Json() });
            await using var manager=new ServerManager(ctx);
            await manager.StartAsync([],CancellationToken.None);
            await McpTests.Connected(manager,"fixture");
            var tools=manager.Inventory("fixture");
            Check.Equal(3,tools.Count,"the readable tools still come back");
            Check.True(tools.All(t=>t.RemoteName!="broken"),"the unreadable one is out");
            Check.True(ctx.Log.Lines.Any(l=>l.Contains("a catalog entry could not be read")),"its failure is logged, not swallowed");
            var rejected=(JsonArray)manager.Snapshot()["servers"]![0]!["rejected"]!;
            Check.Equal(1,rejected.Count(j=>j?["name"]?.GetValue<string>()=="broken"),"named in the rejected list");
            manager.Inventory("fixture");
            Check.Equal(1,((JsonArray)manager.Snapshot()["servers"]![0]!["rejected"]!).Count(j=>j?["name"]?.GetValue<string>()=="broken"),"asking again adds no duplicate");
            ctx.Unload();
        });
    }
}
