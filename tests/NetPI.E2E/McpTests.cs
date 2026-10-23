namespace NetPI.E2E;
public static class McpTests
{
    public static void Register(TestRunner r, Env env)
    {
        r.Add("mcp.deferred", "mcp: real server and MockLlm find one deferred remote tool with tool_search and call it by name; the request's tools never change",async () => {
            string? constantTools = null, constantInstructions = null;
            foreach (var count in new[] { 10, 100, 1000 })
            {
            await env.Rpc("mcp.save",new {id="fixture",config=new {command="dotnet",args=new[]{typeof(McpTests).Assembly.Location,"--mcp-fixture",count.ToString()},cwd=env.Root}});
            try
            {
                var tools=(await env.Rpc("mcp.tools",new {serverId="fixture"})).Arr("tools").ToList();
                Check.Equal(count,tools.Count);
                var target=tools.Single(t=>t.S("name")=="weather");
                var info=await env.Rpc("mcp.tool",new {id=target.S("id")});
                Check.Equal("weather",info.S("name")); Check.Equal("fixture",info.S("serverId")); Check.False(info.B("readOnly"));
                var session=await env.NewSession(CoreTests.Qwen);
                var mark=await env.MockMark();
                var started = DateTimeOffset.UtcNow;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result=await env.Run(session.S("id")!,$"[s:mcp id={target.S("id")}] Weather for Oslo");
                Check.Contains(result.LastAssistant.GetRawText(),"MCP-DONE");
                var outputs=result.Parts("tool_result").ToList();
                Check.Equal(2,outputs.Count);
                Check.Contains(outputs.Last().GetRawText(),"Weather: Oslo");
                var requests=(await env.MockLog()).Where(e=>e.L("seq")>mark).ToList();
                Check.Equal(3,requests.Count);
                // the first request is the same whatever the catalog's size: tool_search, none of the remote tools
                var first=await env.MockRequest(requests[0].L("seq"));
                var serialized=first.P("tools").GetRawText();
                if (constantTools is null) constantTools = serialized; else Check.Equal(constantTools, serialized);
                constantInstructions ??= first.S("instructions"); Check.Equal(constantInstructions, first.S("instructions"));
                Check.Contains(serialized,"tool_search"); Check.NotContains(serialized,target.S("id")!);
                // finding it does not change the request's tools (no prefill, 0f4a6e2): the next request declares the same
                // tools, the remote one still not among them, and the call by name runs all the same
                var second=await env.MockRequest(requests[1].L("seq"));
                Check.Equal(serialized, second.P("tools").GetRawText(), "the tools of the request after tool_search");
                Check.NotContains(second.P("tools").GetRawText(),target.S("id")!);
                foreach(var entry in requests)
                    Check.NotContains((await env.MockRequest(entry.L("seq"))).S("instructions"),"UNTRUSTED_SERVER_INSTRUCTIONS");
                Check.True(outputs.All(o => !o.B("isError")));
                var success = result.Events.Single(e => e.Type == "tool.end" && e.D.S("name") == target.S("id"));
                Console.WriteLine($"    MCP first successful call catalog={count}: {(success.At-started).TotalMilliseconds:0} ms");
                Console.WriteLine($"    MCP catalog={count}: serialized tools={constantTools!.Length} chars, instructions={constantInstructions!.Length} chars, mocked task={watch.ElapsedMilliseconds} ms");
            }
            finally {await env.Rpc("mcp.remove",new {id="fixture"});}
            }
        });
    }
}
