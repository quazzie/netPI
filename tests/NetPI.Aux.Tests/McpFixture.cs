using System.Text.Json.Nodes;
namespace NetPI.Aux.Tests;

// A child process fixture; stdout contains protocol frames only. No network or model credentials.
internal static class McpFixture
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (Environment.GetEnvironmentVariable("MCP_FIXTURE_FAIL") == "1") return 1;
        var mode = args.FirstOrDefault() ?? "modern";
        var write = new SemaphoreSlim(1);
        var jobs = new List<Task>();
        bool changed = false;
        var counter = args.Length > 1 ? args[1] : null;
        async Task Send(JsonObject message) { await write.WaitAsync(); try { await Console.Out.WriteLineAsync(message.ToJsonString()); } finally { write.Release(); } }
        JsonObject Reply(JsonObject request, JsonObject result) => new() { ["jsonrpc"]="2.0", ["id"]=request["id"]?.DeepClone(), ["result"]=result };
        // A tool whose arguments are typed the way a remote schema usually types them.
        JsonObject Typed() => new() { ["name"]="weather", ["description"]="Set a dashboard", ["inputSchema"]=JsonNode.Parse(
            """
            {"type":"object",
             "$defs":{"view":{"type":"object","properties":{"title":{"type":"string"},"max_columns":{"type":"integer"}}}},
             "properties":{"list_only":{"type":"boolean"},"timeout":{"type":"integer"},
               "views":{"type":"array","items":{"$ref":"#/$defs/view"}},
               "config":{"type":"object","properties":{"patch":{"type":"array","items":{"type":"integer"}}}},
               "meta":{"type":"object","properties":{"item":{"type":"string"}}},
               "script":{"type":"string"}}}
            """)! };
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var request = JsonNode.Parse(line)!.AsObject();
            var method = request["method"]!.GetValue<string>();
            if (request["id"] is null) continue;
            if (method == "server/discover")
            {
                if (mode == "legacy") await Send(new JsonObject { ["jsonrpc"]="2.0", ["id"]=request["id"]!.DeepClone(), ["error"]=new JsonObject { ["code"]=-32601, ["message"]="Unknown method" } });
                else await Send(Reply(request, new JsonObject { ["supportedVersions"]=new JsonArray("2026-07-28"), ["capabilities"]=new JsonObject { ["tools"]=new JsonObject { ["listChanged"]=mode=="subscribe" } }, ["instructions"]="UNTRUSTED_SERVER_INSTRUCTIONS" }));
            }
            else if (method == "initialize") await Send(Reply(request, new JsonObject { ["protocolVersion"]="2025-11-25", ["capabilities"]=new JsonObject { ["tools"]=new JsonObject() } }));
            else if (method == "tools/list")
            {
                var count = int.TryParse(mode, out var size) ? size : mode == "large" ? 250 : 1;
                var tools = new JsonArray();
                for (var i=0; i<count; i++) tools.Add(mode == "typed" && i == 0 ? Typed() : new JsonObject { ["name"]=i==0 ? "weather" : "weather_"+i, ["description"]=changed ? "Get UPDATED city weather" : "Get city weather", ["inputSchema"]=JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""") });
                await Send(Reply(request, new JsonObject { ["tools"]=tools }));
            }
            else if (method == "subscriptions/listen")
            {
                JsonObject Notice(string name, JsonNode id) => new() { ["jsonrpc"]="2.0", ["method"]=name, ["params"]=new JsonObject { ["notifications"]=new JsonObject { ["toolsListChanged"]=true }, ["_meta"]=new JsonObject { ["io.modelcontextprotocol/subscriptionId"]=id.DeepClone() } } };
                await Send(Notice("notifications/subscriptions/acknowledged",request["id"]!));
                await Send(Notice("notifications/tools/list_changed",JsonValue.Create(9999)!));
                changed=true;
                await Send(Notice("notifications/tools/list_changed",request["id"]!));
                await Send(Notice("notifications/tools/list_changed",request["id"]!));
            }
            else if (method == "tools/call")
            {
                if (counter is not null) await File.AppendAllTextAsync(counter, request["params"]!["arguments"]!.ToJsonString()+"\n");
                jobs.Add(Task.Run(async () => {
                    var city = request["params"]?["arguments"]?["city"]?.GetValue<string>() ?? "";
                    if (city == "slow") await Task.Delay(300);
                    if (city == "malformed") { await write.WaitAsync(); try { await Console.Out.WriteLineAsync("garbage"); } finally { write.Release(); } return; }
                    // "typed" echoes what arrived, so a test can see the types the server received.
                    var echo = mode == "typed" && request["params"]?["arguments"]?["city"] is null
                        ? request["params"]!["arguments"]!.ToJsonString()
                        : city == "pid" ? "PID: "+Environment.ProcessId : "Weather: "+city;
                    await Send(Reply(request, new JsonObject { ["content"]=new JsonArray(new JsonObject { ["type"]="text", ["text"]=echo }) }));
                }));
            }
            else await Send(Reply(request, new JsonObject()));
        }
        await Task.WhenAll(jobs); return 0;
    }
}
