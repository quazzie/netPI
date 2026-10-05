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
        // "ping": the server pings the client before it answers the first tools/call, and echoes the client's reply in the result.
        var pong = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (request["method"] is null) { pong.TrySetResult(line); continue; }   // the client's answer to a request of ours
            var method = request["method"]!.GetValue<string>();
            if (request["id"] is null) continue;
            if (method == "server/discover")
            {
                if (mode == "slow") await Task.Delay(2500); // a LAN host on a bad day: the connection is slow, not broken
                if (mode == "legacy") await Send(new JsonObject { ["jsonrpc"]="2.0", ["id"]=request["id"]?.DeepClone(), ["error"]=new JsonObject { ["code"]=-32601, ["message"]="Unknown method" } });
                else await Send(Reply(request, new JsonObject { ["supportedVersions"]=new JsonArray("2026-07-28"),
                    ["capabilities"]=ResourcesOnly(mode)
                        ? new JsonObject { ["resources"]=new JsonObject { ["subscribe"]=false, ["listChanged"]=mode=="resources-subscribe" } }
                        : new JsonObject { ["tools"]=new JsonObject { ["listChanged"]=mode=="subscribe" },
                                           ["resources"]=new JsonObject { ["subscribe"]=false, ["listChanged"]=mode=="resources-subscribe" } },
                    ["instructions"]="UNTRUSTED_SERVER_INSTRUCTIONS" }));
            }
            else if (method == "initialize") { if (mode == "slow") await Task.Delay(2500); await Send(Reply(request, new JsonObject { ["protocolVersion"]="2025-11-25", ["capabilities"]=new JsonObject { ["tools"]=new JsonObject() } })); }
            else if (method == "tools/list")
            {
                var count = int.TryParse(mode, out var size) ? size : mode == "large" ? 250 : 1;
                var tools = new JsonArray();
                for (var i=0; i<count; i++) tools.Add(mode == "typed" && i == 0 ? Typed() : new JsonObject { ["name"]=i==0 ? "weather" : "weather_"+i, ["description"]=changed ? "Get UPDATED city weather" : "Get city weather", ["inputSchema"]=JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""") });
                await Send(Reply(request, new JsonObject { ["tools"]=tools }));
            }
            else if (method == "resources/list")
            {
                // Two pages, so the cursor loop is exercised, and a name that changes on notification.
                var page = new JsonArray();
                var listed = new JsonObject();
                if (request["params"]?["cursor"] is null)
                {
                    page.Add(Resource("SKILL.md", "skill://demo/SKILL.md", "Read before acting", "text/markdown"));
                    listed["nextCursor"] = "page2";
                }
                else
                {
                    page.Add(Resource(changed ? "UPDATED.md" : "references/patterns.md", "skill://demo/references/patterns.md", "Patterns and anti-patterns", "text/markdown"));
                    page.Add(Resource("logo.png", "skill://demo/logo.png", "A logo", "image/png"));
                    page.Add(Resource("large.md", "skill://demo/large.md", "A long reference document", "text/markdown"));
                }
                listed["resources"] = page;
                await Send(Reply(request, listed));
            }
            else if (method == "resources/read")
            {
                var uri = request["params"]?["uri"]?.GetValue<string>() ?? "";
                var known = uri == "skill://demo/SKILL.md" || uri == "skill://demo/references/patterns.md"
                    || uri == "skill://demo/UPDATED.md" || uri == "skill://demo/logo.png" || uri == "skill://demo/large.md";
                if (!known)
                {
                    var failure = new JsonObject();
                    failure["code"] = -32002;
                    failure["message"] = "Resource not found";
                    var errored = new JsonObject();
                    errored["jsonrpc"] = "2.0";
                    errored["id"] = request["id"]?.DeepClone();
                    errored["error"] = failure;
                    await Send(errored);
                }
                else
                {
                    var content = new JsonObject();
                    content["uri"] = uri;
                    if (uri == "skill://demo/logo.png") { content["mimeType"] = "image/png"; content["blob"] = "AAECAwQFBgc="; }
                    else if (uri == "skill://demo/large.md") content["text"] = new string('x', 5000);
                    else { content["mimeType"] = "text/markdown"; content["text"] = "Consult " + uri + " before acting."; }
                    var entries = new JsonArray();
                    entries.Add(content);
                    var read = new JsonObject();
                    read["contents"] = entries;
                    await Send(Reply(request, read));
                }
            }
            else if (method == "subscriptions/listen")
            {
                var notice = mode == "resources-subscribe" ? "notifications/resources/list_changed" : "notifications/tools/list_changed";
                JsonObject Notice(string name, JsonNode id) => new() { ["jsonrpc"]="2.0", ["method"]=name, ["params"]=new JsonObject { ["notifications"]=new JsonObject { ["toolsListChanged"]=true }, ["_meta"]=new JsonObject { ["io.modelcontextprotocol/subscriptionId"]=id.DeepClone() } } };
                await Send(Notice("notifications/subscriptions/acknowledged",request["id"]!));
                await Send(Notice(notice,JsonValue.Create(9999)!));
                changed=true;
                await Send(Notice(notice,request["id"]!));
                await Send(Notice(notice,request["id"]!));
            }
            else if (method == "tools/call")
            {
                if (counter is not null) await File.AppendAllTextAsync(counter, request["params"]!["arguments"]!.ToJsonString()+"\n");
                jobs.Add(Task.Run(async () => {
                    if (mode == "ping")
                    {
                        await Send(new JsonObject { ["jsonrpc"]="2.0", ["id"]="srv-ping", ["method"]="ping" });
                        var answer = await pong.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await Send(Reply(request, new JsonObject { ["content"]=new JsonArray(new JsonObject { ["type"]="text", ["text"]="pong: "+answer }) }));
                        return;
                    }
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
    // A mode whose server publishes resources and no tools at all.
    private static bool ResourcesOnly(string mode) => mode.StartsWith("resources", StringComparison.Ordinal);
    private static JsonNode Resource(string name, string uri, string description, string mimeType) =>
        new JsonObject { ["uri"]=uri, ["name"]=name, ["description"]=description, ["mimeType"]=mimeType };
}
