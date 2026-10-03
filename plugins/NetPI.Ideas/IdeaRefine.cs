using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>
/// <c>ideas.refine</c>: task an agent to define an idea better. A new chat on the idea's project (title "Refine idea: …") is
/// created, the idea is attached to it, and the agent is told what to do with it; the chat is the user's to watch, answer
/// in or stop. The agent works through the <c>ideas</c> tool and can read the code, but the chat has every tool that can
/// change something switched off (<see cref="SessionTools"/>) except the ideas tool, the question tool and the todo list:
/// refining an idea is research and writing, never the work the idea describes.
/// </summary>
internal sealed class IdeaRefine(IPluginContext ctx, IdeasRepository repo)
{
    /// <summary>Tools a refining chat keeps although they can change something: it writes the idea, may ask the owner, and keeps a list.</summary>
    internal static readonly string[] KeptTools = ["ideas", "ask_user", "todo_write"];

    public void Register(IRpcRegistry rpc) =>
        rpc.Register("ideas.refine", RefineAsync,
            "Task an agent to define an idea better: { id, hint? (what to focus on), agent? (an agent key; default: any available agent) } → { sessionId, title, agent } — " +
            "a new chat on the idea's project, with the idea attached, in which the agent researches the code and rewrites the idea's summary and sections through the ideas tool; " +
            "everything that can change files or run commands is switched off in it");

    private async Task<object?> RefineAsync(RpcRequest req, CancellationToken ct)
    {
        var id = req.Required("id");
        var idea = repo.Find(id)?.Doc ?? throw new RpcException("not_found", $"Idea {id} not found");
        var runtime = ctx.Services.Get<IAgentRuntime>() ?? throw new RpcException("unavailable", "No agent runtime is running, so no agent can take the task.");
        var agent = req.Str("agent")?.Trim();
        var title = IdeaOps.Str(idea["title"]) ?? id;
        var projectId = IdeaOps.Str(idea["project"]?["id"]);
        if (!string.IsNullOrEmpty(agent) && !string.Equals(agent, SessionAgent.Any, StringComparison.OrdinalIgnoreCase) && !ctx.Rpc.Exists("agents.use"))
            throw new RpcException("unavailable", "Agents cannot be chosen here: the Agents plugin is not running.");

        ct.ThrowIfCancellationRequested();
        SessionInfo session;
        try
        {
            session = ctx.Sessions.CreateSession(new SessionInfo
            {
                Title = Clip($"Refine idea: {title}", 80),
                ProjectId = projectId,
                Meta = new JsonObject { [SessionTools.MetaKey] = new JsonArray([.. ToolsOff().Select(n => (JsonNode)n)]) },
            });
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException("not_found", ex.Message);   // the idea's project is gone: say so instead of a bare 500
        }

        try
        {
            // "any available agent" is the default: whichever agent has a free instance takes the run, and the chat's model follows it
            if (ctx.Rpc.Exists("agents.use"))
                await ctx.Rpc.InvokeAsync("agents.use", new JsonObject { ["sessionId"] = session.Id, ["agent"] = string.IsNullOrEmpty(agent) ? SessionAgent.Any : agent }, ct).ConfigureAwait(false);
            // the idea as a notice in the chat, and the chat on the idea's card under "Chats" (the user opens it from there)
            await ctx.Rpc.InvokeAsync("ideas.attach", new JsonObject { ["sessionId"] = session.Id, ["id"] = id, ["via"] = "refine" }, ct).ConfigureAwait(false);
            repo.AddSessionEntry(id, new JsonObject { ["sessionId"] = session.Id, ["title"] = session.Title, ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["note"] = "Refining this idea" });
            await runtime.SendAsync(session.Id, new UserInput { Text = Prompt(idea, req.Str("hint")) }, DeliveryMode.Auto, ct).ConfigureAwait(false);
        }
        catch
        {
            // nothing started: the chat is not left behind (an empty one is not even listed, one with the notice would be)
            try { ctx.Sessions.DeleteSession(session.Id); } catch { /* already gone */ }
            throw;
        }
        return new JsonObject { ["sessionId"] = session.Id, ["title"] = session.Title, ["agent"] = string.IsNullOrEmpty(agent) ? SessionAgent.Any : agent };
    }

    /// <summary>Every tool that can change something, except the few a refining chat needs: what is left reads the code and the web.</summary>
    private List<string> ToolsOff() =>
        [.. ctx.Tools.All.Where(t => !t.Definition.ReadOnly && !KeptTools.Contains(t.Definition.Name, StringComparer.OrdinalIgnoreCase)).Select(t => t.Definition.Name).Order(StringComparer.Ordinal)];

    /// <summary>What the agent is asked to do. The idea itself reaches it as the notice <c>ideas.attach</c> adds; this is the task.</summary>
    internal static string Prompt(JsonObject idea, string? hint)
    {
        var id = IdeaOps.Str(idea["id"]);
        var sb = new StringBuilder();
        sb.Append("Refine idea `").Append(id).Append("` (“").Append(IdeaOps.Str(idea["title"])).Append("”): define it better. ")
          .Append("The owner filed it quickly and it is attached above. Your job is to make it a clear, checkable piece of work that someone could pick up cold — not to do the work.\n\n");
        if (!string.IsNullOrWhiteSpace(hint)) sb.Append("The owner asks you to focus on: ").Append(hint.Trim()).Append("\n\n");
        sb.Append("1. Get the idea with the ideas tool (action get, id `").Append(id).Append("`) so you have its current text and section ids. Look at the other open ideas (action list) for duplicates and neighbours.\n")
          .Append("2. Research what it touches: read the code and docs that matter (grep, find, read, and the web where the idea needs it). Find where it lives (files, symbols), how it works today, what already exists, and what is unclear or risky.\n")
          .Append("3. Rewrite the idea with the ideas tool (action update). Keep the owner's intent and the owner's words: put their original text in a section titled “Original” (kind note) first if you change the summary. Then give it: a title that says it on one line, a summary of what and why in two to four sentences, and sections of the right kinds — research (what you found, with paths), plan (small ordered steps or the shape of the fix), requirements or design where a decision is made, and a section “Questions” (kind blocker) for what only the owner can decide. Add how it would be tested. Set priority and tags only when they are missing or plainly wrong.\n")
          .Append("4. Do not change any files and do not start the work: the tools that could are switched off in this chat. If the idea is already clear, say so and change only what is missing.\n\n")
          .Append("Finish with three short lines: what you changed in the idea, what is still open, and the question you most need the owner to answer (or “none”).");
        return sb.ToString();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
