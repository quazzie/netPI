using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// Save on tab close (phase 2 of docs/plans/2026-09-27-ideas-follow-the-session.md). Closing a session tab is the moment a
/// plan gets forgotten, so the check runs then, in the background, and never holds the tab's close:
/// <list type="number">
/// <item>which open idea the chat worked on — a pick-one decision over the open ideas at p ≥ <c>ideas.attachThreshold</c>
/// (5/6 right at 0.8) — recorded on that idea as a <c>sessions</c> entry, without asking;</item>
/// <item>whether the chat leaves a plan, feature idea or research question that was neither built nor saved: one
/// generative call, reasoning low, that decides and drafts (NOTHING, or SAVE + title + summary). Measured
/// (docs/DECISION-MODELS.md, "Ideas: save on tab close"): 8/11 caught, 1/32 false, 1.6 s p50.</item>
/// </list>
/// A draft becomes a suggestion in the database (it survives a restart) and waits for the user. Nothing reaches the
/// backlog without a click, and the click is one transaction: the idea, the record of the answer and the card leaving
/// the queue commit together, so an interrupted answer can neither lose a card nor save an idea twice.
/// </summary>
public sealed class IdeaSaveCheck(IPluginContext ctx, IdeasRepository repo)
{
    public const double DefaultAttachThreshold = 0.8;
    public const int MinUserMessages = 2;
    /// <summary>How often a check that failed on this conversation is retried.</summary>
    public const int MaxTries = 3;
    /// <summary>And how long to wait before the next try.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);
    /// <summary>A tab closed while the agent was still working: the check waits for the run, this often and this long.</summary>
    internal static TimeSpan DeferStep { get; set; } = TimeSpan.FromSeconds(30);
    public const int MaxDefers = 20;
    public const string SuggestedEvent = "ideas.suggested";
    /// <summary>A card left the pending file (answered, discarded, or finished after a restart).</summary>
    public const string ResolvedEvent = "ideas.resolved";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // The digest the two checks read: what the user asked and what came back, tools by name. Never the raw transcript:
    // the measured runs used a digest, and a whole 200-turn session would not fit the check's window anyway.
    private const int MaxChars = 12000;
    /// <summary>How much of the budget the beginning of a long chat keeps (the rest goes to its ending).</summary>
    private const int HeadShare = 4;

    /// <summary>How the last turn of a closed tab ended: still running, finished, or stopped badly.</summary>
    private enum Turn { Open, Ended, Bad }

    /// <summary>Why a check may not run (yet), and what the UI is told.</summary>
    private sealed record Claim(bool Started, string Reason, string? Token);

    private static readonly string SaveSystem = """
        You decide whether a finished conversation with an AI coding agent leaves something worth keeping in the
        user's backlog of ideas, and if it does you draft it.

        Answer SAVE when the conversation leaves a plan, a feature idea or a research question that the user wanted
        and that was neither built nor already written down: a plan the agent made and nobody implemented, a feature
        the user asked for and the agent only discussed, an open question worth coming back to, a suggestion the user
        said to keep for later.

        Answer NOTHING for a loose end: a pending deploy, commit or restart, a failed one-off task, troubleshooting,
        work that was actually done, and a chat that only answered a question.

        A plan written to docs/plans in the repository counts as saved: answer NOTHING for it.

        Answer with NOTHING, or with three lines and nothing else:
        SAVE
        <title: specific, short, the idea on its own>
        <summary: one or two sentences saying what it is and what is left>
        """;

    /// <summary>Background model work queues behind the chats, on the backend's own slots.</summary>
    private readonly IdeaAdmission _admission = new(ctx);
    /// <summary>Every pick-one question a check asks, asked in one place.</summary>
    private readonly IdeaDecider _decider = new(ctx);
    private readonly IdeasRepository _repo = repo;

    public void Register(IRpcRegistry rpc)
    {
        rpc.Register("ideas.closed", Closed,
            "A session tab was closed: { sessionId } → { checked: bool, reason } — attaches the chat to the idea it worked on and offers a card when it leaves an unsaved plan (background)");
        rpc.RegisterReadOnly("ideas.suggestions", Suggestions,
            "Cards waiting for the user (a plan a closed chat left unsaved, an idea a commit may have finished): { } → { suggestions: [...] }");
        rpc.Register("ideas.resolve", Resolve,
            "Answer a card: { id, action: \"save\" | \"done\" | \"discard\", edit?: { title?, summary? } } → { saved: idea | null, discarded }");
    }

    // ------------------------------------------------------------------ the check

    /// <summary>
    /// A tab was closed: the chat behind it may leave something worth keeping. The check runs in the background and
    /// never holds the close. Whether it may run is decided from the conversation's <b>current</b> state and recorded
    /// per conversation revision, so a chat is checked once per revision, a failed check is retried, and a chat closed
    /// mid-run is checked when the run ends instead of never.
    /// </summary>
    public async Task<object?> Closed(RpcRequest req, CancellationToken ct)
    {
        var sessionId = req.Required("sessionId");
        if (!Setting("ideas.saveCheck", true)) return Result("off");
        var session = ctx.Sessions.GetSession(sessionId);
        if (session is null) return Result("no_session");
        if (session.Kind == "subagent") return Result("subagent");

        var messages = ctx.Sessions.GetMessages(sessionId);
        var users = messages.Count(m => m.Role == MessageRole.User);
        if (users < MinUserMessages) return Result("short");

        // The state the chat is in now, not every state it was in: a turn that was cut short hours ago and was worked
        // on since is a finished conversation. A tab closed while the agent is still running is not: it waits.
        var turn = TurnOf(messages);
        if (IdeaRuns.SessionBusy(ctx, sessionId)) turn = Turn.Open;
        if (turn == Turn.Bad) return Result("unfinished");

        var rev = Revision(messages);
        var claim = ClaimAsync(sessionId, users, rev, ct);
        if (!claim.Started) return Result(claim.Reason);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        ctx.Track(cts);
        _ = Task.Run(() => DeferredRunAsync(session, messages, turn, claim.Token, cts.Token), CancellationToken.None);
        return Result(turn == Turn.Open ? "running" : "started");
    }

    /// <summary>
    /// Take the check for one conversation revision, or say why not. The mark has a state: <c>running</c> is a claim in
    /// flight (a second close does not start a second check, and the claim is owned by a token so a worker that was
    /// overtaken cannot report over the newer outcome), <c>done</c> is the only permanent one, and <c>failed</c> may be
    /// tried again a few times — so an outage, a timeout or a reload no longer eats the check silently.
    /// </summary>
    private Claim ClaimAsync(string sessionId, int users, string rev, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (started, reason, token) = _repo.ClaimCheck(sessionId, users, rev, MaxTries, RetryAfter);
        return new Claim(started, reason, token);
    }

    /// <summary>Wait for an open run to end (a bounded number of times), then run the check once for real.</summary>
    private async Task DeferredRunAsync(SessionInfo session, IReadOnlyList<ChatMessage> messages, Turn turn, string? token, CancellationToken ct)
    {
      try {
        var current = messages;
        for (var waited = 0; (turn == Turn.Open || IdeaRuns.SessionBusy(ctx, session.Id)) && waited < MaxDefers; waited++)
        {
            await Task.Delay(DeferStep, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            if (ctx.Sessions.GetSession(session.Id) is null) { GiveUpAsync(session.Id, token, ct); return; }
            current = ctx.Sessions.GetMessages(session.Id);
            turn = TurnOf(current);
        }
        if (turn == Turn.Open || IdeaRuns.SessionBusy(ctx, session.Id)) { GiveUpAsync(session.Id, token, ct); return; }
        if (turn == Turn.Bad) { GiveUpAsync(session.Id, token, ct); return; }
        await RunAsync(session, current, token, ct).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        ctx.Logger.LogWarning("Ideas: the deferred check on {Session} failed: {Message}", session.Id, ex.Message);
      }
    }

    /// <summary>The conversation as it will be judged: its beginning for the intent, its ending for what came of it.</summary>
    private static Turn TurnOf(IReadOnlyList<ChatMessage> messages)
    {
        for (var k = messages.Count - 1; k >= 0; k--)
        {
            var m = messages[k];
            if (m.Role != MessageRole.Assistant) continue;
            return m.StopReason switch
            {
                null or "tool_use" => Turn.Open,
                "aborted" or "error" or "length" or "content_filter" => Turn.Bad,
                _ => Turn.Ended,
            };
        }
        return Turn.Open; // the user has not had an answer yet
    }

    /// <summary>
    /// What makes this conversation a different one to check: the messages it has now. Two closes of a chat that has
    /// not changed are the same revision, a chat with a new turn is not.
    /// </summary>
    private static string Revision(IReadOnlyList<ChatMessage> messages) => messages.Count > 0 ? $"{messages.Count}:{messages[^1].Id}" : "0";

    private async Task RunAsync(SessionInfo session, IReadOnlyList<ChatMessage> messages, string? token, CancellationToken ct)
    {
        var digest = Digest(messages);
        if (digest.Length < 80) { FinishAsync(session.Id, token, ct, null); return; } // nothing to judge: that is an outcome, not a failure
        var project = session.ProjectId is { } pid ? ctx.Sessions.GetProject(pid) : null;

        try
        {
            var open = OpenAsync(project?.Id, ct);
            if (open.Count > 0 && DecisionCapabilities.Available(ctx.Services, ctx.Rpc, "decide.decision"))
                await AttachAsync(open, session, digest, ct).ConfigureAwait(false);

            if (!ctx.Models.Cached.Any()) await ctx.Models.ListAsync(ct: ct).ConfigureAwait(false);
            var draft = await SaveCheckAsync(session, digest, project, ct).ConfigureAwait(false);
            if (draft is null)
            {
                FinishAsync(session.Id, token, ct, null);
                return;
            }

            if (IdeaRuns.SessionBusy(ctx, session.Id)) { GiveUpAsync(session.Id, token, ct); return; }
            // A plan the agent saved while this check was in flight must not be offered a second time.
            var alreadySaved = _repo.All().Any(i =>
                (i["sessionIds"] as JsonArray ?? []).Any(s => IdeaOps.Str(s) == session.Id) &&
                string.Equals(IdeaOps.Str(i["title"])?.Trim(), draft.Value.Title.Trim(), StringComparison.OrdinalIgnoreCase));
            if (alreadySaved) { FinishAsync(session.Id, token, ct, null); return; }

            var verdict = await new IdeaVerifier(ctx).VerifyAsync($"Save plan: {draft.Value.Title}\n{draft.Value.Summary}", digest, session.Id, project?.Id, ct).ConfigureAwait(false);
            if (!verdict.Verified) { FinishAsync(session.Id, token, ct, verdict.Retryable ? verdict.Reason : null); return; }
            if (IdeaRuns.SessionBusy(ctx, session.Id) || Revision(ctx.Sessions.GetMessages(session.Id)) != Revision(messages)) { GiveUpAsync(session.Id, token, ct); return; }

            var suggestion = new JsonObject
            {
                ["id"] = "sg_" + Guid.NewGuid().ToString("N")[..10],
                ["kind"] = "save",
                ["sessionId"] = session.Id,
                ["sessionTitle"] = IdeaOps.Clip(session.Title, 120),
                ["title"] = draft.Value.Title,
                ["summary"] = draft.Value.Summary,
                ["at"] = IdeaOps.Now(),
                ["seen"] = false,
                ["verified"] = true,
                ["verification"] = verdict.Reason,
            };
            if (project is not null) suggestion["project"] = new JsonObject { ["id"] = project.Id, ["name"] = project.Name };
            else suggestion["project"] = null;

            // One card per chat per plan: a check that runs twice (a retry, a restart) does not stack them.
            if (_repo.AddCard(suggestion))
            {
                ctx.Events.Publish(SuggestedEvent, new JsonObject { ["suggestion"] = suggestion.DeepClone() });
            }
            FinishAsync(session.Id, token, ct, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            FinishAsync(session.Id, token, ct, "cancelled");
        }
        catch (Exception ex)
        {
            // A failed check stays retryable: the mark records what went wrong and the next close of this conversation
            // tries again (a few times), instead of the chat being silently excluded forever.
            ctx.Logger.LogWarning("Ideas: the save check on {Session} failed: {Message}", session.Id, ex.Message);
            FinishAsync(session.Id, token, ct, ex.Message);
        }
    }

    /// <summary>
    /// The mark for this conversation revision. <paramref name="error"/> null means the check <b>ran</b> — "nothing worth
    /// keeping" is an answer, not a failure — and only a check that could not run (the model was down, the run never
    /// ended) leaves the mark failed, and therefore worth trying again. A worker whose claim was taken over in the
    /// meantime reports nothing at all: the newer run owns that mark.
    /// </summary>
    private void FinishAsync(string sessionId, string? token, CancellationToken ct, string? error)
    {
        try { _repo.FinishCheck(sessionId, token, error); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { ctx.Logger.LogDebug("Ideas: the check mark for {Session} could not be written: {Message}", sessionId, ex.Message); }
    }

    /// <summary>A check that waited for a run that never ended stops trying (its mark is retryable).</summary>
    private void GiveUpAsync(string sessionId, string? token, CancellationToken ct) => FinishAsync(sessionId, token, ct, "the run never ended");

    /// <summary>Which open idea the chat worked on: a <c>sessions</c> entry on that idea, no question asked.</summary>
    private async Task AttachAsync(List<JsonObject> open, SessionInfo session, string digest, CancellationToken ct)
    {
        // What the chat already says it worked on: an idea the user added to it (a notice), or a session entry an
        // earlier check recorded. That is the answer — asking the model about it again could only make it worse.
        var explicitId = ExplicitIdeaOf(session);
        if (explicitId is not null)
        {
            RecordAsync(explicitId, session, ct);
            return;
        }

        // Bounded: the digest picks the window when the backlog has more ideas than one decision can offer.
        IdeaPick? best = null;
        List<JsonObject>? bestWindow = null;
        foreach (var window in IdeaMatch.Windows(open, digest))
        {
            var list = IdeaMatch.Options(window, "none of these: the conversation is about something else", out var none, out var labels);
            var answer = await _decider.AskAsync(new JsonObject
            {
                ["role"] = "system",
                ["content"] = "You decide which of the user's backlog of open ideas a finished conversation with an AI coding " +
                              "agent was about, so the conversation can be attached to it. Reply with the letter of the best option only.\n\n" +
                              "Open ideas:\n" + list,
            }, "Which open idea is this conversation about? " +
               "Read the conversation: pick the idea it worked on (built, tested, discussed, or changed its plan), " +
               $"pick {none} when it is about something else. Answer with one letter.\n\n" +
               $"Conversation:\n<<<\n{IdeaOps.Clip(digest, 4000)}\n>>>",
                labels, "the attach question", sessionId: null, projectId: null, wait: null, Timeout, ct).ConfigureAwait(false);
            var pick = Pick(answer, labels);
            if (pick is null) return; // no usable answer: nothing is attached, and the save check still runs
            if (best is null || pick.P > best.P) { best = pick; bestWindow = window; }
            if (best.P > 0.5 || pick.None >= best.P) break; // decided (or nothing in this window): no further windows
        }
        if (best is null || bestWindow is null) return;
        if (!best.Clear(Math.Clamp(Setting("ideas.attachThreshold", DefaultAttachThreshold), 0.3, 0.99))) return;
        if (IdeaOps.Str(bestWindow[best.Index]["id"]) is not { Length: > 0 } chosenId) return;
        RecordAsync(chosenId, session, ct);
    }

    /// <summary>The idea this chat is explicitly about: one the user added to it, or one an earlier check recorded it on.</summary>
    private string? ExplicitIdeaOf(SessionInfo session)
    {
        foreach (var m in ctx.Sessions.GetMessages(session.Id))
            if (m.Meta?["kind"] is JsonValue v && v.TryGetValue<string>(out var kind) && kind == "idea"
                && m.Meta["ideaId"] is JsonValue id && id.TryGetValue<string>(out var ideaId) && ideaId.Length > 0)
                return ideaId;
        return _repo.IdeaWithSession(session.Id);
    }

    /// <summary>
    /// Record on the idea that this chat worked on it — a <c>sessions</c> entry beside the text, one per session, so
    /// the idea's own notes are never edited by the check.
    /// </summary>
    private void RecordAsync(string id, SessionInfo session, CancellationToken ct)
    {
        // A session that recall already attached is not attached twice: the entry carries the note, not a second mark.
        var entry = new JsonObject
        {
            ["sessionId"] = session.Id,
            ["title"] = IdeaOps.Clip(session.Title, 120),
            ["at"] = IdeaOps.Now(),
            ["seen"] = false,
        };
        if (session.MessageCount > 0) entry["seq"] = session.MessageCount;
        _repo.AddSessionEntry(id, entry);
    }

    /// <summary>The generative check: NOTHING, or a drafted title and summary.</summary>
    private async Task<(string Title, string Summary)?> SaveCheckAsync(SessionInfo session, string digest, ProjectInfo? projectOf, CancellationToken ct)
    {
        var model = await ResolveModelAsync(ct).ConfigureAwait(false);
        // Not a silent "no card": the check did not run, so the mark says why and the next close tries again.
        if (model is null) throw new InvalidOperationException($"model {Setting("ideas.model", IdeaRecall.DefaultModel)} is not in the catalog");
        var maxOut = Math.Clamp(model.MaxOutputTokens is > 0 and var m ? Math.Min(m, 1024) : 1024, 256, 4096);
        var request = new ModelRequest
        {
            Model = model,
            SystemPrompt = SaveSystem,
            Messages = [ChatMessage.UserText($"Conversation with the agent:\n<<<\n{digest}\n>>>\n\nDoes it leave something worth keeping?")],
            ReasoningEffort = EffortFor(model),
            MaxOutputTokens = maxOut,
            SessionId = session.Id,
            Purpose = "other",
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var admission = await _admission.EnterAsync(model, "the save check", session.Id, projectOf?.Id, ct).ConfigureAwait(false);
        // A drop is a failure, not an answer: the mark stays retryable and carries the reason, so the next close of
        // this conversation runs the check again instead of the chat being silently excluded. A skip (no model, or a
        // paid one the user did not allow) will not change on a retry, so it is an outcome: no call, and no card.
        if (admission.Retryable) throw new InvalidOperationException($"dropped, and it runs again later: {admission.Reason}");
        if (!admission.Admitted) return null;
        using var slot = admission.Lease!;
        var response = await ctx.Models.CompleteAsync(request, cts.Token).ConfigureAwait(false);
        return Parse(response.Text);
    }

    /// <summary>NOTHING, or SAVE with a title and a summary. Anything else is nothing: a card is never a guess.</summary>
    internal static (string Title, string Summary)? Parse(string text)
    {
        // Empty lines are kept: a blank line right after SAVE is a missing title, not a summary line.
        var lines = text.Split('\n').SkipWhile(string.IsNullOrWhiteSpace).ToArray();
        if (lines.Length == 0 || !lines[0].StartsWith("SAVE", StringComparison.OrdinalIgnoreCase)) return null;
        var title = lines.Length > 1 ? Clean(lines[1]) : "";
        var summary = string.Join(' ', lines.Skip(2).Select(Clean).Where(s => s.Length > 0));
        if (title.Length is 0 or > 140) return null;
        return (title, IdeaOps.Clip(summary, 600));
    }

    private static string Clean(string s) => s.Trim().Trim('#', '*', '`', '"', '\'', '-', ' ').Trim();

    /// <summary>
    /// A <see cref="IdeaDecider.Answer"/> as this check reads it: no pick for a skip, a drop or a failure alike, so
    /// the caller only has to know whether there is an answer. Only the labels that were offered count, and "none" is
    /// never an idea.
    /// </summary>
    private static IdeaPick? Pick(IdeaDecider.Answer answer, JsonArray labels) =>
        answer.Probs is { } probs ? IdeaMatch.Pick(probs, IdeaMatch.Names(labels)) : null;

    // ------------------------------------------------------------------ the cards

    /// <summary>The cards waiting for the user, in the order they arrived (a plain read: it never writes).</summary>
    public Task<object?> Suggestions(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<object?>(new JsonObject
        {
            ["suggestions"] = new JsonArray(_repo.Cards().Select(c => (JsonNode?)c).ToArray()),
        });
    });

    /// <summary>
    /// Answer a card. One transaction, and the transaction is the whole of it: what the card asks is checked against
    /// what the user answered, the idea is written (or marked done), the answer is recorded and the card leaves the
    /// queue. A second window answering the same card, or the same call retried after a timeout, reads the recorded
    /// answer instead of producing it again — so an answer cannot be lost and cannot happen twice, and there is no
    /// journal to replay.
    /// </summary>
    public Task<object?> Resolve(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var id = req.Required("id");
        var action = (req.Str("action") ?? "").Trim().ToLowerInvariant();
        var edit = req.Prop("edit") is { ValueKind: JsonValueKind.Object } e ? JsonObject.Create(e.Clone()) as JsonObject : null;
        // A card that is already answered is not gone: the repository reports what it did last time, so a retry (or a
        // second window) cannot save a second idea.
        var card = _repo.Card(id);
        var resolution = _repo.ResolveCard(id, action, edit);
        // Only after the commit: nothing is announced that did not happen.
        PublishResolved(id, action, card);
        return Task.FromResult<object?>(new JsonObject
        {
            ["saved"] = resolution.Idea?.DeepClone(),
            ["discarded"] = resolution.Idea is null,
            ["action"] = resolution.Action,
            ["alreadyResolved"] = resolution.AlreadyResolved,
        });
    });

    /// <summary>A storage failure is the caller's answer, not a crash.</summary>
    private static async Task<object?> Guard(Func<Task<object?>> body)
    {
        try { return await body().ConfigureAwait(false); }
        catch (RpcException) { throw; }
        catch (IdeasConflictException ex) { throw new RpcException("conflict", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new RpcException("io_error", ex.Message); }
    }

    private void PublishResolved(string? id, string? action, JsonNode? card) =>
        ctx.Events.Publish(ResolvedEvent, new JsonObject { ["id"] = id, ["action"] = action, ["card"] = card?.DeepClone() });

    /// <summary>A card this check offers (the commit check uses the same door, so the two never stack cards).</summary>
    public bool Offer(JsonObject card)
    {
        if (!_repo.AddCard(card)) return false;
        ctx.Events.Publish(SuggestedEvent, new JsonObject { ["suggestion"] = card.DeepClone() });
        return true;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The conversation as the checks read it: the user turns whole, the answers and tools clipped. A long chat keeps
    /// its <b>beginning and its ending</b> — the intent at the top and what came of it (built, cancelled, deferred) at
    /// the bottom — because a check that only sees the discussion mistakes an implemented plan for an open one.
    /// </summary>
    internal static string Digest(IReadOnlyList<ChatMessage> messages)
    {
        var lines = new List<string>();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case MessageRole.User:
                    lines.Add("User: " + IdeaOps.Clip(Plain(m), 1500));
                    break;
                case MessageRole.Assistant:
                    var text = Plain(m);
                    var tools = m.Parts.OfType<ToolCallPart>().Select(c => c.Name).ToList();
                    if (text.Length == 0 && tools.Count == 0) break;
                    var line = "Agent: " + IdeaOps.Clip(text, 800);
                    if (tools.Count > 0) line += "\n  used: " + string.Join(", ", tools.Distinct());
                    lines.Add(line);
                    break;
            }
        }
        var all = string.Join('\n', lines);
        if (all.Length <= MaxChars) return all;
        var head = MaxChars * HeadShare / (HeadShare + 1); // the rest is the ending
        var tail = MaxChars - head;
        return all[..head].TrimEnd() + "\n\n[... " + (all.Length - MaxChars).ToString("N0") + " characters of the middle of this conversation ...]\n\n" + all[^tail..];
    }

    private static string Plain(ChatMessage m) => string.Join(' ', m.Parts.OfType<TextPart>().Select(t => t.Text));

    /// <summary>The open ideas the check is offered: the project's and the unbound ones, in backlog order.</summary>
    private List<JsonObject> OpenAsync(string? projectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _repo.OpenIdeas(projectId);
    }

    /// <summary>
    /// The model the check runs on: exactly the one the user configured. There is no fallback to whatever else is in
    /// the catalog — a check that silently runs on another model (a paid one, or a much weaker one) is worse than no
    /// check, and the user is told instead.
    /// </summary>
    private async Task<ModelInfo?> ResolveModelAsync(CancellationToken ct)
    {
        var name = Setting("ideas.model", IdeaRecall.DefaultModel) is { Length: > 0 } m ? m.Trim() : IdeaRecall.DefaultModel;
        var model = await ctx.Models.FindAsync(name, ct).ConfigureAwait(false);
        if (model is null)
            ctx.Logger.LogWarning("Ideas: the save check cannot run: model {Model} is not in the catalog (it is not replaced by another one).", name);
        return model;
    }

    /// <summary>
    /// The cheapest reasoning effort the model actually has, by an explicit order — "low" if it has it, then less, and
    /// the model's own default when it has none of them. Never a step up: asking a big model to think harder is not
    /// what this check wants.
    /// </summary>
    internal static string? EffortFor(ModelInfo model)
    {
        var r = model.Reasoning;
        if (r is null || !r.Supported) return null;
        foreach (var wanted in (string[])["low", "minimal", "none"])
            if (r.Efforts.Any(e => string.Equals(e, wanted, StringComparison.OrdinalIgnoreCase)))
                return r.Efforts.First(e => string.Equals(e, wanted, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private T Setting<T>(string key, T fallback)
    {
        try { return ctx.Settings.Get(key, fallback) ?? fallback; }
        catch { return fallback; }
    }

    private static JsonObject Result(string reason) => new() { ["checked"] = reason is "started" or "already", ["reason"] = reason };
}
