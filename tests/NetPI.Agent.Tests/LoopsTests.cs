using NetPI.Loops;

namespace NetPI.Agent.Tests;

public static class LoopsTests
{
    public static void Register(TestRunner t)
    {
        t.Add("loops: in a real run the hint lands after the repeated call's result and before the next model call", HintInRun);
    }

    private static async Task HintInRun()
    {
        await using var h = await TestHost.StartAsync();
        await h.StartPluginAsync(new LoopsPlugin());
        var ran = 0;
        h.AddTool(new FakeTool("bash", (ctx, args, ct) =>
        {
            Interlocked.Increment(ref ran);
            return Task.FromResult(ToolResult.Ok("Tests: 3 failed, 12 passed"));
        }));
        var s = h.NewSession();
        var sawHint = false;
        h.Catalog.Handler = (r, ct) =>
        {
            // the model keeps running the same command until it reads the hint
            if (r.Messages.Any(m => m.Text.Contains("Loop check")))
            {
                sawHint = true;
                return Reply.Text("I keep getting the same failures; the cause is in the test data. Stopping to ask you.");
            }
            return Reply.Tool("bash", new { command = "npm test" });
        };
        await h.SendAsync(s.Id, "make the tests pass");
        await h.IdleAsync(s.Id);

        Check.True(sawHint, "the model read the hint");
        Check.Equal(3, ran, "the third run is the one announced, then the model stopped");
        var messages = h.Messages(s.Id);
        var notice = messages.Single(m => m.Role == MessageRole.Notice && m.MetaString("kind") == "loop");
        var i = messages.IndexOf(notice);
        Check.Equal(MessageRole.Tool, messages[i - 1].Role, "after the third call's result");
        Check.Equal(MessageRole.Assistant, messages[i + 1].Role, "before the next model call");
    }
}
