namespace NetPI.E2E;
public static class McpTests
{
    public static void Register(TestRunner r, Env env)
    {
        r.Add("mcp.gateway", "mcp: real server and MockLlm discover one schema and execute through gateway",async () => {
            string? constantTools = null, constantInstructions = null;
            foreach (var count in new[] { 10, 100, 1000 })
            {
            await env.Rpc("mcp.save",new {id="fixture",config=new {command="dotnet",args=new[]{typeof(McpTests).Assembly.Location,"--mcp-fixture",count.ToString()},cwd=env.Root}});
            try
            {
                var tools=(await env.Rpc("mcp.tools",new {serverId="fixture"})).Arr("tools").ToList();
                Check.Equal(count,tools.Count);
                var target=tools.Single(t=>t.S("name")=="weather");
                var session=await env.NewSession(CoreTests.Qwen);
                var mark=await env.MockMark();
                var started = DateTimeOffset.UtcNow;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result=await env.Run(session.S("id")!,$"[s:mcp id={target.S("id")} revision={target.S("revision")}] Weather for Oslo");
                Check.Contains(result.LastAssistant.GetRawText(),"MCP-DONE");
                var outputs=result.Parts("tool_result").ToList();
                Check.Equal(3,outputs.Count);
                Check.Contains(outputs.Last().GetRawText(),"Weather: Oslo");
                var requests=(await env.MockLog()).Where(e=>e.L("seq")>mark).ToList();
                Check.Equal(4,requests.Count);
                string? prefix=null;
                foreach(var entry in requests)
                {
                    var request=await env.MockRequest(entry.L("seq"));
                    var serialized=request.P("tools").GetRawText();
                    prefix ??=serialized;Check.Equal(prefix,serialized);
                    constantTools ??= serialized; Check.Equal(constantTools, serialized);
                    constantInstructions ??= request.S("instructions"); Check.Equal(constantInstructions, request.S("instructions"));
                    Check.Contains(serialized,"mcp_search");Check.Contains(serialized,"mcp_call");
                    Check.NotContains(serialized,target.S("id")!);
                    Check.NotContains(request.S("instructions"),"UNTRUSTED_SERVER_INSTRUCTIONS");
                }
                Check.True(outputs.All(o => !o.B("isError")));
                var success = result.Events.Single(e => e.Type == "tool.end" && e.D.S("name") == "mcp_call");
                Console.WriteLine($"    MCP first successful call catalog={count}: {(success.At-started).TotalMilliseconds:0} ms");
                Console.WriteLine($"    MCP catalog={count}: serialized tools={prefix!.Length} chars, instructions={constantInstructions!.Length} chars, schema inspections=1, argument errors=0, mocked task={watch.ElapsedMilliseconds} ms");
            }
            finally {await env.Rpc("mcp.remove",new {id="fixture"});}
            }
        });
    }
}
