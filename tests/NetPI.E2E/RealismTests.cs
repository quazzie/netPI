using System.Text.Json;

namespace NetPI.E2E;

/// <summary>Things real backends do: cached-token usage, think tags in content, broken tool arguments, empty answers, in-stream errors.</summary>
public static class RealismTests
{
    private static readonly (string Model, string Api)[] Apis =
    [
        (CoreTests.Qwen, "responses"),
        ("aiproxy/gemma-4", "chat"),
        ("anthropic/claude-sonnet-4-5", "anthropic"),
    ];

    private static async Task WithChatGemma(Env env, Func<Task> body)
    {
        await env.Rpc("settings.set", new { path = "providers.aiproxy.models.gemma-4", value = new { transport = "chat" } });
        try { await body(); }
        finally { await env.Rpc("settings.set", new { path = "providers.aiproxy.models.gemma-4", value = (object?)null }); }
    }

    public static void Register(TestRunner r, Env env)
    {
        r.Add("usage: cached prompt tokens are mapped per API; context meter = prompt + completion; agent totals add up", () => WithChatGemma(env, async () =>
        {
            foreach (var (model, api) in Apis)
            {
                var p = await env.NewProject("usage-" + api, CoreTests.Seed);
                var s = await env.NewSession(model: model, projectId: p.S("id"));
                var sid = s.S("id")!;
                var mockMark = await env.MockMark();
                var run = await env.Run(sid, "edit [s:tools file=notes.txt old=beta new=BETA-USAGE]");
                Check.Contains(run.FinalText, "TOOLS-DONE");
                var log = (await env.MockLog(mockMark)).Where(e => e.S("api") == api).ToList();
                Check.Equal(4, log.Count, api + ": 4 model calls");
                var assistants = run.Role("assistant").ToList();
                Check.Equal(log.Count, assistants.Count);
                for (var i = 0; i < log.Count; i++)
                {
                    var u = assistants[i].P("usage");
                    var e = log[i];
                    Check.Equal(e.L("inputTokens"), u.L("inputTokens") + u.L("cacheReadTokens") + u.L("cacheWriteTokens"), $"{api} call {i}: whole prompt");
                    Check.Equal(e.L("cachedTokens"), u.L("cacheReadTokens"), $"{api} call {i}: cached part");
                    Check.Equal(e.L("outputTokens"), u.L("outputTokens"), $"{api} call {i}: completion");
                }
                Check.True(log.Skip(1).All(e => e.L("cachedTokens") > 0), "follow-up calls report cache hits");
                // the UI pairs the streamed tool call with its tool.start/tool.end rows by callId
                var streamedIds = run.OfType("stream.tool").Select(e => e.D.S("callId")).ToList();
                var startedIds = run.OfType("tool.start").Select(e => e.D.S("callId")).ToList();
                Check.Equal(string.Join(",", startedIds), string.Join(",", streamedIds), api + ": stream.tool ids = tool.start ids");
                Check.Equal(string.Join(",", run.Parts("tool_call").Select(c => c.S("id"))), string.Join(",", startedIds), api + ": persisted call ids");
                var ctx = run.OfType("session.context").Last();
                Check.Equal(log[^1].L("inputTokens") + log[^1].L("outputTokens"), ctx.D.L("used"), api + ": context meter");
                var session = await env.Rpc("sessions.get", new { id = sid });
                Check.Equal(ctx.D.L("used"), session.L("contextTokens"));
                Check.Equal(log.Sum(e => e.L("inputTokens")), run.Final.L("inputTokens"), api + ": agent input total (cached included)");
                Check.Equal(log.Sum(e => e.L("outputTokens")), run.Final.L("outputTokens"), api + ": agent output total");
            }
        }));

        r.Add("think tags: <think>…</think> in the content becomes a thinking part (chat and responses)", () => WithChatGemma(env, async () =>
        {
            foreach (var model in new[] { "aiproxy/gemma-4", CoreTests.Qwen })
            {
                var s = await env.NewSession(model: model);
                var run = await env.Run(s.S("id")!, "think out loud [s:thinktags]");
                Check.Equal("Answer after the think block. THINKTAGS-DONE", run.FinalText, model + ": text without the think block");
                var thinking = run.Parts("thinking").ToList();
                Check.True(thinking.Any(t => (t.S("text") ?? "").Contains("reason inside think tags")), model + ": thinking extracted");
                var streamed = string.Concat(run.OfType("stream.delta").Where(e => e.D.S("kind") == "text").Select(e => e.D.S("text")));
                Check.NotContains(streamed, "<think>", model + ": no raw think tags streamed as text");
            }
        }));

        r.Add("broken tool calls: invalid JSON arguments and an unknown tool are reported back and the model recovers", async () =>
        {
            var p = await env.NewProject("badargs", CoreTests.Seed);
            var s = await env.NewSession(projectId: p.S("id"));
            var run = await env.Run(s.S("id")!, "read it [s:badargs file=notes.txt]");
            var results = run.Parts("tool_result").ToList();
            Check.Equal(3, results.Count);
            Check.True(results[0].B("isError"));
            Check.Contains(results[0].S("content"), "Invalid JSON arguments for read");
            Check.True(results[1].B("isError"));
            Check.Contains(results[1].S("content"), "Unknown tool 'read_file'");
            Check.Contains(results[1].S("content"), "read");
            Check.False(results[2].B("isError"));
            Check.Contains(run.FinalText, "BADARGS-DONE");
            Check.Contains(run.FinalText, "alpha line");
        });

        r.Add("empty answer: an empty response is nudged (responses and anthropic)", async () =>
        {
            foreach (var model in new[] { CoreTests.Qwen, "anthropic/claude-sonnet-4-5" })
            {
                var s = await env.NewSession(model: model);
                var mockMark = await env.MockMark();
                var run = await env.Run(s.S("id")!, "say nothing [s:empty]");
                var notice = run.Role("notice").Single();
                Check.Equal("nudge", notice.P("meta").S("kind"));
                Check.Contains(run.FinalText, "NUDGE-RESUMED");
                var log = await env.MockLog(mockMark);
                Check.True(log.All(e => e.L("status") == 200), model + ": the transcript with the empty answer was accepted: " + string.Join(" | ", log.Select(e => e.S("error"))));
            }
        });

        r.Add("in-stream errors: response.failed / error chunk / overloaded_error are retried on all three APIs", () => WithChatGemma(env, async () =>
        {
            foreach (var (model, api) in Apis)
            {
                var s = await env.NewSession(model: model);
                var mockMark = await env.MockMark();
                var run = await env.Run(s.S("id")!, "fail midway [s:midfail]");
                Check.Contains(run.FinalText, "MIDFAIL-RECOVERED", api);
                Check.Equal(1, run.OfType("stream.reset").Count(), api + ": partial text discarded");
                Check.True(run.OfType("agent.notice").Any(e => (e.D.S("text") ?? "").Contains("Retrying")), api + ": retry notice");
                Check.Equal(2, (await env.MockLog(mockMark)).Count(e => e.S("scenario") == "midfail"), api + ": two attempts");
                Check.Equal(2, run.Messages.Count, api + ": only the successful answer is persisted");
            }
        }));
    }
}
