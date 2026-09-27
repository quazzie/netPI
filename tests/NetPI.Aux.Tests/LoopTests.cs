using System.Text.Json.Nodes;
using NetPI.Loops;

namespace NetPI.Aux.Tests;

public static class LoopTests
{
    private sealed class Env
    {
        public required FakePluginContext Ctx { get; init; }
        public required LoopHook Hook { get; init; }
        public required AgentTurnContext Turn { get; init; }
        private int _id;

        public async Task Ran(string tool, object args, string result, bool error = false, string? image = null)
        {
            var call = new ToolCallPart { Id = $"call_{++_id}", Name = tool, Arguments = NetPiJson.ToNode(args)!.ToJsonString() };
            await Hook.OnAfterToolCallAsync(Turn, call, new ToolResultPart
            {
                CallId = call.Id, Name = tool, Content = result, IsError = error,
                Images = image is null ? null : [new ImagePart { MediaType = "image/png", Data = image }],
            });
        }

        /// <summary>The model asks for these calls next: the hook's decision.</summary>
        public async Task<TurnDecision?> Next(params (string Tool, object Args)[] calls) =>
            await Hook.OnAfterModelCallAsync(Turn, new ChatMessage
            {
                Role = MessageRole.Assistant,
                Parts = [.. calls.Select(c => (MessagePart)new ToolCallPart { Id = $"call_{++_id}", Name = c.Tool, Arguments = NetPiJson.ToNode(c.Args)!.ToJsonString() })],
                StopReason = "tool_use",
            });
    }

    private static void Null(object? o, string message = "expected no hint") => Check.True(o is null, message);
    private static void NotNull(object? o, string message = "expected a hint") => Check.True(o is not null, message);

    private static Env Setup()
    {
        var ctx = new FakePluginContext();
        var session = ctx.SessionsFake.CreateSession(new SessionInfo { Title = "t" });
        var run = T.Run(ctx, T.Model(), session);
        var turn = T.Turn(run, [new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "Make the tests pass." }] }]);
        return new Env { Ctx = ctx, Hook = new LoopHook(ctx), Turn = turn };
    }

    public static void Register(TestRunner r)
    {
        r.Add("loops: the same call a third time after the same result gets one hint, once", async () =>
        {
            var e = Setup();
            // durations and extra whitespace differ between runs: still the same result and the same call
            await e.Ran("bash", new { command = "npm test" }, "Tests: 3 failed, 12 passed (1.2 s)");
            Null(await e.Next(("bash", new { command = "npm test" })), "a second run is fine");
            await e.Ran("bash", new { command = "npm test" }, "Tests: 3 failed,  12 passed (1.6 s)");
            var d = await e.Next(("bash", new { command = "npm test" }));
            Check.Equal(TurnAction.Inject, d!.Action);
            Check.Equal("loop", d.NoticeKind);
            Check.Contains(d.Text, "you have run bash `npm test` 2 times with the same result");
            await e.Ran("bash", new { command = "npm test" }, "Tests: 3 failed, 12 passed (1.1 s)");
            Null(await e.Next(("bash", new { command = "npm test" })), "one hint per loop");
            Check.Equal(190, e.Hook.Order);
        });

        r.Add("loops: the same failure retried is named as a failure; changing results are progress", async () =>
        {
            var e = Setup();
            await e.Ran("bash", new { command = "dotnet build" }, "error CS1002: ; expected", error: true);
            await e.Ran("bash", new { command = "dotnet build" }, "error CS1002: ; expected", error: true);
            var d = await e.Next(("bash", new { command = "dotnet build" }));
            Check.Contains(d!.Text, "has failed 2 times with the same error");

            var p = Setup();
            await p.Ran("bash", new { command = "dotnet build" }, "error CS1002: ; expected", error: true);
            await p.Ran("bash", new { command = "dotnet build" }, "error CS0103: The name 'x' does not exist", error: true);
            Null(await p.Next(("bash", new { command = "dotnet build" })), "a different error each time: the agent is fixing things");
            await p.Ran("read", new { path = "a.cs" }, "one");
            await p.Ran("read", new { path = "b.cs" }, "two");
            Null(await p.Next(("read", new { path = "c.cs" })), "different arguments are different calls");
        });

        r.Add("loops: results that differ past their start or only in the image are progress; the same image is not", async () =>
        {
            // two screenshots of the same page: the same text, other pixels
            var e = Setup();
            await e.Ran("browser", new { action = "screenshot" }, "Screenshot of Flights — https://x/", image: "AAAA");
            await e.Ran("browser", new { action = "screenshot" }, "Screenshot of Flights — https://x/", image: "BBBB");
            Null(await e.Next(("browser", new { action = "screenshot" })), "the page changed between the screenshots");
            // two snapshots whose first 400 characters are the same
            var head = "Page: Flights — https://x/\n" + string.Concat(Enumerable.Range(1, 30).Select(i => $"[{i}] [button] Filter {i}\n"));
            var s = Setup();
            await s.Ran("browser", new { action = "snapshot" }, head + "[31] [text] Loading…");
            await s.Ran("browser", new { action = "snapshot" }, head + "[31] [text] SAS 07:05, 1 190 SEK");
            Null(await s.Next(("browser", new { action = "snapshot" })), "results came in below the first 400 characters");
            // really the same, image included
            var same = Setup();
            await same.Ran("browser", new { action = "screenshot" }, "Screenshot of Flights — https://x/", image: "AAAA");
            await same.Ran("browser", new { action = "screenshot" }, "Screenshot of Flights — https://x/", image: "AAAA");
            NotNull(await same.Next(("browser", new { action = "screenshot" })));
        });

        r.Add("loops: an edit and its undo, twice, is back and forth", async () =>
        {
            var e = Setup();
            object there = new { path = "a.cs", oldText = "x", newText = "y" };
            object back = new { path = "a.cs", oldText = "y", newText = "x" };
            await e.Ran("edit", there, "Edited a.cs");
            await e.Ran("edit", back, "Edited a.cs");
            await e.Ran("edit", there, "Edited a.cs");
            var d = await e.Next(("edit", back));
            Check.Contains(d!.Text, "going back and forth");
        });

        r.Add("loops: switched off, a hint budget of 0, or a turn without tool calls: no hint", async () =>
        {
            var e = Setup();
            await e.Ran("bash", new { command = "ls" }, "a");
            await e.Ran("bash", new { command = "ls" }, "a");
            e.Ctx.SettingsFake.Set("loops.enabled", false);
            Null(await e.Next(("bash", new { command = "ls" })));
            e.Ctx.SettingsFake.Set("loops.enabled", true);
            e.Ctx.SettingsFake.Set("loops.maxHintsPerRun", 0);
            Null(await e.Next(("bash", new { command = "ls" })));
            e.Ctx.SettingsFake.Set("loops.maxHintsPerRun", 3);
            Null(await e.Next(), "no tool calls");
            e.Ctx.SettingsFake.Set("loops.repeats", 4);
            Null(await e.Next(("bash", new { command = "ls" })), "a higher repeat count");
            await e.Ran("bash", new { command = "ls" }, "a");
            NotNull(await e.Next(("bash", new { command = "ls" })), "the fourth call after the same result");
        });

        r.Add("loops: a decision model reads suspicious steps; a confident 'stuck' hints, an unsure one does not", async () =>
        {
            async Task<(TurnDecision? D, List<JsonNode?> Asked)> Run(double pStuck, string model, bool suspicious = true)
            {
                var e = Setup();
                var asked = new List<JsonNode?>();
                e.Ctx.RpcFake.Register("decide.ask", (req, ct) =>
                {
                    asked.Add(NetPiJson.ToNode(req.Params));
                    return Task.FromResult<object?>(new JsonObject { ["stuck"] = new JsonObject { ["type"] = "noul", ["noul"] = pStuck } });
                });
                e.Ctx.SettingsFake.Set("loops.model", model);
                // eight bash steps, each a little different, most failing: no exact repeat, but suspicious
                for (var i = 0; i < 8; i++)
                    await e.Ran("bash", new { command = $"npm test -- --grep case{i}" }, suspicious || i < 2 ? $"FAIL case{i}" : "ok", error: suspicious ? i % 2 == 0 : i < 2);
                return (await e.Next(("bash", new { command = "npm test -- --grep case9" })), asked);
            }

            var (d, asked) = await Run(0.93, "qwen3.8-27b");
            Check.Contains(d!.Text, "Loop check (qwen3.8-27b, p = 0.93)");
            Check.Contains(d.Text, "bash ×8; 4 failed");
            Check.Equal(1, asked.Count);
            Check.Equal("qwen3.8-27b", asked[0]!["model"].Str());
            Check.Contains(asked[0]!["state"]!["goal"].Str(), "Make the tests pass.");
            Check.Contains(asked[0]!["state"]!["recent_steps"].Str(), "8. bash {\"command\":\"npm test -- --grep case7\"} → ok: FAIL case7");

            (d, asked) = await Run(0.6, "qwen3.8-27b");
            Null(d, "unsure: no hint");
            Check.Equal(1, asked.Count);

            (d, asked) = await Run(0.99, "");
            Null(d, "no model set: only the deterministic checks");
            Check.Equal(0, asked.Count);

            (d, asked) = await Run(0.99, "qwen3.8-27b", suspicious: false);
            Null(d, "steps that look like progress are not sent");
            Check.Equal(0, asked.Count);
        });
    }
}
