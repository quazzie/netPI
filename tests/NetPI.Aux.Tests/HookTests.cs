using System.Text.Json.Nodes;
using NetPI.Nudge;
using NetPI.ToolRepair;

namespace NetPI.Aux.Tests;

public static class NudgeTests
{
    private static (NudgeHook Hook, AgentTurnContext Turn, FakePluginContext Ctx) Setup(int toolCalls = 0)
    {
        var ctx = new FakePluginContext();
        var session = ctx.SessionsFake.CreateSession(new SessionInfo { Title = "t" });
        var run = T.Run(ctx, T.Model(), session);
        run.ToolCallCount = toolCalls;
        return (new NudgeHook(() => ctx.Settings), T.Turn(run), ctx);
    }

    private static async Task<TurnDecision?> After(NudgeHook hook, AgentTurnContext turn, ChatMessage msg) =>
        await hook.OnAfterModelCallAsync(turn, msg);

    public static void Register(TestRunner r)
    {
        r.Add("nudge: empty response → inject nudge notice", async () =>
        {
            var (hook, turn, _) = Setup();
            var d = await After(hook, turn, new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = "  " }], StopReason = "stop" });
            Check.Equal(TurnAction.Inject, d!.Action);
            Check.Equal("nudge", d.NoticeKind);
            Check.Contains(d.Text, "empty");
            Check.Equal(200, hook.Order);
        });

        r.Add("nudge: only thinking (cut off while thinking) → nudge", async () =>
        {
            var (hook, turn, _) = Setup();
            var msg = new ChatMessage { Role = MessageRole.Assistant, Parts = [new ThinkingPart { Text = "I should read the file" }], StopReason = "stop" };
            var d = await After(hook, turn, msg);
            Check.Contains(d!.Text, "still thinking");
            Check.Contains(d.Text, "if you were about to call a tool, call it now");
        });

        r.Add("nudge: stop reason length → cut-off nudge", async () =>
        {
            var (hook, turn, _) = Setup();
            var d = await After(hook, turn, new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = "Here is the long expl" }], StopReason = "length" });
            Check.Contains(d!.Text, "cut off");
            Check.Contains(d.Text, "Continue exactly where you left off");
        });

        r.Add("nudge: cut off while only thinking → stop deliberating, not continue", async () =>
        {
            var (hook, turn, _) = Setup();
            var d = await After(hook, turn, new ChatMessage { Role = MessageRole.Assistant, Parts = [new ThinkingPart { Text = new string('t', 60_000) }], StopReason = "length" });
            Check.Contains(d!.Text, "whole output budget thinking");
            Check.Contains(d.Text, "smallest next step");
            Check.NotContains(d.Text, "Continue exactly where you left off", "continuing would mean thinking more");
            Check.Equal(NudgeReason.CutOffThinking, NudgeHook.Classify(new ChatMessage { Role = MessageRole.Assistant, Parts = [new ThinkingPart { Redacted = "x" }], StopReason = "length" }, 0));
        });

        r.Add("nudge: cut off while writing a tool call as text → make it smaller, it did not run", async () =>
        {
            var (hook, turn, _) = Setup();
            var d = await After(hook, turn, new ChatMessage
            {
                Role = MessageRole.Assistant, StopReason = "length",
                Parts = [new ThinkingPart { Text = "write motion.cpp" }, new TextPart { Text = "Now the core: <tool_call> <function=write> <parameter=content> // Motion controllers…" }],
            });
            Check.Contains(d!.Text, "NOT executed");
            Check.Contains(d.Text, "write a large file in parts");
        });

        r.Add("nudge: announced action without a tool call (only after tools were used)", async () =>
        {
            var announce = new[]
            {
                "I found the bug. Now let me check the tests:",
                "Let me run the build",
                "I'll update the config file now…",
                "Next, I'll fix the parser",
                "Let me check the file...",
                "Now I’ll look at the other usages",
                "The following files need changes:",
            };
            foreach (var text in announce)
            {
                var (hook, turn, _) = Setup(toolCalls: 3);
                var d = await After(hook, turn, T.Assistant(text));
                Check.True(d is { Action: TurnAction.Inject }, $"should nudge: {text}");
                Check.Contains(d!.Text, "without calling a tool");
                // Without prior tool use it is a normal answer.
                var (hook2, turn2, _) = Setup(toolCalls: 0);
                Check.True(await After(hook2, turn2, T.Assistant(text)) is null, $"no tools yet: {text}");
            }
            var fine = new[]
            {
                "Done. All 42 tests pass.",
                "Let me know if you need anything else",
                "I'll leave the rest to you.",
                "Should I also update the docs?",
                "The fix is in place and the build is green!",
            };
            foreach (var text in fine)
            {
                var (hook, turn, _) = Setup(toolCalls: 3);
                Check.True(await After(hook, turn, T.Assistant(text)) is null, $"should not nudge: {text}");
            }
        });

        r.Add("nudge: textual tool-call markup → nudge", async () =>
        {
            var (hook, turn, _) = Setup();
            var d = await After(hook, turn, T.Assistant("<tool_call>\n<function=unknown_tool>\n</function>\n</tool_call>"));
            Check.Contains(d!.Text, "NOT executed");
            var (hook2, turn2, _) = Setup();
            Check.True(await After(hook2, turn2, T.Assistant("<function=read><parameter=path>a</parameter></function>")) is not null);
        });

        r.Add("nudge: tool calls, aborted and error responses are left alone", async () =>
        {
            var (hook, turn, _) = Setup(toolCalls: 2);
            Check.True(await After(hook, turn, T.Assistant("Let me read it:", T.Call("c1", "read"))) is null);
            Check.True(await After(hook, turn, new ChatMessage { Role = MessageRole.Assistant, StopReason = "aborted" }) is null);
            Check.True(await After(hook, turn, new ChatMessage { Role = MessageRole.Assistant, StopReason = "error" }) is null);
            Check.True(await After(hook, turn, T.Assistant("All done.")) is null);
        });

        r.Add("nudge: at most nudge.maxPerRun consecutive nudges", async () =>
        {
            var (hook, turn, ctx) = Setup();
            var empty = new ChatMessage { Role = MessageRole.Assistant, StopReason = "stop" };
            for (var i = 0; i < 3; i++) Check.True(await After(hook, turn, empty) is not null, $"nudge {i + 1}");
            Check.True(await After(hook, turn, empty) is null, "4th nudge must be suppressed");
            Check.Equal(3, (int)turn.Run.Items[NudgeHook.CountKey]!);
            Check.Equal(0, ctx.Bus.Events.Count); // publishes nothing itself

            var (hook2, turn2, ctx2) = Setup();
            ctx2.SettingsFake.Set("nudge.maxPerRun", 1);
            Check.True(await After(hook2, turn2, empty) is not null);
            Check.True(await After(hook2, turn2, empty) is null);
        });

        r.Add("nudge: the cap bounds one stall episode, not the run — a response that needs no nudge resets it", async () =>
        {
            var (hook, turn, _) = Setup();
            var empty = new ChatMessage { Role = MessageRole.Assistant, StopReason = "stop" };

            // Stall: three nudges in a row, then the fourth is suppressed.
            for (var i = 0; i < 3; i++) Check.True(await After(hook, turn, empty) is not null, $"nudge {i + 1}");
            Check.True(await After(hook, turn, empty) is null, "4th consecutive nudge is suppressed");

            // The agent recovers: a real tool call. The counter is gone, not left at 3.
            Check.True(await After(hook, turn, T.Assistant("Reading it.", T.Call("c1", "read"))) is null);
            Check.True(!turn.Run.Items.ContainsKey(NudgeHook.CountKey), "a tool call resets the counter");

            // A later stall gets its own budget: three more nudges, then suppressed again.
            for (var i = 0; i < 3; i++) Check.True(await After(hook, turn, empty) is not null, $"second episode nudge {i + 1}");
            Check.Equal(3, (int)turn.Run.Items[NudgeHook.CountKey]!);
            Check.True(await After(hook, turn, empty) is null, "the second episode is capped too");
        });

        r.Add("nudge: a clean final answer resets the counter too, and so do aborted and errored turns", async () =>
        {
            var empty = new ChatMessage { Role = MessageRole.Assistant, StopReason = "stop" };
            foreach (var ok in new[]
            {
                T.Assistant("All done — the tests pass."),
                new ChatMessage { Role = MessageRole.Assistant, StopReason = "aborted" },
                new ChatMessage { Role = MessageRole.Assistant, StopReason = "error" },
            })
            {
                var (hook, turn, _) = Setup();
                Check.True(await After(hook, turn, empty) is not null, "first nudge");
                Check.True(await After(hook, turn, empty) is not null, "second nudge");
                Check.Equal(2, (int)turn.Run.Items[NudgeHook.CountKey]!);

                Check.True(await After(hook, turn, ok) is null);
                Check.True(!turn.Run.Items.ContainsKey(NudgeHook.CountKey), $"counter reset by stop reason {ok.StopReason}");
                Check.True(await After(hook, turn, empty) is not null, "and the next stall is nudged again");
            }
        });

        r.Add("nudge: maxPerRun=1 still lets a recovered run stall once more", async () =>
        {
            var (hook, turn, ctx) = Setup();
            ctx.SettingsFake.Set("nudge.maxPerRun", 1);
            var empty = new ChatMessage { Role = MessageRole.Assistant, StopReason = "stop" };
            Check.True(await After(hook, turn, empty) is not null, "nudge");
            Check.True(await After(hook, turn, empty) is null, "suppressed");
            Check.True(await After(hook, turn, T.Assistant("Working on it.", T.Call("c1", "read"))) is null, "tool call");
            Check.True(await After(hook, turn, empty) is not null, "nudged again after the recovery");
        });

        r.Add("nudge: nudge.enabled=false disables it", async () =>
        {
            var (hook, turn, ctx) = Setup();
            ctx.SettingsFake.Set("nudge.enabled", false);
            Check.True(await After(hook, turn, new ChatMessage { Role = MessageRole.Assistant, StopReason = "stop" }) is null);
        });

        r.Add("nudge: plugin registers the hook", async () =>
        {
            var ctx = new FakePluginContext();
            await new NudgePlugin().StartAsync(ctx, CancellationToken.None);
            Check.True(ctx.Services.GetAll<IAgentHook>().Single() is NudgeHook);
        });
    }
}

public static class ToolRepairTests
{
    private static readonly List<ToolDefinition> Tools =
    [
        T.Tool("read", new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" },
            ["offset"] = new JsonObject { ["type"] = "integer" },
            ["limit"] = new JsonObject { ["type"] = new JsonArray("integer", "null") },
        }),
        T.Tool("bash", new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string" },
            ["timeout"] = new JsonObject { ["type"] = "number" },
            ["background"] = new JsonObject { ["type"] = "boolean" },
        }),
        T.Tool("edit", new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" },
            ["edits"] = new JsonObject { ["type"] = "array" },
            ["options"] = new JsonObject { ["type"] = "object" },
            ["mode"] = new JsonObject { ["enum"] = new JsonArray("a", "b") },
        }),
        T.Tool("agent_choices"),
        T.Tool("write", new JsonObject { ["path"] = new JsonObject { ["type"] = "string" }, ["content"] = new JsonObject { ["type"] = "string" } }),
    ];

    private static ChatMessage Repair(string text, List<ToolDefinition>? tools = null)
    {
        var msg = T.Assistant(text);
        msg.Id = 7; msg.Seq = 12; msg.SessionId = "ses_x";
        return ToolCallRepair.Repair(msg, tools ?? Tools) ?? throw new AssertException("expected a repair for: " + Check.Show(text));
    }

    private static JsonObject Args(ToolCallPart c) => (JsonObject)JsonNode.Parse(c.Arguments)!;

    public static void Register(TestRunner r)
    {
        r.Add("toolrepair: Qwen block without parameters", () =>
        {
            var m = Repair("<tool_call>\n<function=agent_choices>\n</function>\n</tool_call>");
            var call = m.ToolCalls.Single();
            Check.Equal("agent_choices", call.Name);
            Check.Equal("{}", call.Arguments);
            Check.True(call.Id.StartsWith("call_"));
            Check.Equal("tool_use", m.StopReason);
            Check.Equal(true, (bool)m.Meta!["repaired"]!);
            Check.Equal("", m.Text);
            Check.Equal(7L, m.Id);
            Check.Equal(12L, m.Seq);
            Check.Equal("ses_x", m.SessionId);
        });

        r.Add("toolrepair: Qwen inline parameters", () =>
        {
            var m = Repair("<tool_call><function=read><parameter=path>src/a.cs</parameter></function></tool_call>");
            Check.Equal("src/a.cs", Args(m.ToolCalls.Single())["path"].Str());
        });

        r.Add("toolrepair: multi-line values trim one leading/trailing newline (LF and CRLF)", () =>
        {
            var m = Repair("<tool_call>\n<function=write>\n<parameter=path>\nx.txt\n</parameter>\n<parameter=content>\n\n  line1\nline2\n\n</parameter>\n</function>\n</tool_call>");
            var a = Args(m.ToolCalls.Single());
            Check.Equal("x.txt", a["path"].Str());
            Check.Equal("\n  line1\nline2\n", a["content"].Str());
            var m2 = Repair("<tool_call>\r\n<function=read>\r\n<parameter=path>\r\nsrc/b.cs\r\n</parameter>\r\n</function>\r\n</tool_call>");
            Check.Equal("src/b.cs", Args(m2.ToolCalls.Single())["path"].Str());
        });

        r.Add("toolrepair: Hermes JSON (object, string arguments, parameters, array)", () =>
        {
            var m = Repair("<tool_call>{\"name\":\"read\",\"arguments\":{\"path\":\"x\",\"offset\":5}}</tool_call>");
            var a = Args(m.ToolCalls.Single());
            Check.Equal("x", a["path"].Str());
            Check.Equal(5, (int)a["offset"]!);

            var m2 = Repair("<tool_call>\n{\"name\": \"read\", \"arguments\": \"{\\\"path\\\": \\\"y\\\"}\"}\n</tool_call>");
            Check.Equal("y", Args(m2.ToolCalls.Single())["path"].Str());

            var m3 = Repair("<tool_call>{\"name\":\"bash\",\"parameters\":{\"command\":\"ls\"}}</tool_call>");
            Check.Equal("ls", Args(m3.ToolCalls.Single())["command"].Str());

            var m4 = Repair("<tool_call>[{\"name\":\"read\",\"arguments\":{\"path\":\"a\"}},{\"name\":\"read\",\"arguments\":{\"path\":\"b\"}}]</tool_call>");
            Check.Equal(2, m4.ToolCalls.Count());
            Check.Equal("b", Args(m4.ToolCalls.Last())["path"].Str());

            var m5 = Repair("<tool_call>\n```json\n{\"function\": {\"name\": \"agent_choices\", \"arguments\": {}}}\n```\n</tool_call>");
            Check.Equal("agent_choices", m5.ToolCalls.Single().Name);
        });

        r.Add("toolrepair: bare <function=…> and name=\"…\" variants", () =>
        {
            var m = Repair("<function=bash>\n<parameter=command>\nls -la\n</parameter>\n</function>");
            Check.Equal("ls -la", Args(m.ToolCalls.Single())["command"].Str());
            Check.Equal("", m.Text);

            var m2 = Repair("<function name=\"read\"><parameter name=\"path\">q.txt</parameter></function>");
            Check.Equal("q.txt", Args(m2.ToolCalls.Single())["path"].Str());

            var m3 = Repair("<function=read>{\"path\": \"j.txt\", \"limit\": 3}</function>");
            var a3 = Args(m3.ToolCalls.Single());
            Check.Equal("j.txt", a3["path"].Str());
            Check.Equal(3, (int)a3["limit"]!);

            // A stray </tool_call> after a bare function is removed too.
            var m4 = Repair("<function=agent_choices>\n</function>\n</tool_call>");
            Check.Equal("", m4.Text);
        });

        r.Add("toolrepair: missing closing tags at the end of the message", () =>
        {
            var m = Repair("<tool_call>\n<function=read>\n<parameter=path>\nsrc/a.cs\n");
            Check.Equal("src/a.cs", Args(m.ToolCalls.Single())["path"].Str());
            Check.Equal("", m.Text);

            var m2 = Repair("<tool_call>\n<function=read>\n<parameter=path>\nsrc/a.cs\n</parameter>\n");
            Check.Equal("src/a.cs", Args(m2.ToolCalls.Single())["path"].Str());

            // Unclosed parameter followed by the next parameter.
            var m3 = Repair("<tool_call><function=read><parameter=path>a.txt\n<parameter=offset>10</parameter></function></tool_call>");
            var a3 = Args(m3.ToolCalls.Single());
            Check.Equal("a.txt", a3["path"].Str());
            Check.Equal(10, (int)a3["offset"]!);

            // Unclosed block followed by another block.
            var m4 = Repair("<tool_call>\n<function=read>\n<parameter=path>\na\n</parameter>\n</function>\n<tool_call>\n<function=read>\n<parameter=path>\nb\n</parameter>\n</function>\n</tool_call>");
            Check.Equal(2, m4.ToolCalls.Count());
            Check.Equal("a", Args(m4.ToolCalls.First())["path"].Str());
            Check.Equal("b", Args(m4.ToolCalls.Last())["path"].Str());
        });

        r.Add("toolrepair: several calls in one envelope are all converted", () =>
        {
            var text = "<tool_call>\n<function=read>\n<parameter=path>\na.cs\n</parameter>\n</function>\n</tool_call>\n" +
                       "<tool_call>\n<function=read>\n<parameter=path>\nb.cs\n</parameter>\n</function>\n</tool_call>";
            var m = Repair(text);
            Check.Equal(2, m.ToolCalls.Count());
            Check.Equal("", m.Text);
            Check.True(m.ToolCalls.Select(c => c.Id).Distinct().Count() == 2, "unique ids");
            // The markup is gone, so the message is nothing but the two calls.
            Check.True(m.Parts.All(p => p is ToolCallPart));
        });

        r.Add("toolrepair: parameter values may contain </function> and markup-like text", () =>
        {
            var m = Repair("<tool_call><function=write><parameter=path>x.xml</parameter><parameter=content><a></function></a></parameter></function></tool_call>");
            var a = Args(m.ToolCalls.Single());
            Check.Equal("<a></function></a>", a["content"].Str());
        });

        r.Add("toolrepair: type coercion from the schema", () =>
        {
            var m = Repair("<function=read><parameter=path>007</parameter><parameter=offset> 30 </parameter><parameter=limit>2.0</parameter></function>");
            var a = Args(m.ToolCalls.Single());
            Check.Equal("007", a["path"].Str());
            Check.Equal("30", a["offset"]!.ToJsonString());
            Check.Equal("2", a["limit"]!.ToJsonString());

            var b = Args(Repair("<function=bash><parameter=command>true</parameter><parameter=timeout>1.5</parameter><parameter=background>TRUE</parameter></function>").ToolCalls.Single());
            Check.Equal("true", b["command"].Str()); // string stays a string
            Check.Equal("1.5", b["timeout"]!.ToJsonString());
            Check.Equal("true", b["background"]!.ToJsonString());

            var e = Args(Repair("<function=edit><parameter=edits>[{\"oldText\":\"a\",\"newText\":\"b\"}]</parameter><parameter=options>{\"x\":1}</parameter><parameter=mode>a</parameter><parameter=extra>42</parameter></function>").ToolCalls.Single());
            Check.True(e["edits"] is JsonArray { Count: 1 });
            Check.True(e["options"] is JsonObject);
            Check.Equal("a", e["mode"].Str());
            Check.Equal("42", e["extra"].Str()); // unknown property: kept as string

            var bad = Args(Repair("<function=read><parameter=path>p</parameter><parameter=offset>abc</parameter></function>").ToolCalls.Single());
            Check.Equal("abc", bad["offset"].Str()); // not coercible: left for the tool to report

            Check.Equal("null", ToolCallRepair.Coerce("null", new JsonObject { ["type"] = new JsonArray("array", "null") })?.ToJsonString() ?? "null");
            Check.True(ToolCallRepair.Coerce("[1]", new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "array" }) }) is JsonArray);
        });

        r.Add("toolrepair: name mapping (dots, case, namespaces) and unknown tools", () =>
        {
            Check.Equal("agent_choices", Repair("<tool_call>\n<function=agent.choices>\n</function>\n</tool_call>").ToolCalls.Single().Name);
            Check.Equal("read", Repair("<function=READ><parameter=path>a</parameter></function>").ToolCalls.Single().Name);
            Check.Equal("read", Repair("<tool_call>{\"name\":\"functions.read\",\"arguments\":{\"path\":\"a\"}}</tool_call>").ToolCalls.Single().Name);

            // Only unknown tools → nothing to repair.
            Check.True(ToolCallRepair.Repair(T.Assistant("<tool_call>\n<function=fly_to_moon>\n</function>\n</tool_call>"), Tools) is null);

            // Mixed: the known call is converted, the unknown markup stays in the text.
            var m = Repair("<tool_call><function=fly_to_moon></function></tool_call>\n<tool_call><function=read><parameter=path>a</parameter></function></tool_call>");
            Check.Equal("read", m.ToolCalls.Single().Name);
            Check.Contains(m.Text, "fly_to_moon");
        });

        r.Add("toolrepair: nothing to do for native calls, aborted/error, plain text", () =>
        {
            Check.True(ToolCallRepair.Repair(T.Assistant("<function=read></function>", T.Call("c1", "read")), Tools) is null);
            var aborted = T.Assistant("<function=read><parameter=path>a</parameter></function>");
            aborted.StopReason = "aborted";
            Check.True(ToolCallRepair.Repair(aborted, Tools) is null);
            Check.True(ToolCallRepair.Repair(T.Assistant("The function returns <T> values."), Tools) is null);
            Check.Equal(0, ToolCallTextParser.Parse("nothing here").Count);
        });

        r.Add("toolrepair: a whole-message fence is an envelope, thinking and usage are kept", () =>
        {
            var msg = new ChatMessage
            {
                Role = MessageRole.Assistant, StopReason = "stop", Meta = new JsonObject { ["kind"] = "x" },
                Parts =
                [
                    new ThinkingPart { Text = "need to read", Signature = "sig" },
                    new TextPart { Text = "```xml\n<tool_call>\n<function=read>\n<parameter=path>\na\n</parameter>\n</function>\n</tool_call>\n```" },
                ],
                Usage = new Usage { InputTokens = 10, OutputTokens = 5 },
            };
            var m = ToolCallRepair.Repair(msg, Tools)!;
            Check.True(m.Parts[0] is ThinkingPart { Signature: "sig" });
            Check.Equal("read", m.ToolCalls.Single().Name);
            Check.Equal("", m.Text); // the emptied fence disappears with the markup
            Check.Equal("x", m.MetaString("kind"));
            Check.Equal(10L, m.Usage!.InputTokens);
            Check.True(msg.Meta!["repaired"] is null, "original message untouched");
        });

        r.Add("toolrepair: documentation examples are not executed", () =>
        {
            // A normal answer that shows a call as an example, in a fence.
            var doc = "Here is an example; do not run it:\n\n```xml\n<tool_call>\n<function=write>\n" +
                      "<parameter=path>\nx.txt\n</parameter>\n<parameter=content>\nhello\n</parameter>\n</function>\n</tool_call>\n```";
            Check.True(ToolCallRepair.Repair(T.Assistant(doc), Tools) is null, "example inside a fence");

            // Quoted source: prose that quotes the markup, with a destructive call in it.
            var quoted = "The repair hook recognises markup like this:\n<tool_call><function=bash><parameter=command>rm -rf /</parameter></function></tool_call>\nThat is the whole rule.";
            Check.True(ToolCallRepair.Repair(T.Assistant(quoted), Tools) is null, "quoted source");

            // Prose and markup in the same answer: the explanation wins, nothing is executed.
            var both = "I will write the file now.\n<function=write><parameter=path>x.txt</parameter><parameter=content>hi</parameter></function>";
            Check.True(ToolCallRepair.Repair(T.Assistant(both), Tools) is null, "prose plus markup");

            // A complete envelope followed by a comment is still an explanation.
            var tail = "<tool_call><function=read><parameter=path>a</parameter></function></tool_call>\n(as shown in the docs)";
            Check.True(ToolCallRepair.Repair(T.Assistant(tail), Tools) is null, "trailing comment");

            // A plain prose part next to a call part is an answer, not a call.
            var parts = new ChatMessage
            {
                Role = MessageRole.Assistant,
                Parts = [new TextPart { Text = "Reading the file." }, new TextPart { Text = "<function=read><parameter=path>a</parameter></function>" }],
            };
            Check.True(ToolCallRepair.Repair(parts, Tools) is null, "prose part next to a call part");
        });

        r.Add("toolrepair: a call envelope is markup only, prose is not", () =>
        {
            Check.True(ToolCallTextParser.IsCallEnvelope("<function=read><parameter=path>a</parameter></function>"));
            Check.True(ToolCallTextParser.IsCallEnvelope("```xml\n<tool_call>\n<function=read>\n</function>\n</tool_call>\n```"));
            Check.True(ToolCallTextParser.IsCallEnvelope("<function=a></function>\n\n<function=b></function>"));
            Check.True(!ToolCallTextParser.IsCallEnvelope("Run this:\n<function=read></function>"));
            Check.True(!ToolCallTextParser.IsCallEnvelope("no markup at all"));
            Check.True(!ToolCallTextParser.IsCallEnvelope("```xml\n<function=read></function>\n```\nand then it writes the file"));
            Check.True(!ToolCallTextParser.IsCallEnvelope("```xml\n```"));
        });

        r.Add("toolrepair: parser spans cover the whole markup", () =>
        {
            var text = "A <tool_call><function=read><parameter=path>x</parameter></function></tool_call> B";
            var calls = ToolCallTextParser.Parse(text);
            Check.Equal(1, calls.Count);
            Check.Equal("<tool_call><function=read><parameter=path>x</parameter></function></tool_call>", text[calls[0].Start..calls[0].End]);
            Check.Equal("A  B", ToolCallTextParser.RemoveSpans(text, [(calls[0].Start, calls[0].End)]));
        });

        r.Add("toolrepair: hook returns Replace, respects toolRepair.enabled, runs before nudge", async () =>
        {
            var ctx = new FakePluginContext();
            await new ToolRepairPlugin().StartAsync(ctx, CancellationToken.None);
            var hook = ctx.Services.GetAll<IAgentHook>().Single();
            Check.Equal(100, hook.Order);
            Check.True(hook.Order < new NudgeHook(() => null).Order);
            var session = ctx.SessionsFake.CreateSession(new SessionInfo());
            var turn = T.Turn(T.Run(ctx, T.Model(), session), tools: Tools);
            var d = await hook.OnAfterModelCallAsync(turn, T.Assistant("<function=agent_choices></function>"));
            Check.Equal(TurnAction.Replace, d!.Action);
            Check.Equal("agent_choices", d.Replacement!.ToolCalls.Single().Name);
            ctx.SettingsFake.Set("toolRepair.enabled", false);
            Check.True(await hook.OnAfterModelCallAsync(turn, T.Assistant("<function=agent_choices></function>")) is null);
        });
    }
}
