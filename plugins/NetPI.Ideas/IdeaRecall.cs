using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// Recall on the first message (docs/plans/2026-09-27-ideas-follow-the-session.md): which open idea does the first
/// message of a chat continue? An idea id in the text is the match; otherwise one decision (<c>decide.decision</c>,
/// the Decide plugin's pass-through to NInfer's <c>/v1/decision</c>) over the open ideas of the session's project and
/// the global ones, title and summary per lettered option plus "none". The list is the system prompt, so the checks
/// made while the user types reuse NInfer's cache of it. Measured (docs/DECISION-MODELS.md, "Ideas recall"): at
/// p ≥ 0.8 no false chip on 56 none cases, 95 ms. <c>ideas.attach</c> adds the chosen idea to the chat as a notice.
/// </summary>
public sealed partial class IdeaRecall(IPluginContext ctx, IdeasRepository repo, IdeasLocator locator,
    IdeaVectors? vectors = null, IdeaOutcomes? outcomes = null)
{
    /// <summary>The model the checks are measured on (the setting's default; <c>ideas.model</c> names another one).</summary>
    public const string DefaultModel = "qwen3.8-27b";
    public const double DefaultThreshold = 0.8;
    public const int MinLength = 12;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The one pick-one question the composer asks while the user types.</summary>
    private readonly IdeaDecider _decider = new(ctx);

    [GeneratedRegex(@"\bidea-[a-z0-9]{6}\b", RegexOptions.IgnoreCase)]
    private static partial Regex IdeaIdPattern();

    public void Register(IRpcRegistry rpc)
    {
        rpc.Register("ideas.recall", Recall,
            "Recall on the first message: { sessionId, text } → { match: { id, title, p } | null, reason: id|model|none|short|off|unavailable|error, error?, ms }");
        rpc.Register("ideas.attach", Attach,
            "Add an idea to a chat: { sessionId, id } → { noticeId } — a notice (kind \"idea\") with the idea's title, summary and sections; the session is recorded on the idea");
    }

    public async Task<object?> Recall(RpcRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sessionId = req.Required("sessionId");
        var text = (req.Str("text") ?? "").Trim();
        JsonObject Result(string reason, JsonObject? match = null, string? error = null)
        {
            var o = new JsonObject { ["match"] = match, ["reason"] = reason, ["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds) };
            if (error is not null) o["error"] = error;
            return o;
        }
        if (!ctx.Settings.GetOr("ideas.recall", true)) return Result("off");
        var project = locator.ProjectOfSession(sessionId);
        var open = await OpenIdeasAsync(project?.Id, ct).ConfigureAwait(false);

        // An id in the text is the match (the titles the model reads hold no ids).
        foreach (Match m in IdeaIdPattern().Matches(text))
            if (open.FirstOrDefault(i => string.Equals(IdeaOps.Str(i["id"]), m.Value, StringComparison.OrdinalIgnoreCase)) is { } byId)
                return Result("id", MatchOf(byId, 1.0));

        if (open.Count == 0) return Result("none");
        if (text.Length < MinLength) return Result("short");
        if (!DecisionCapabilities.Available(ctx.Services, ctx.Rpc, "decide.decision")) return Result("unavailable");

        // One window while the user types (latency is the whole point here); a backlog larger than the letters is
        // ranked by what the message shares with each idea, so the ideas past the 51st are still eligible.
        // Above 51 ideas the meaning ranking joins the word ranking (below that every idea is offered, in backlog order,
        // so the list stays the same while the user types and NInfer keeps it cached).
        var ranking = vectors is null ? null : await vectors.RankAsync(text, open, ct).ConfigureAwait(false);
        var window = IdeaMatch.Windows(open, text, maxWindows: 1, ranking).First();
        var list = IdeaMatch.Options(window, "none of these: the message is about something else", out var none, out var labels);
        var system = "You match the first message of a new chat with an AI coding agent to the user's backlog of open ideas " +
                     "(planned features, fixes and experiments), so the agent can be given the idea's notes. Reply with the letter of the best option only.\n\n" +
                     "Open ideas:\n" + list;
        var question = $"First message of a new chat:\n<<<\n{IdeaOps.Clip(text, 2000)}\n>>>\n\n" +
                       "Is this message about working on one of the open ideas (continuing, building, testing or discussing it)? Which one? " +
                       $"Pick {none} when it is about something else, even if the topic is near an idea.\nAnswer with one letter.";

        // Typed-ahead background work: the decider waits for the backend's slot briefly (idea-np3a5g's rule for a drop)
        // and never for a paid model. A drop here is a keystroke without a suggestion, not a failure to report: the
        // composer moves on.
        var answer = await _decider.AskAsync(
            new JsonObject { ["role"] = "system", ["content"] = system }, question, labels, "recall",
            sessionId, project?.Id, IdeaAdmission.InteractiveWait, Timeout, ct).ConfigureAwait(false);
        if (answer.WillRepeat) return Result("skipped", error: answer.Reason);
        if (answer.Kind == IdeaDecider.AnswerKind.Dropped) return Result("no_slot", error: answer.Reason);
        if (answer.Probs is not { } probs) return Result("error", error: answer.Reason);

        // The answer is read the same way the other checks read it: only letters we offered count, "none" is not an idea.
        var pick = IdeaMatch.Pick(probs, IdeaMatch.Names(labels));
        if (pick is null) return Result("error", error: "the decision returned no usable probabilities");
        var threshold = Math.Clamp(ctx.Settings.GetOr("ideas.recallThreshold", DefaultThreshold), 0.3, 0.99);
        if (!pick.Clear(threshold)) return Result("none");
        // A chip shown is recorded (with the probabilities); ideas.attach records the ones the user added, so a chip
        // shown and never added is the user's "no".
        outcomes?.Record("recall", "shown", IdeaOutcomes.Pick(pick, IdeaOps.Str(window[pick.Index]["id"]), threshold, text).Also("sessionId", sessionId));
        return Result("model", MatchOf(window[pick.Index], pick.P));
    }

    public async Task<object?> Attach(RpcRequest req, CancellationToken ct)
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
        outcomes?.Record("recall", "added", new JsonObject
        {
            ["ideaId"] = IdeaOps.Str(idea["id"]), ["sessionId"] = sessionId, ["via"] = req.Str("via") ?? "user",
        });
        return new JsonObject { ["noticeId"] = added.Id, ["ideaId"] = IdeaOps.Str(idea["id"]) };
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

    /// <summary>The open ideas (not done or rejected) of the project and the global ones, in backlog order.</summary>
    private Task<List<JsonObject>> OpenIdeasAsync(string? projectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(repo.OpenIdeas(projectId));
    }

    private static JsonObject MatchOf(JsonObject idea, double p) => new()
    {
        ["id"] = IdeaOps.Str(idea["id"]),
        ["title"] = IdeaOps.Str(idea["title"]),
        ["p"] = Math.Round(p, 3),
    };
}
