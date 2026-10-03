using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>
/// <c>ideas.attach</c>: add an idea to a chat as a notice (kind <c>idea</c>) with its title, summary and sections, and
/// record the chat on the idea. The Ideas tab's "add to chat" and <c>ideas.refine</c> use it. (The composer's idea chip
/// that guessed the idea from the first message, <c>ideas.recall</c>, was removed on 2026-10-03: little value — 3 of 6
/// real matches — for a decision on every typing pause, and it offered an idea to chats that already had it; the agent
/// finds ideas itself with the ideas tool's meaning search and <c>memory_search</c>.)
/// </summary>
public sealed class IdeaAttach(IPluginContext ctx, IdeasRepository repo)
{
    public void Register(IRpcRegistry rpc) =>
        rpc.Register("ideas.attach", Attach,
            "Add an idea to a chat: { sessionId, id } → { noticeId } — a notice (kind \"idea\") with the idea's title, summary and sections; the session is recorded on the idea");

    public Task<object?> Attach(RpcRequest req, CancellationToken ct)
    {
        var sessionId = req.Required("sessionId");
        var id = req.Required("id");
        if (ctx.Sessions.GetSession(sessionId) is null) throw new RpcException("not_found", $"Session {sessionId} not found");
        ct.ThrowIfCancellationRequested();
        // The storage is a short transaction; the notice the user sees in the chat is not part of it.
        var (idea, _) = repo.AddSession(id, sessionId);
        var notice = ChatMessage.NoticeText(ToNotice(idea, "the ideas backlog"), "idea");
        notice.Meta!["ideaId"] = IdeaOps.Str(idea["id"]);
        var added = ctx.Sessions.AppendMessage(sessionId, notice);
        return Task.FromResult<object?>(new JsonObject { ["noticeId"] = added.Id, ["ideaId"] = IdeaOps.Str(idea["id"]) });
    }

    /// <summary>The notice the agent reads: the idea as a reference, not an order to implement it.</summary>
    public static string ToNotice(JsonObject idea, string where)
    {
        var sb = new StringBuilder();
        sb.Append("The user added an idea from the ideas backlog to this chat (`").Append(IdeaOps.Str(idea["id"])).Append("` in ").Append(where)
          .Append("). Use its notes; keep it up to date with the ideas tool when the work changes it.\n\n");
        sb.Append(IdeaOps.RenderMarkdown(idea));
        return sb.ToString().TrimEnd();
    }
}
