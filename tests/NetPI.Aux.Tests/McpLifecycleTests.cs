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
        r.Add("mcp search: deterministic top three ranking and no match avoid catalog fallback",()=>{
            var ctx=new FakePluginContext();var manager=new ServerManager(ctx);var config=McpTests.Config();
            RemoteTool Entry(string name,string description,string arg)
            {
                var raw=McpTests.Tool(name);raw["description"]=description;raw["inputSchema"]!["properties"]=new JsonObject {[arg]=new JsonObject {["type"]="string"}};raw["inputSchema"]!.AsObject().Remove("required");return new RemoteTool(manager,config,raw);
            }
            var weather=Entry("cityForecast","Look up the forecast for a city","city");
            var files=Entry("findFiles","Search files by filename","filename");
            var mail=Entry("sendEmail","Send email to an address","recipient");
            var docs=new[]{weather,files,mail};
            foreach(var (query,target) in new[]{("weather city forecast",weather),("search filename",files),("email recipient",mail)})
                Check.True(McpSearchTool.Rank(docs,query,3).Take(3).Contains(target));
            Check.Equal(0,McpSearchTool.Rank(docs,"zzunknowncapabilityzz",3).Count);
            Check.Equal(weather,McpSearchTool.Rank(docs,weather.Definition.Name,1).Single());
            ctx.Unload();
        });
    }
}
