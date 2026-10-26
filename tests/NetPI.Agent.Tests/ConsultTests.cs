using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NetPI.Consult;

namespace NetPI.Agent.Tests;

/// <summary>
/// The consult tool with the real runner and a scripted model: one read-only call of the Claude CLI model
/// (Purpose "consult"), the answer as the tool's result, refusals with their reasons, and the bounded outcomes.
/// </summary>
public static class ConsultTests
{
    public static void Register(TestRunner t)
    {
        t.Add("consult: a chat asks the Claude CLI model, one read-only call, the answer is the tool's result", HappyPath);
        t.Add("consult: a failed call degrades to a note, and there is no second request (no retry loop)", NoRetry);
        t.Add("consult: no claude-cli provider, the tool is not registered; adding one announces it, disabling it takes it back", ProviderGatesTheTool);
        t.Add("consult: a non-owner-started chat (a webhook's) is refused with the terms reason, no model call", UnattendedRefused);
        t.Add("consult: the unattended reasons name what started the chat", UnattendedReasons);
        t.Add("consult: a model that is not on the claude-cli provider is refused with the reason", WrongProviderRefused);
        t.Add("consult: no model found, the tool answers with the CLI models that exist (and says to enable the provider)", ModelNotFoundNote);
        t.Add("consult: a long answer is cut at consult.maxAnswerTokens (4 chars a token), saying so", CutAtMaxAnswerTokens);
        t.Add("consult: a CLI that never answers times out with a note, and the run goes on", ConsultTimeout);
    }

    /// <summary>The consult plugin on a test host, with a Claude CLI model in the catalog and a claude-cli provider configured.</summary>
    private static async Task<TestHost> Start()
    {
        var h = await TestHost.StartAsync(hh =>
        {
            hh.Catalog.AddModel(new ModelInfo { Provider = "claude-cli", Id = "claude-opus-5-5", Status = "available" });
            hh.Settings.SetQuiet("providers.instances", new JsonObject { ["cli"] = new JsonObject { ["type"] = "claude-cli" } });
        });
        await h.StartPluginAsync(new ConsultPlugin());
        return h;
    }

    private static bool IsConsult(ModelRequest r) => r.Purpose == "consult";

    private static ToolResultPart ConsultResult(TestHost h, string sid) =>
        h.Messages(sid).SelectMany(m => m.ToolResults).Single(r => r.Name == "consult");

    private static async Task HappyPath()
    {
        var h = await Start();
        await using var _h = h;
        var s = h.NewSession();
        var consults = new List<ModelRequest>();
        var agentCalls = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsConsult(r)) { lock (consults) consults.Add(r); return Reply.Text("Use one table with a covering index; avoid the join."); }
            return Interlocked.Increment(ref agentCalls) switch
            {
                1 => Reply.Tool("consult", new { brief = "How should we index the orders table?", context = "Postgres 17, 40M rows, read-heavy" }),
                _ => Reply.Text("Done: I asked the consultant."),
            };
        };
        await h.SendAsync(s.Id, "Design the index");
        await h.IdleAsync(s.Id);

        Check.Equal(1, consults.Count, "exactly one consult request");
        var req = consults[0];
        Check.Equal("consult", req.Purpose);
        Check.Equal(0, req.Tools.Count, "none of NetPI's tools");
        Check.True(req.ReadOnlyTools, "the consultant may read the workspace with the CLI's own tools, and only read");
        Check.Contains(req.SystemPrompt!, "about 16000 tokens", "the consultant is told its answer budget");
        Check.Equal("medium", req.ReasoningEffort, "the effort from the setting");
        Check.Equal(1, req.Messages.Count, "the brief is the single user message");
        Check.Contains(req.Messages[0].Text, "How should we index the orders table?");
        Check.Contains(req.Messages[0].Text, "Postgres 17, 40M rows, read-heavy", "the context goes in too");

        var result = ConsultResult(h, s.Id);
        Check.False(result.IsError);
        Check.Contains(result.Content, "Use one table with a covering index");
        Check.Equal("claude-cli/claude-opus-5-5", (string?)result.Details?["model"]);
        Check.Equal(false, (bool)result.Details!["truncated"]!);
        Check.Equal("Done: I asked the consultant.", h.Messages(s.Id)[^1].Text, "the run went on");
    }

    private static async Task NoRetry()
    {
        var h = await Start();
        await using var _h = h;
        var s = h.NewSession();
        var consultCalls = 0;
        var agentCalls = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsConsult(r)) { Interlocked.Increment(ref consultCalls); throw new ModelException("the CLI is not signed in", false); }
            return Interlocked.Increment(ref agentCalls) switch
            {
                1 => Reply.Tool("consult", new { brief = "Design the index" }),
                _ => Reply.Text("The consult failed; I went on without it."),
            };
        };
        await h.SendAsync(s.Id, "Design the index");
        await h.IdleAsync(s.Id);

        Check.Equal(1, consultCalls, "no retry loop: the failed call is not repeated by the tool");
        var result = ConsultResult(h, s.Id);
        Check.True(result.IsError);
        Check.Contains(result.Content, "the CLI is not signed in", "the reason is the result");
        Check.Equal("The consult failed; I went on without it.", h.Messages(s.Id)[^1].Text, "the run went on");
    }

    private static async Task ProviderGatesTheTool()
    {
        var h = await TestHost.StartAsync(hh =>
            hh.Catalog.AddModel(new ModelInfo { Provider = "claude-cli", Id = "claude-opus-5-5", Status = "available" }));
        await using var _h = h;
        await h.StartPluginAsync(new ConsultPlugin());
        Check.True(h.Tools.Get("consult") is null, "no claude-cli provider: the tool is not registered");

        h.Settings.Set("providers.instances", new JsonObject { ["cli"] = new JsonObject { ["type"] = "claude-cli" } });
        await h.Bus.DrainAsync();
        Check.True(h.Tools.Get("consult") is not null, "a claude-cli provider: the tool is registered");
        Check.True(h.Bus.OfType("consult.toolsChanged")
            .Any(e => FakeBus.Data(e)["available"]!.GetValue<bool>() == true && FakeBus.Data(e)["reason"]!.ToString()!.Contains("present and enabled")),
            "and the change is announced on the bus with the cause");

        h.Settings.Set("providers.instances.cli.enabled", false);
        await h.Bus.DrainAsync();
        Check.True(h.Tools.Get("consult") is null, "disabled: the tool goes again");
        Check.True(h.Bus.OfType("consult.toolsChanged")
            .Any(e => FakeBus.Data(e)["available"]!.GetValue<bool>() == false && FakeBus.Data(e)["reason"]!.ToString()!.Contains("disabled")),
            "with the cause");
    }

    private static async Task UnattendedRefused()
    {
        var h = await Start();
        await using var _h = h;
        var s = h.Sessions.CreateSession(new SessionInfo
        {
            Title = "Webhook: Checkmk",
            Meta = new JsonObject { ["webhook"] = "wh_1", ["webhookName"] = "Checkmk" },
        });
        var consultCalls = 0;
        var agentCalls = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsConsult(r)) { Interlocked.Increment(ref consultCalls); return Reply.Text("never"); }
            return Interlocked.Increment(ref agentCalls) switch
            {
                1 => Reply.Tool("consult", new { brief = "Design the index" }),
                _ => Reply.Text("Refused."),
            };
        };
        await h.SendAsync(s.Id, "Design the index");
        await h.IdleAsync(s.Id);

        Check.Equal(0, consultCalls, "no model call for a refused consult");
        var result = ConsultResult(h, s.Id);
        Check.True(result.IsError);
        Check.Contains(result.Content, "webhook Checkmk", "the reason names what started the chat");
        Check.Contains(result.Content, "owner", "and the terms behind it");
    }

    private static Task UnattendedReasons()
    {
        Check.Equal("this chat is a subagent's", ConsultTool.UnattendedReason(new SessionInfo { Kind = "subagent" }));
        Check.Contains(ConsultTool.UnattendedReason(new SessionInfo { Meta = new JsonObject { ["webhook"] = "w1", ["webhookName"] = "Checkmk" } })!, "webhook Checkmk");
        Check.Contains(ConsultTool.UnattendedReason(new SessionInfo { Meta = new JsonObject { ["schedule"] = "s1" } })!, "schedule");
        Check.Contains(ConsultTool.UnattendedReason(new SessionInfo { Meta = new JsonObject { ["quiet"] = true } })!, "script");
        Check.Contains(ConsultTool.UnattendedReason(new SessionInfo { Meta = new JsonObject { ["origin"] = "phone" } })!, "phone");
        Check.True(ConsultTool.UnattendedReason(new SessionInfo { Title = "mine" }) is null, "an owner's chat has no reason");
        return Task.CompletedTask;
    }

    private static async Task WrongProviderRefused()
    {
        var h = await Start();
        await using var _h = h;
        var s = h.NewSession();
        var consultCalls = 0;
        var agentCalls = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsConsult(r)) { Interlocked.Increment(ref consultCalls); return Reply.Text("never"); }
            return Interlocked.Increment(ref agentCalls) switch
            {
                1 => Reply.Tool("consult", new { brief = "Design the index", model = "fake/local" }),
                _ => Reply.Text("Refused."),
            };
        };
        await h.SendAsync(s.Id, "Design the index");
        await h.IdleAsync(s.Id);

        Check.Equal(0, consultCalls, "the refused model is not asked");
        var result = ConsultResult(h, s.Id);
        Check.True(result.IsError);
        Check.Contains(result.Content, "fake/local", "the model is named");
        Check.Contains(result.Content, "claude-cli", "and the provider that would serve it");
    }

    private static async Task ModelNotFoundNote()
    {
        // The provider entry exists (the tool is registered) but no CLI model is in the catalog.
        var h = await TestHost.StartAsync();
        await using var _h = h;
        h.Settings.SetQuiet("providers.instances", new JsonObject { ["cli"] = new JsonObject { ["type"] = "claude-cli" } });
        await h.StartPluginAsync(new ConsultPlugin());
        Check.True(h.Tools.Get("consult") is not null, "the tool is registered on the provider entry");

        var s = h.NewSession();
        var agentCalls = 0;
        h.Catalog.Handler = (r, ct) => Interlocked.Increment(ref agentCalls) switch
        {
            1 => Reply.Tool("consult", new { brief = "Design the index" }),
            _ => Reply.Text("No model."),
        };
        await h.SendAsync(s.Id, "Design the index");
        await h.IdleAsync(s.Id);

        var result = ConsultResult(h, s.Id);
        Check.False(result.IsError, "a note, not a failure of the run");
        Check.Contains(result.Content, "No Claude CLI model is in the model list");
        Check.Contains(result.Content, "provider", "and it says to enable the provider");
    }

    private static async Task CutAtMaxAnswerTokens()
    {
        var h = await Start();
        await using var _h = h;
        h.Settings.Set("consult.maxAnswerTokens", 50);
        var s = h.NewSession();
        var agentCalls = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsConsult(r)) return Reply.Text(new string('x', 500));
            return Interlocked.Increment(ref agentCalls) switch
            {
                1 => Reply.Tool("consult", new { brief = "Design the index" }),
                _ => Reply.Text("Cut."),
            };
        };
        await h.SendAsync(s.Id, "Design the index");
        await h.IdleAsync(s.Id);

        var result = ConsultResult(h, s.Id);
        Check.False(result.IsError);
        const string cut = "\n…cut at about 50 tokens, ask for less";
        Check.Equal(200, result.Content.Trim().Length - cut.Length, "the answer is cut at the limit: 50 tokens, 200 chars");
        Check.Contains(result.Content, cut.Trim());
        Check.Equal(true, (bool)result.Details!["truncated"]!);
        Check.Equal(200 + cut.Length, (int)result.Details!["chars"]!);
    }

    private static async Task ConsultTimeout()
    {
        var h = await Start();
        await using var _h = h;
        h.Settings.Set("consult.timeoutSeconds", 1);
        var s = h.NewSession();
        var agentCalls = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsConsult(r)) return Hang(ct);
            return Interlocked.Increment(ref agentCalls) switch
            {
                1 => Reply.Tool("consult", new { brief = "Design the index" }),
                _ => Reply.Text("It timed out."),
            };
        };
        await h.SendAsync(s.Id, "Design the index");
        await h.IdleAsync(s.Id);

        var result = ConsultResult(h, s.Id);
        Check.True(result.IsError);
        Check.Contains(result.Content, "did not answer in 1s", "the bounded outcome names the timeout");
        Check.Equal("It timed out.", h.Messages(s.Id)[^1].Text, "the run went on");
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Hang([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }
}
