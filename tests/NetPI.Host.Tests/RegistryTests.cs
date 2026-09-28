using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Events;
using NetPI.Host.Registries;
using NetPI.Host.Settings;

namespace NetPI.Host.Tests;

public static class RegistryTests
{
    private sealed class Tool(string name, string tag) : IAgentTool
    {
        public string Tag { get; } = tag;
        public ToolDefinition Definition { get; } = new() { Name = name, Description = tag };
        public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct) => Task.FromResult(ToolResult.Ok(Tag));
    }

    private interface IGreeter { string Hello(); }
    private sealed class Greeter(string s) : IGreeter { public string Hello() => s; }

    public static void Register(TestRunner r)
    {
        r.Add("services: priority wins, ties go to the latest, disposal restores", () =>
        {
            var reg = new ServiceRegistry();
            var changes = 0;
            reg.Changed += _ => changes++;
            var a = reg.Register<IGreeter>(new Greeter("a"));
            var b = reg.Register<IGreeter>(new Greeter("b"));
            Check.Equal("b", reg.Get<IGreeter>()!.Hello(), "latest wins a tie");
            var c = reg.Register<IGreeter>(new Greeter("c"), priority: -1);
            Check.Equal("b", reg.Get<IGreeter>()!.Hello());
            Check.Equal("b,a,c", string.Join(",", reg.GetAll<IGreeter>().Select(g => g.Hello())));
            b.Dispose();
            b.Dispose();
            Check.Equal("a", reg.Get<IGreeter>()!.Hello());
            // Registered by concrete type: still found through the interface.
            using var d = reg.Register(new Greeter("concrete"), priority: 5);
            Check.Equal("concrete", reg.Get<IGreeter>()!.Hello());
            a.Dispose();
            c.Dispose();
            Check.Equal(7, changes);
            Check.Equal(1, reg.GetAll<IGreeter>().Count);
        });

        r.Add("rpc: stacking, restore on dispose, params serialization, not_found", async () =>
        {
            var rpc = new RpcRegistry();
            var first = rpc.Register("m.x", (req, _) => Task.FromResult<object?>("first:" + req.Str("v")));
            var second = rpc.Register("m.x", (req, _) => Task.FromResult<object?>("second:" + req.Int("n")), "override", "plugin.b");
            Check.Equal("second:3", await rpc.InvokeAsync("m.x", new { n = 3 }));
            Check.Equal("plugin.b", rpc.List().Single(m => m.Method == "m.x").PluginId);
            second.Dispose();
            second.Dispose();
            Check.Equal("first:hi", await rpc.InvokeAsync("m.x", new JsonObject { ["v"] = "hi" }));
            first.Dispose();
            Check.False(rpc.Exists("m.x"));
            var ex = await Check.ThrowsAsync<RpcException>(() => rpc.InvokeAsync("m.x"));
            Check.Equal("not_found", ex.Code);
        });

        // What a plugin passes when it forwards a caller's own arguments (the diag tool's rpc action): a JsonObject.
        // Every kind of member has to survive the trip, or a filtered method silently ignores the filter.
        r.Add("rpc: a JsonObject's members arrive as they are (the diag rpc action's params)", async () =>
        {
            var rpc = new RpcRegistry();
            using var _ = rpc.Register("m.params", (req, _) => Task.FromResult<object?>(new JsonObject
            {
                ["n"] = req.Int("n"), ["s"] = req.Str("s"), ["b"] = req.Bool("b"),
                ["raw"] = req.Params.GetRawText(),
            }));

            var got = (JsonObject)(await rpc.InvokeAsync("m.params", new JsonObject { ["n"] = 3, ["s"] = "hi", ["b"] = true }))!;
            Check.Equal(3, got["n"]!.GetValue<int>(), "a number");
            Check.Equal("hi", got["s"]!.GetValue<string>(), "a string");
            Check.True(got["b"]!.GetValue<bool>(), "a bool");
            Check.Equal("""{"n":3,"s":"hi","b":true}""", got["raw"]!.GetValue<string>(), "nothing rewritten on the way");

            // and an element passes straight through, which is the shape the tool now sends
            using var doc = JsonDocument.Parse("""{"n":7,"s":"x"}""");
            var via = (JsonObject)(await rpc.InvokeAsync("m.params", doc.RootElement.Clone()))!;
            Check.Equal(7, via["n"]!.GetValue<int>());
            Check.Equal("x", via["s"]!.GetValue<string>());
        });

        r.Add("tools: highest priority per name, ties → latest, tools.disabled, tools.changed", async () =>
        {
            var file = Path.Combine(T.TempDir("tools"), "settings.json");
            await using var bus = new EventBus(NullLogger.Instance);
            using var settings = new SettingsStore(file, NullLogger.Instance);
            settings.AttachBus(bus);
            using var tools = new ToolRegistry(settings, bus, NullLogger.Instance);
            var changed = 0;
            using var sub = bus.Subscribe("tools.changed", _ => Interlocked.Increment(ref changed));

            var read1 = tools.Register(new Tool("read", "one"), 0, "p1");
            var read2 = tools.Register(new Tool("read", "two"), 0, "p2");
            var readLow = tools.Register(new Tool("read", "low"), -5, "p3");
            using var bash = tools.Register(new Tool("bash", "bash"), 0, "p1");
            Check.Equal("two", ((Tool)tools.Get("read")!).Tag);
            Check.Equal(2, tools.All.Count);
            Check.Equal(4, tools.Registrations.Count);
            read2.Dispose();
            Check.Equal("one", ((Tool)tools.Get("read")!).Tag);
            read1.Dispose();
            read1.Dispose();
            Check.Equal("low", ((Tool)tools.Get("read")!).Tag);

            settings.Set("tools.disabled", new JsonArray("bash"));
            tools.OnSettingsChanged();
            Check.True(tools.Get("bash") is null);
            Check.Equal("read", string.Join(",", tools.All.Select(t => t.Definition.Name)));
            Check.True(tools.IsDisabled("bash"));
            readLow.Dispose();
            await Wait.UntilAsync(() => Volatile.Read(ref changed) > 0, "tools.changed");
        });

        r.Add("ui: host fills pluginId/version; latest duplicate wins; version bump", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            using var ui = new UiRegistry(bus);
            var events = 0;
            using var sub = bus.Subscribe(EventTypes.UiChanged, _ => Interlocked.Increment(ref events));
            var tab = new UiTabInfo { Id = "t", Title = "Tab", Order = 5 };
            using var a = ui.AddTab(tab, "p.one", "v1");
            Check.Equal("p.one", ui.Tabs.Single().PluginId);
            Check.Equal("v1", ui.Tabs.Single().Version);
            var dup = ui.AddTab(new UiTabInfo { Id = "t", Title = "Tab 2" }, "p.one", "v1");
            Check.Equal("Tab 2", ui.Tabs.Single().Title);
            dup.Dispose();
            dup.Dispose();
            Check.Equal("Tab", ui.Tabs.Single().Title);
            ui.SetVersion("p.one", "v2");
            Check.Equal("v2", ui.Tabs.Single().Version);
            using var cmd = ui.AddCommand(new SlashCommandInfo { Name = "go", Description = "Go" }, "p.one");
            Check.Equal("p.one", ui.Commands.Single().PluginId);
            await Wait.UntilAsync(() => Volatile.Read(ref events) > 0, "ui.changed (debounced)");
        });

        r.Add("http registry: exact vs prefix routes, longest match, dispose", () =>
        {
            var http = new HttpRegistry();
            Func<Microsoft.AspNetCore.Http.HttpContext, Task> h1 = _ => Task.CompletedTask, h2 = _ => Task.CompletedTask, h3 = _ => Task.CompletedTask;
            var r1 = http.Map("p", "files/*", h1);
            using var r2 = http.Map("p", "files/special", h2);
            using var r3 = http.Map("p", "*", h3);
            Check.True(http.Match("p", "files/a/b") == h1);
            Check.True(http.Match("p", "/files/special/") == h2);
            Check.True(http.Match("p", "other") == h3);
            Check.True(http.Match("q", "files/a") is null);
            r1.Dispose();
            r1.Dispose();
            Check.True(http.Match("p", "files/a") == h3);
        });
    }
}
