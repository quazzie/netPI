using System.Globalization;
using NetPI.MockLlm;

// Scripted mock model server (AiProxy + Anthropic). See docs/TESTING.md.
//   dotnet tests/MockLlm/bin/Debug/MockLlm.dll [--port 7479] [--speed 1] [--tiny-ctx 12000] [--anthropic-key KEY] [--verbose]
var o = new MockOptions();
string? initHome = null;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--speed": o.Speed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--tiny-ctx": o.TinyContext = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--anthropic-key": o.AnthropicKey = Next(); break;
        case "--verbose" or "-v": o.Verbose = true; break;
        case "--init-home": initHome = Next(); break;
        case "-h" or "--help":
            Console.WriteLine("MockLlm [--port 7479] [--speed 1] [--tiny-ctx 12000] [--anthropic-key KEY] [--verbose] [--init-home DIR]");
            Console.WriteLine("  --init-home DIR   write/merge DIR/settings.json so `netpi-server --home DIR` uses this mock, then keep running");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}");
            return 2;
    }
}

if (initHome is not null) InitHome(initHome, $"http://127.0.0.1:{o.Port}", o.AnthropicKey ?? "mock-key");

await using var server = await MockLlmServer.StartAsync(o);
Console.WriteLine($"MockLlm listening on {server.BaseUrl}");
Console.WriteLine($"  AiProxy:   providers.aiproxy.baseUrl   = {server.BaseUrl}");
Console.WriteLine($"  Anthropic: providers.anthropic.baseUrl = {server.BaseUrl}/anthropic  (any apiKey)");
Console.WriteLine("  Control:   GET /_stats, GET /_log?since=N, GET /_request/{seq}, POST /_reset");
using var stop = CancellationTokenSource.CreateLinkedTokenSource(server.Stopping); // SIGTERM (host lifetime)
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
return 0;

// Point a NetPI home at this mock: providers.aiproxy / providers.anthropic (other settings are kept).
static void InitHome(string home, string baseUrl, string key)
{
    home = Path.GetFullPath(home.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), home[1..].TrimStart('/', '\\')) : home);
    Directory.CreateDirectory(home);
    var file = Path.Combine(home, "settings.json");
    var root = File.Exists(file) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as System.Text.Json.Nodes.JsonObject ?? [] : [];
    if (root["providers"] is not System.Text.Json.Nodes.JsonObject providers) root["providers"] = providers = [];
    if (providers["aiproxy"] is not System.Text.Json.Nodes.JsonObject aiproxy) providers["aiproxy"] = aiproxy = [];
    aiproxy["baseUrl"] = baseUrl;
    if (providers["anthropic"] is not System.Text.Json.Nodes.JsonObject anthropic) providers["anthropic"] = anthropic = [];
    anthropic["baseUrl"] = baseUrl + "/anthropic";
    anthropic["apiKey"] = key;
    File.WriteAllText(file, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Wrote {file}: start NetPI with  netpi-server --home \"{home}\"");
}
