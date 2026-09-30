using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Host.Web;
namespace NetPI.Host.Tests;

public static class McpLifecycleTests
{
    public static void Register(TestRunner r)
    {
        r.Add("mcp host: successful swap unloads old context and failed candidate retains healthy process", async () => {
            var root=T.TempDir("mcp-plugins");
            var built=Path.Combine(T.RepoRoot,"artifacts","dev","app","plugins","NetPI.Mcp");
            T.CopyDir(built,Path.Combine(root,"NetPI.Mcp"));
            await using var server=await PluginTests.StartAsync(root);
            var variable="NETPI_MCP_TEST_FAIL_"+Environment.ProcessId;
            Environment.SetEnvironmentVariable(variable,"0");
            try
            {
                await server.Rpc.InvokeAsync("mcp.save",new {id="fixture",config=new {
                    command="dotnet",args=new[]{typeof(McpLifecycleTests).Assembly.Location,"--mcp-fixture"},
                    cwd=Path.GetTempPath(),env=new Dictionary<string,string>{["MCP_FIXTURE_FAIL"]=variable}
                }});
                var original=Current(server);var pid=await Pid(server);
                await server.Plugins.ReloadAsync("netpi.mcp");
                Check.False(ReferenceEquals(original.Target,Current(server).Target));
                Check.True(await Collected(original),"old MCP assembly context is collectible");
                Check.True(Gone(pid),"old owned child exited after swap");
                var healthy=Current(server);var livePid=await Pid(server);
                Environment.SetEnvironmentVariable(variable,"1");
                await server.Plugins.ReloadAsync("netpi.mcp");
                Check.True(ReferenceEquals(healthy.Target,Current(server).Target),"failed replacement kept old context");
                Check.Equal(livePid,await Pid(server),"failed candidate did not kill healthy child");
                Environment.SetEnvironmentVariable(variable,"0");
                await server.Plugins.SetEnabledAsync("netpi.mcp",false);
                Check.True(Gone(livePid),"owned child exited on disable");
                Check.True(await Collected(healthy),"disabled MCP assembly context is collectible");
            }
            finally { Environment.SetEnvironmentVariable(variable,null); }
        });
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Current(NetPiServer server) => server.Kernel.Plugins.GetLoadState("netpi.mcp").Current!;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> Pid(NetPiServer server)
    {
        var tool=server.Kernel.Tools.All.Single(t=>t.Definition.Category=="mcp" && t.Definition.Name.StartsWith("mcp_fixture_"));
        var result=await tool.ExecuteAsync(new ToolContext {SessionId="s",AgentId="a",CallId="c",Cwd=Path.GetTempPath(),Services=server.Kernel.Services,Events=server.Events},JsonDocument.Parse("""{"city":"pid"}""").RootElement,CancellationToken.None);
        return int.Parse(result.Content.Split(':')[1].Trim());
    }
    private static bool Gone(int pid) { try {using var p=Process.GetProcessById(pid);return p.HasExited;} catch(ArgumentException){return true;} }
    private static async Task<bool> Collected(WeakReference weak)
    {
        for(var i=0;i<30 && weak.IsAlive;i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();await Task.Delay(50);}
        return !weak.IsAlive;
    }
}
