using System.Text.Json.Nodes;
using NetPI.Mcp;

namespace NetPI.Aux.Tests;

/// <summary>Resources: a server that publishes none of them alongside tools, and one that publishes only them.</summary>
public static class McpResourceTests
{
    private static ServerConfig Config(string mode) => new()
    {
        Id = "fixture", Command = "dotnet", Args = [typeof(McpResourceTests).Assembly.Location, "--mcp-fixture", mode],
        Cwd = Path.GetTempPath(), ConnectTimeoutMs = 5000, CallTimeoutMs = 5000,
    };

    private static ToolContext Context(FakePluginContext ctx, string session = "ses_1") =>
        new() { SessionId = session, AgentId = "agt_1", CallId = "call_1", Cwd = ctx.Paths.DefaultWorkspace, Services = ctx.Services, Events = ctx.Events };

    public static void Register(TestRunner r)
    {
        r.Add("mcp resources: a server that advertises resources and no tools still connects", async () =>
        {
            await using var c = new McpConnection(Config("resources"), 100000, _ => { });
            using var timeout = new CancellationTokenSource(10000);
            await c.InitializeAsync(timeout.Token);
            Check.False(c.HasTools);
            Check.True(c.HasResources);
            Check.False(c.ResourceSubscribe);
            Check.Equal(0, c.ListAsync(10, 100000, timeout.Token).Result.Count);
            // Two pages, followed by the cursor loop to the end.
            var resources = await c.ListResourcesAsync(10, 100000, timeout.Token);
            Check.Equal(4, resources.Count);
            Check.Equal("skill://demo/SKILL.md", resources[0]["uri"]!.GetValue<string>());
            var read = await c.ReadResourceAsync("skill://demo/SKILL.md", timeout.Token);
            Check.Contains(read.ToJsonString(), "Consult skill://demo/SKILL.md");
            await Check.ThrowsAsync<McpException>(() => c.ReadResourceAsync("skill://demo/nope.md", timeout.Token));
            // An oversized catalog fails explicitly rather than quietly truncating.
            await Check.ThrowsAsync<McpException>(() => c.ListResourcesAsync(1, 100000, timeout.Token));
        });

        r.Add("mcp resources: mcp_search finds them, mcp_resource reads text and never puts a blob in context", async () =>
        {
            var ctx = new FakePluginContext();
            ctx.Sessions.CreateSession(new SessionInfo { Id = "ses_1" });
            ctx.Settings.Set("mcp.servers", new JsonObject { ["fixture"] = Config("resources").Json() });
            await using var manager = new ServerManager(ctx);
            await manager.StartAsync([], CancellationToken.None);
            var search = new McpSearchTool(ctx, manager);
            var resource = new McpResourceTool(ctx, manager);
            ctx.Tools.Register(search); ctx.Tools.Register(resource);

            Check.Equal(0, manager.Catalog().Count);
            Check.Equal(4, manager.Resources(null).Count);
            var found = await search.ExecuteAsync(Context(ctx), T.Args(new { query = "patterns anti-patterns" }), CancellationToken.None);
            Check.Contains(found.Content, "skill://demo/references/patterns.md");
            Check.Contains(found.Content, "\"kind\":\"resource\"");
            // A skill document is reachable by its own name too.
            Check.Contains((await search.ExecuteAsync(Context(ctx), T.Args(new { query = "SKILL.md read before acting" }), CancellationToken.None)).Content, "skill://demo/SKILL.md");

            var read = await resource.ExecuteAsync(Context(ctx), T.Args(new { uri = "skill://demo/SKILL.md" }), CancellationToken.None);
            Check.Contains(read.Content, "Consult skill://demo/SKILL.md");
            Check.Contains(NetPiJson.ToNode(read.Details)!.ToJsonString(), "\"kind\":\"mcp-resource\"");

            // Binary: reported, never decoded into the conversation.
            var blob = await resource.ExecuteAsync(Context(ctx), T.Args(new { uri = "skill://demo/logo.png" }), CancellationToken.None);
            Check.Contains(blob.Content, "Binary resource");
            Check.NotContains(blob.Content, "AAECAwQFBgc=");
            Check.NotContains(NetPiJson.ToNode(blob.Details)!.ToJsonString(), "AAECAwQFBgc=");
            Check.Contains(NetPiJson.ToNode(blob.Details)!.ToJsonString(), "image/png");

            // The URI is the gate: nothing the server did not advertise is ever requested.
            var refused = await Check.ThrowsAsync<McpException>(() => resource.ExecuteAsync(Context(ctx),
                T.Args(new { uri = "https://example.com/anything.md" }), CancellationToken.None));
            Check.Contains(refused.Message, "not advertised");
            var empty = Check.Throws<McpException>(() => resource.ExecuteAsync(Context(ctx),
                T.Args(new { uri = "x" }), CancellationToken.None));
            Check.Contains(empty.Message, "mcp_search");

            // Oversized text is kept whole on disk rather than truncated into the context.
            ctx.Settings.Set("mcp.maxResourceChars", 1024);
            var large = await resource.ExecuteAsync(Context(ctx), T.Args(new { uri = "skill://demo/large.md" }), CancellationToken.None);
            Check.Contains(large.Content, "Truncated");
            var details = NetPiJson.ToNode(large.Details)!.AsObject();
            Check.Equal(5000, details["chars"]!.GetValue<int>());
            Check.True(File.Exists(details["file"]!.GetValue<string>()));
            Check.Equal(5000, File.ReadAllText(details["file"]!.GetValue<string>()).Length);
            await manager.DisposeAsync(); ctx.Unload();
        });

        r.Add("mcp resources: an oversized read is bounded, an allow-list hides, and a notification refreshes", async () =>
        {
            var ctx = new FakePluginContext();
            ctx.Settings.Set("mcp.servers", new JsonObject { ["fixture"] = Config("resources-subscribe").Json() });
            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = ctx.Events.Subscribe("mcp.serverChanged", _ => refreshed.TrySetResult());
            await using var manager = new ServerManager(ctx);
            await manager.StartAsync([], CancellationToken.None);
            Check.Equal(4, manager.Resources(null).Count);
            // resources/list_changed re-reads the catalog, and the server renamed a resource when it did.
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await manager.CommandAsync("fixture", false, CancellationToken.None);
            Check.Contains(string.Join(",", manager.Resources(null).Select(r => r.Uri)), "skill://demo/references/patterns.md");
            await manager.DisposeAsync();

            // The per-server allow-list is the gate upstream of the catalog: an empty list exposes nothing.
            var restricted = new FakePluginContext();
            restricted.Settings.Set("mcp.servers", new JsonObject
            {
                ["fixture"] = (Config("resources") with { Resources = ["skill://demo/SKILL.md"] }).Json(),
            });
            await using var only = new ServerManager(restricted);
            await only.StartAsync([], CancellationToken.None);
            Check.Equal(1, only.Resources(null).Count);
            Check.Equal("skill://demo/SKILL.md", only.Resources(null)[0].Uri);
            restricted.Settings.Set("mcp.servers", new JsonObject { ["fixture"] = (Config("resources") with { Resources = [] }).Json() });
            await only.ReconcileAsync(CancellationToken.None);
            Check.Equal(0, only.Resources(null).Count);
            await only.DisposeAsync(); restricted.Unload();
            ctx.Unload();
        });
    }
}
