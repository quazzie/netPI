using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.CodeMode;

namespace NetPI.Agent.Tests;

public static class CodeModeTests
{
    /// <summary>A tool a script calls: counts its calls, answers with its name and the city.</summary>
    private sealed class Lookup(string name, bool deferred = false, bool readOnly = true, int delayMs = 0) : IAgentTool
    {
        public int Calls;
        public int Running, MaxRunning;
        public ToolDefinition Definition { get; } = new()
        {
            Name = name, Description = "Look up " + name.Replace('_', ' ') + " for a city.", Deferred = deferred, ReadOnly = readOnly,
            Parameters = JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}}}""")!.AsObject(),
        };
        public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var now = Interlocked.Increment(ref Running);
            lock (this) MaxRunning = Math.Max(MaxRunning, now);
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs, ct);
                var city = new ToolArgs(args).Str("city") ?? "?";
                if (city == "fail") return ToolResult.Error("no such city");
                return ToolResult.Ok(Definition.Name + ":" + city, new { city });
            }
            finally { Interlocked.Decrement(ref Running); }
        }
    }

    private sealed class Hook : IAgentHook
    {
        public int Order => 1;
        public readonly List<string> Before = [], After = [];
        public ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call)
        {
            lock (Before) Before.Add(call.Id + " " + call.Name);
            return ValueTask.FromResult<ToolCallDecision?>(call.Name == "danger" ? new ToolCallDecision { Block = true, Reason = "danger is off limits" } : null);
        }
        public ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result)
        {
            lock (After) After.Add(call.Name);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A stand-in for the runtime's nested calls: runs the tools directly.</summary>
    private sealed class Direct(params IAgentTool[] tools) : INestedToolCalls
    {
        public IReadOnlyList<ToolDefinition> Tools => [.. tools.Select(t => t.Definition)];
        public async Task<ToolResult> CallAsync(string name, JsonElement arguments, CancellationToken ct) =>
            tools.FirstOrDefault(t => t.Definition.Name == name) is { } tool
                ? await tool.ExecuteAsync(null!, arguments, ct)
                : ToolResult.Error($"Unknown tool '{name}'");
    }

    private static Task<ScriptRunner.Outcome> Run(string code, ScriptStore? store = null, double seconds = 10, long memory = 64L << 20, params IAgentTool[] tools) =>
        new ScriptRunner(new ScriptHost(new Direct(tools), store ?? ScriptStore.Of(null), null), new ScriptLimits(TimeSpan.FromSeconds(seconds), memory))
            .RunAsync(code, CancellationToken.None);

    public static void Register(TestRunner t)
    {
        t.Add("codemode: a script calls tools in parallel through the hooks, deferred ones too, and only its output reaches the model", async () =>
        {
            await using var h = await TestHost.StartAsync();
            await h.StartPluginAsync(new CodeModePlugin());
            var ctx = new TestPluginContext(h, "netpi.test");
            var weather = new Lookup("weather_lookup", deferred: true, delayMs: 150);
            ctx.Tools.Register(weather);
            var hook = new Hook(); h.Services.Register<IAgentHook>(hook);
            var session = h.NewSession(); var stage = 0;
            const string code = """
                const cities = ['Oslo', 'Bergen', 'Tromso'];
                const r = await Promise.all(cities.map(city => tools.weather_lookup({ city })));
                text(r.length + ' results');
                return r.join(';');
                """;
            h.Catalog.Handler = (r, ct) => stage++ == 0 ? Reply.Tools(Reply.Call("codemode", new { code })) : Reply.Text("done");
            await h.SendAsync(session.Id, "weather?"); await h.IdleAsync(session.Id);

            var result = h.Messages(session.Id).SelectMany(m => m.ToolResults).Single();
            Check.False(result.IsError, result.Content);
            Check.Contains(result.Content, "Script completed");
            Check.Contains(result.Content, "3 results");
            Check.Contains(result.Content, "weather_lookup:Oslo;weather_lookup:Bergen;weather_lookup:Tromso");
            Check.Equal(3, weather.Calls);
            Check.True(weather.MaxRunning > 1, "read-only calls run side by side");
            Check.Equal(3, hook.Before.Count(b => b.Contains("~") && b.EndsWith(" weather_lookup")));
            Check.Equal(3, hook.After.Count(a => a == "weather_lookup"));
            Check.Equal(3, result.Details!["callCount"]!.GetValue<int>());
            // the deferred tool was reached without being declared
            Check.False(h.Catalog.Requests.Any(r => r.Tools.Any(d => d.Name == "weather_lookup")));
            Check.True(h.Catalog.Requests.First().Tools.Any(d => d.Name == "codemode"));
            ctx.Unload();
        });

        t.Add("codemode: a blocked call rejects; the script keeps its output so far and reports the error; writes run one at a time", async () =>
        {
            await using var h = await TestHost.StartAsync();
            await h.StartPluginAsync(new CodeModePlugin());
            var ctx = new TestPluginContext(h, "netpi.test");
            var danger = new Lookup("danger", readOnly: false);
            var write = new Lookup("write_note", readOnly: false, delayMs: 50);
            ctx.Tools.Register(danger); ctx.Tools.Register(write);
            h.Services.Register<IAgentHook>(new Hook());
            var session = h.NewSession(); var stage = 0;
            const string code = """
                await Promise.all([1, 2, 3].map(i => tools.write_note({ city: 'c' + i })));
                const settled = await callTool('write_note', { city: 'fail' });
                text('settled: ' + settled.isError + ' ' + settled.content);
                text('before');
                await tools.danger({ city: 'x' });
                text('never');
                """;
            h.Catalog.Handler = (r, ct) => stage++ == 0 ? Reply.Tools(Reply.Call("codemode", new { code })) : Reply.Text("done");
            await h.SendAsync(session.Id, "go"); await h.IdleAsync(session.Id);

            var result = h.Messages(session.Id).SelectMany(m => m.ToolResults).Single();
            Check.True(result.IsError);
            Check.Contains(result.Content, "Script failed");
            Check.Contains(result.Content, "settled: true no such city");
            Check.Contains(result.Content, "before");
            Check.NotContains(result.Content, "never");
            Check.Contains(result.Content, "danger is off limits");
            Check.Equal(0, danger.Calls);
            Check.Equal(4, write.Calls);
            Check.Equal(1, write.MaxRunning);
            ctx.Unload();
        });

        t.Add("codemode: store and load keep values across a chat's scripts, only from scripts that succeed", async () =>
        {
            await using var h = await TestHost.StartAsync();
            await h.StartPluginAsync(new CodeModePlugin());
            var session = h.NewSession(); var stage = 0;
            string[] scripts =
            [
                "store('cursor', { page: 2 }); return 'stored';",
                "store('cursor', { page: 99 }); throw new Error('boom');",
                "return 'page ' + load('cursor').page + ' ' + (load('missing') === undefined);",
            ];
            h.Catalog.Handler = (r, ct) => stage < 3 ? Reply.Tools(Reply.Call("codemode", new { code = scripts[stage++] })) : Reply.Text("done");
            await h.SendAsync(session.Id, "go"); await h.IdleAsync(session.Id);
            var results = h.Messages(session.Id).SelectMany(m => m.ToolResults).ToList();
            Check.Equal(3, results.Count);
            Check.Contains(results[1].Content, "Error: boom");
            Check.Contains(results[2].Content, "page 2 true");
            Check.Equal("""{"cursor":{"page":2}}""", h.Sessions.GetSession(session.Id)!.Meta?[ScriptStore.MetaKey]?.ToJsonString());
        });

        t.Add("codemode: the sandbox has no Node, timers or .NET, and modern JavaScript works", async () =>
        {
            var o = await Run("return [typeof require, typeof process, typeof fetch, typeof setTimeout, typeof System, typeof importNamespace].join(',');");
            Check.True(o.Ok, o.Text);
            Check.Contains(o.Text, "undefined,undefined,undefined,undefined,undefined,undefined");
            o = await Run("class A { #x = 1; get x() { return this.#x; } } const { a, ...rest } = { a: 1, b: 2 }; return [new A().x, rest.b, [1, 2, 3].at(-1), 2n ** 64n + ''];");
            Check.True(o.Ok, o.Text);
            Check.Contains(o.Text, """[1,2,3,"18446744073709551616"]""");
        });

        t.Add("codemode: limits — a hung promise, a busy loop, memory, recursion, a syntax error", async () =>
        {
            var o = await Run("await new Promise(() => {});");
            Check.False(o.Ok); Check.Contains(o.Text, "can never settle");
            o = await Run("while (true) {}", seconds: 0.5);
            Check.False(o.Ok); Check.True(o.TimedOut); Check.Contains(o.Text, "did not finish within");
            o = await Run("const a = []; while (true) a.push('x'.repeat(10000));", memory: 32L << 20);
            Check.False(o.Ok); Check.Contains(o.Text, "out of memory");
            o = await Run("function f() { return f(); } f();");
            Check.False(o.Ok); Check.Contains(o.Text, "recursion");
            o = await Run("this is not javascript");
            Check.False(o.Ok); Check.Contains(o.Text, "Script error:");
        });

        t.Add("codemode: discovery — ALL_TOOLS, searchTools, describeTool, an unknown tool's error", async () =>
        {
            var tools = new IAgentTool[] { new Lookup("weather_lookup"), new Lookup("stock_quote") };
            var o = await Run("""
                text(ALL_TOOLS.map(t => t.name).sort().join(','));
                text(searchTools('weather').map(t => t.name).join(','));
                text(describeTool('stock_quote').parameters.properties.city.type);
                text(String(describeTool('nope')));
                text('stock_quote' in tools);
                try { await tools.nope({}); } catch (e) { text(e.message); }
                """, tools: tools);
            Check.True(o.Ok, o.Text);
            Check.Contains(o.Text, "stock_quote,weather_lookup");
            Check.Contains(o.Text, "==> text 2/6 <==\nweather_lookup\n");
            Check.Contains(o.Text, "\nstring\n");
            Check.Contains(o.Text, "\nundefined\n");
            Check.Contains(o.Text, "\ntrue\n");
            Check.Contains(o.Text, "Unknown tool 'nope'");
        });

        t.Add("codemode: a nested tool cannot run a script", async () =>
        {
            await using var h = await TestHost.StartAsync();
            var ctx = await h.StartPluginAsync(new CodeModePlugin());
            var tool = ctx.Tools.Get("codemode")!;
            var context = new ToolContext { SessionId = "s", AgentId = "a", CallId = "c~1", Cwd = h.Workspace, Services = h.Services, Events = ctx.Events };
            var r = await tool.ExecuteAsync(context, JsonDocument.Parse("""{"code":"return 1"}""").RootElement, CancellationToken.None);
            Check.True(r.IsError); Check.Contains(r.Content, "not from inside another tool");
        });
    }
}
