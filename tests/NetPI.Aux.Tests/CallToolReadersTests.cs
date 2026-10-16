using System.Text.Json.Nodes;
using NetPI.Compaction;
using NetPI.Skills;

namespace NetPI.Aux.Tests;

/// <summary>Readers of the transcript see a call made through <c>call_tool</c> as the tool it ran (ToolCalls in Contracts).</summary>
public static class CallToolReadersTests
{
    /// <summary>The call the model makes for a deferred tool: the transcript stores it under call_tool.</summary>
    public static ToolCallPart Wrapped(string id, string tool, string arguments) =>
        T.Call(id, "call_tool", new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(arguments) }.ToJsonString());

    public static void Register(TestRunner r)
    {
        r.Add("call_tool readers: ToolCalls.Effective unwraps objects and JSON strings, leaves other calls alone, names results", () =>
        {
            var (n, a) = ToolCalls.Effective(Wrapped("c1", "todo_write", """{"items":[1]}"""));
            Check.Equal("todo_write", n); Check.Equal("""{"items":[1]}""", a);
            var asString = T.Call("c2", "call_tool", """{"name":" skill ","arguments":"{\"name\":\"x\"}"}""");
            Check.Equal(("skill", """{"name":"x"}"""), ToolCalls.Effective(asString));
            Check.Equal(("skill", "{}"), ToolCalls.Effective(T.Call("c3", "call_tool", """{"name":"skill"}""")));
            var plain = T.Call("c4", "read", """{"path":"a"}""");
            Check.Equal(("read", """{"path":"a"}"""), ToolCalls.Effective(plain));
            foreach (var bad in new[] { "not json", "{}", """{"name":3}""", """{"name":" "}""" })
                Check.Equal("call_tool", ToolCalls.EffectiveName(T.Call("c5", "call_tool", bad)), "an unusable call stays call_tool: " + bad);
            Check.Equal("c1", ToolCalls.Unwrap(Wrapped("c1", "x", "{}"))!.Id, "the outer call's id is kept");
            Check.True(ToolCalls.Unwrap(plain) is null);

            var calls = ToolCalls.Index([T.Assistant("", Wrapped("c1", "todo_write", "{}"), plain)]);
            Check.Equal("todo_write", ToolCalls.EffectiveName(new ToolResultPart { CallId = "c1", Name = "call_tool" }, calls));
            Check.Equal("read", ToolCalls.EffectiveName(new ToolResultPart { CallId = "c4", Name = "read" }, calls));
            Check.Equal("call_tool", ToolCalls.EffectiveName(new ToolResultPart { CallId = "gone", Name = "call_tool" }, calls), "an unknown call keeps the stored name");
        });

        r.Add("call_tool readers: compaction file lists and transcript read the inner tool", () =>
        {
            var msgs = new List<ChatMessage>
            {
                T.Assistant("", Wrapped("c1", "write", """{"path":"w.cs","content":"x"}"""), Wrapped("c2", "edit", """{"path":"e.cs","oldText":"a","newText":"b"}""")),
                T.Assistant("", Wrapped("c3", "edit", """{"files":[{"path":"m1.cs","edits":[]}]}"""), Wrapped("c4", "read", """{"path":"r.cs"}""")),
                T.Assistant("", Wrapped("c5", "ssh", """{"action":"edit","host":"srv","path":"/a"}""")),
            };
            var (read, modified) = FileLists.Collect(msgs, null);
            Check.Equal("r.cs", string.Join(",", read));
            Check.Equal("e.cs,m1.cs,srv:/a,w.cs", string.Join(",", modified));

            var a = T.Assistant("", Wrapped("c1", "skill", """{"name":"x"}"""));
            Check.Equal("[Assistant]\n[Tool call] skill({\"name\":\"x\"})", TranscriptSerializer.Serialize(a));
            var tool = new ChatMessage { Role = MessageRole.Tool, Parts = [new ToolResultPart { CallId = "c1", Name = "call_tool", Content = "ok" }] };
            Check.Contains(TranscriptSerializer.Serialize(tool, ToolCalls.Index([a, tool])), "[Tool result: skill]");
        });

        r.Add("call_tool readers: a skill loaded through call_tool counts as loaded", () =>
        {
            var call = Wrapped("c1", "skill", """{"name":"review"}""");
            var result = new ToolResultPart { CallId = "c1", Name = "call_tool", Content = "…", Details = new JsonObject { ["name"] = "review" } };
            var msgs = new List<ChatMessage> { T.Assistant("", call), new() { Role = MessageRole.Tool, Parts = [result] } };
            Check.Equal("review", string.Join(",", SkillNotices.Loaded(msgs)));
        });
    }
}
