using System.Text.Json.Nodes;
using NetPI.Loops;

namespace NetPI.Agent.Tests;

public static class LoopsTests
{
    public static void Register(TestRunner t)
    {
        t.Add("loops: in a real run the hint lands after the repeated call's result and before the next model call", HintInRun);
        t.Add("loops: the full-conversation check declares its need for the captured conversation; the runtime captures only then", CaptureDeclared);
    }

    /// <summary>
    /// The runtime knows no loops setting by name: it asks the provider for the capture while a registered consumer wants
    /// it, and this plugin's consumer wants it while loops.contextChecks is on — read at every call.
    /// </summary>
    private static async Task CaptureDeclared()
    {
        await using var h = await TestHost.StartAsync();
        var captured = new List<bool>();
        h.Services.Register<IAgentHook>(new CaptureProbe(captured));
        var s = h.NewSession();
        await h.SendAsync(s.Id, "one");
        await h.IdleAsync(s.Id);
        Check.Equal("False", string.Join(",", captured), "no consumer: no capture");
        await h.StartPluginAsync(new LoopsPlugin());
        await h.SendAsync(s.Id, "two");
        await h.IdleAsync(s.Id);
        Check.Equal("False,False", string.Join(",", captured), "the plugin is loaded, the check is off: no capture");
        h.Settings.Set("loops.contextChecks", JsonValue.Create(true));
        await h.SendAsync(s.Id, "three");
        await h.IdleAsync(s.Id);
        Check.Equal("False,False,True", string.Join(",", captured), "the check is on: the runtime asks for the capture");
        h.Settings.Set("loops.contextChecks", JsonValue.Create(false));
        await h.SendAsync(s.Id, "four");
        await h.IdleAsync(s.Id);
        Check.Equal("False,False,True,False", string.Join(",", captured), "switched off again: read at every call");
    }

    /// <summary>What the runtime asked of the provider for each call.</summary>
    private sealed class CaptureProbe(List<bool> captured) : IAgentHook
    {
        public ValueTask<TurnDecision?> OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant)
        {
            lock (captured) captured.Add(turn.SentRequest?.CaptureDecisionContext == true);
            return ValueTask.FromResult<TurnDecision?>(null);
        }
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
