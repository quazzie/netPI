using System.Globalization;
using System.Text;
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
/// A draft becomes a suggestion in <c>~/.netpi/ideas-pending.json</c> (it survives a restart) and waits for the user.
/// Nothing reaches the backlog without a click.
/// </summary>
public sealed class IdeaSaveCheck(IPluginContext ctx, IdeasStore store, IdeasLocator locator)
{
    public const string DefaultModel = "qwen3.8-27b";
    public const double DefaultAttachThreshold = 0.8;
    public const int MinUserMessages = 2;
    /// <summary>How long a "this chat was checked" mark is kept, so a session reopened months later is not asked again.</summary>
    private const int MarkKeepDays = 30;
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

    // Single-token letters for the options (the last one used is "none"); more open ideas than letters: the newest stay out.
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // The digest the two checks read: what the user asked and what came back, tools by name. Never the raw transcript:
    // the measured runs used a digest, and a whole 200-turn session would not fit the check's window anyway.
    private const int MaxChars = 12000;
    /// <summary>How much of the budget the beginning of a long chat keeps (the rest goes to its ending).</summary>
    private const int HeadShare = 4;

    /// <summary>How the last turn of a closed tab ended: still running, finished, or stopped badly.</summary>
    private enum Turn { Open, Ended, Bad }

    /// <summary>Why a check may not run (yet), and what the UI is told.</summary>
    private sealed record Claim(bool Started, string Reason);

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

    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>The OS lock on the pending file, shared with any other instance of this plugin.</summary>
    private readonly FileGate _files = new();
    /// <summary>Background model work queues behind the chats, on the backend's own slots.</summary>
    private readonly IdeaAdmission _admission = new(ctx);

    public void Register(IRpcRegistry rpc)
    {
        rpc.Register("ideas.closed", Closed,
            "A session tab was closed: { sessionId } → { checked: bool, reason } — attaches the chat to the idea it worked on and offers a card when it leaves an unsaved plan (background)");
        rpc.Register("ideas.suggestions", Suggestions,
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
        if (turn == Turn.Bad) return Result("unfinished");

        var rev = Revision(messages);
        var claim = await ClaimAsync(sessionId, users, rev, ct).ConfigureAwait(false);
        if (!claim.Started) return Result(claim.Reason);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        ctx.Track(cts);
        _ = Task.Run(() => DeferredRunAsync(session, messages, turn, cts.Token), CancellationToken.None);
        return Result(turn == Turn.Open ? "running" : "started");
    }

    /// <summary>
    /// Take the check for one conversation revision, or say why not. The mark has a state: <c>running</c> is a claim in
    /// flight (a second close does not start a second check), <c>done</c> is the only permanent one, and <c>failed</c>
    /// may be tried again a few times — so an outage, a timeout or a reload no longer eats the check silently.
    /// </summary>
    private async Task<Claim> ClaimAsync(string sessionId, int users, string rev, CancellationToken ct) =>
        await MutateAsync(f =>
        {
            var marks = f["checked"] as JsonObject ?? (JsonObject)(f["checked"] = new JsonObject());
            if (marks[sessionId] is JsonObject seen && IdeaOps.Str(seen["rev"]) == rev)
            {
                var state = IdeaOps.Str(seen["state"]) ?? "done"; // marks from before the states were just "checked"
                if (state is "done" or "running") return new Claim(false, "already");
                if (state == "failed" && Tries(seen) >= MaxTries
                    && Stamp(seen["at"]) is { } at && DateTimeOffset.UtcNow - at < RetryAfter)
                    return new Claim(false, "already");
            }
            marks[sessionId] = new JsonObject
            {
                ["n"] = users,
                ["rev"] = rev,
                ["state"] = "running",
                ["tries"] = 0,
                ["at"] = IdeaOps.Now(),
            };
            var cutoff = DateTimeOffset.UtcNow.AddDays(-MarkKeepDays);
            foreach (var key in marks.Select(p => p.Key).ToList())
                if (marks[key] is JsonObject m && Stamp(m["at"]) is { } at && at < cutoff)
                    marks.Remove(key);
            return new Claim(true, "started");
        }, ct).ConfigureAwait(false);

    /// <summary>Wait for an open run to end (a bounded number of times), then run the check once for real.</summary>
    private async Task DeferredRunAsync(SessionInfo session, IReadOnlyList<ChatMessage> messages, Turn turn, CancellationToken ct)
    {
      try {
        var current = messages;
        for (var waited = 0; turn == Turn.Open && waited < MaxDefers; waited++)
        {
            await Task.Delay(DeferStep, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            if (ctx.Sessions.GetSession(session.Id) is null) { await GiveUpAsync(session.Id, ct).ConfigureAwait(false); return; }
            current = ctx.Sessions.GetMessages(session.Id);
            turn = TurnOf(current);
        }
        await RunAsync(session, current, turn == Turn.Open, ct).ConfigureAwait(false);
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

    private async Task RunAsync(SessionInfo session, IReadOnlyList<ChatMessage> messages, bool gaveUpWaiting, CancellationToken ct)
    {
        var digest = Digest(messages);
        if (digest.Length < 80) { await FinishAsync(session.Id, ct, null).ConfigureAwait(false); return; } // nothing to judge: that is an outcome, not a failure
        var project = session.ProjectId is { } pid ? ctx.Sessions.GetProject(pid) : null;

        try
        {
            var open = await OpenAsync(project?.Id, ct).ConfigureAwait(false);
            if (open.Count > 0 && ctx.Rpc.Exists("decide.decision"))
                await AttachAsync(open, session, digest, ct).ConfigureAwait(false);

            if (!ctx.Models.Cached.Any()) await ctx.Models.ListAsync(ct: ct).ConfigureAwait(false);
            var draft = await SaveCheckAsync(session, digest, project, ct).ConfigureAwait(false);
            if (draft is null)
            {
                await FinishAsync(session.Id, ct, gaveUpWaiting ? "the run never ended, so the chat was judged as it was" : null).ConfigureAwait(false);
                return;
            }

            var suggestion = new JsonObject
            {
                ["id"] = "sg_" + Guid.NewGuid().ToString("N")[..10],
                ["kind"] = "save",
                ["sessionId"] = session.Id,
                ["sessionTitle"] = Clip(session.Title, 120),
                ["title"] = draft.Value.Title,
                ["summary"] = draft.Value.Summary,
                ["at"] = IdeaOps.Now(),
                ["seen"] = false,
            };
            if (project is not null) suggestion["project"] = new JsonObject { ["id"] = project.Id, ["name"] = project.Name };
            else suggestion["project"] = null;

            var added = await MutateAsync(f =>
            {
                // One card per chat per plan: a check that runs twice (a retry, a restart) does not stack them.
                var cards = f["suggestions"]!.AsArray();
                foreach (var node in cards.OfType<JsonObject>())
                    if (IdeaOps.Str(node["kind"]) is "save" or null && IdeaOps.Str(node["sessionId"]) == session.Id
                        && Same(node["title"]?.DeepClone(), suggestion["title"]?.DeepClone()))
                        return false;
                cards.Add(suggestion);
                return true;
            }, ct).ConfigureAwait(false);
            if (!added) { await FinishAsync(session.Id, ct, null).ConfigureAwait(false); return; } // the card was already there: still a result
            ctx.Events.Publish(SuggestedEvent, new JsonObject { ["suggestion"] = suggestion.DeepClone() });
            await FinishAsync(session.Id, ct, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishAsync(session.Id, ct, "cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed check stays retryable: the mark records what went wrong and the next close of this conversation
            // tries again (a few times), instead of the chat being silently excluded forever.
            ctx.Logger.LogWarning("Ideas: the save check on {Session} failed: {Message}", session.Id, ex.Message);
            await FinishAsync(session.Id, ct, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The mark for this conversation revision. <paramref name="error"/> null means the check <b>ran</b> — "nothing worth
    /// keeping" is an answer, not a failure — and only a check that could not run (the model was down, the file could
    /// not be written, the run never ended) leaves the mark failed, and therefore worth trying again.
    /// </summary>
    private async Task FinishAsync(string sessionId, CancellationToken ct, string? error)
    {
        try
        {
            await MutateAsync(f =>
            {
                if (f["checked"] is not JsonObject marks || marks[sessionId] is not JsonObject mark) return false;
                if (IdeaOps.Str(mark["state"]) is not "running") return false; // a later close already took it
                mark["state"] = error is null ? "done" : "failed";
                mark["at"] = IdeaOps.Now();
                if (error is not null) mark["error"] = Clip(error, 200);
                if (mark["tries"] is JsonValue v && v.TryGetValue<int>(out var tries)) mark["tries"] = tries + 1;
                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { ctx.Logger.LogDebug("Ideas: the check mark for {Session} could not be written: {Message}", sessionId, ex.Message); }
    }

    /// <summary>A check that waited for a run that never ended stops trying (its mark is retryable).</summary>
    private async Task GiveUpAsync(string sessionId, CancellationToken ct) =>
        await FinishAsync(sessionId, ct, "the run never ended").ConfigureAwait(false);

    /// <summary>Which open idea the chat worked on: a <c>sessions</c> entry on that idea, no question asked.</summary>
    private async Task AttachAsync(List<JsonObject> open, SessionInfo session, string digest, CancellationToken ct)
    {
        // What the chat already says it worked on: an idea the user added to it (a notice), or a session entry an
        // earlier check recorded. That is the answer — asking the model about it again could only make it worse.
        var explicitId = await ExplicitIdeaOfAsync(session, ct).ConfigureAwait(false);
        if (explicitId is not null)
        {
            await RecordAsync(explicitId, session, ct).ConfigureAwait(false);
            return;
        }

        // Bounded: the digest picks the window when the backlog has more ideas than one decision can offer.
        IdeaPick? best = null;
        List<JsonObject>? bestWindow = null;
        foreach (var window in IdeaMatch.Windows(open, digest))
        {
            var list = IdeaMatch.Options(window, "none of these: the conversation is about something else", out var none, out var labels);
            var answer = await DecideAsync(new JsonObject
            {
                ["role"] = "system",
                ["content"] = "You decide which of the user's backlog of open ideas a finished conversation with an AI coding " +
                              "agent was about, so the conversation can be attached to it. Reply with the letter of the best option only.\n\n" +
                              "Open ideas:\n" + list,
            }, "Which open idea is this conversation about? " +
               "Read the conversation: pick the idea it worked on (built, tested, discussed, or changed its plan), " +
               $"pick {none} when it is about something else. Answer with one letter.\n\n" +
               $"Conversation:\n<<<\n{Clip(digest, 4000)}\n>>>",
                labels, ct).ConfigureAwait(false);
            if (answer is null) return; // no usable answer: nothing is attached, and the save check still runs
            if (best is null || answer.P > best.P) { best = answer; bestWindow = window; }
            if (best.P > 0.5 || answer.None >= best.P) break; // decided (or nothing in this window): no further windows
        }
        if (best is null || bestWindow is null) return;
        if (best.P < Math.Clamp(Setting("ideas.attachThreshold", DefaultAttachThreshold), 0.3, 0.99)
            || best.P <= best.None) return;
        if (IdeaOps.Str(bestWindow[best.Index]["id"]) is not { Length: > 0 } chosenId) return;
        await RecordAsync(chosenId, session, ct).ConfigureAwait(false);
    }

    /// <summary>The idea this chat is explicitly about: one the user added to it, or one an earlier check recorded it on.</summary>
    private async Task<string?> ExplicitIdeaOfAsync(SessionInfo session, CancellationToken ct)
    {
        foreach (var m in ctx.Sessions.GetMessages(session.Id))
            if (m.Meta?["kind"] is JsonValue v && v.TryGetValue<string>(out var kind) && kind == "idea"
                && m.Meta["ideaId"] is JsonValue id && id.TryGetValue<string>(out var ideaId) && ideaId.Length > 0)
                return ideaId;
        return await store.ReadAsync(locator.GlobalFile(), f => IdeaOps.All(f.Ideas)
            .Where(i => IdeaOps.WorkedOnWith(i, session.Id))
            .Select(i => IdeaOps.Str(i["id"]))
            .FirstOrDefault(id => id is { Length: > 0 }), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Record on the idea that this chat worked on it — a <c>sessions</c> entry beside the text, one per session, so
    /// the idea's own notes are never edited by the check.
    /// </summary>
    private async Task RecordAsync(string id, SessionInfo session, CancellationToken ct)
    {
        // A session that recall already attached is not attached twice: the entry carries the note, not a second mark.
        var entry = new JsonObject
        {
            ["sessionId"] = session.Id,
            ["title"] = Clip(session.Title, 120),
            ["at"] = IdeaOps.Now(),
            ["seen"] = false,
        };
        if (session.MessageCount > 0) entry["seq"] = session.MessageCount;
        await store.UpdateAsync<object?>(locator.GlobalFile(), f =>
        {
            if (IdeaOps.Find(f.Ideas, id) is { } found) IdeaOps.AddSessionEntry(found, entry);
            return null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The generative check: NOTHING, or a drafted title and summary.</summary>
    private async Task<(string Title, string Summary)?> SaveCheckAsync(SessionInfo session, string digest, ProjectInfo? projectOf, CancellationToken ct)
    {
        var model = await ResolveModelAsync(ct).ConfigureAwait(false);
        // Not a silent "no card": the check did not run, so the mark says why and the next close tries again.
        if (model is null) throw new InvalidOperationException($"model {Setting("ideas.model", DefaultModel)} is not in the catalog");
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
        using var slot = await _admission.EnterAsync(model, "the save check", session.Id, projectOf?.Id, ct).ConfigureAwait(false);
        if (slot is null) return null; // a paid model the user did not allow: no call, and no card
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
        return (title, Clip(summary, 600));
    }

    private static string Clean(string s) => s.Trim().Trim('#', '*', '`', '"', '\'', '-', ' ').Trim();

    private async Task<IdeaPick?> DecideAsync(JsonObject system, string question, JsonArray labels, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var name = Setting("ideas.model", DefaultModel) is { Length: > 0 } m ? m.Trim() : DefaultModel;
            // The decision runs on the same backend as the chats, so it takes a slot like everything else.
            var model = await ctx.Models.FindAsync(name, ct).ConfigureAwait(false);
            using var slot = await _admission.EnterAsync(model, "an ideas decision", sessionId: null, projectId: null, ct).ConfigureAwait(false);
            var raw = await ctx.Rpc.InvokeAsync("decide.decision", new JsonObject
            {
                ["model"] = name,
                ["messages"] = new JsonArray(system),
                ["branches"] = new JsonArray(new JsonObject { ["id"] = "pick", ["content"] = question, ["labels"] = labels }),
            }, cts.Token).ConfigureAwait(false);
            // Read the answer once, in one place: only the letters we offered count, and "none" is never an idea.
            return IdeaMatch.Answer(raw as JsonNode ?? JsonSerializer.SerializeToNode(raw), labels.Count - 1);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ctx.Logger.LogWarning("Ideas: the check got no answer within {Seconds:0} s.", Timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogWarning("Ideas: the check failed: {Message}", ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ the cards

    public async Task<object?> Suggestions(RpcRequest req, CancellationToken ct) => await Guard(async () =>
        new JsonObject { ["suggestions"] = (await ReadPendingAsync(ct).ConfigureAwait(false))["suggestions"]!.DeepClone() }
    ).ConfigureAwait(false);

    /// <summary>
    /// Answer a card. The answer is written to the pending file <b>before</b> the backlog is touched and taken out of it
    /// <b>after</b>, and the journal entry is replayed at the next start: a crash, a failed write or a reload between
    /// the two cannot lose the card and cannot save the same idea twice. Everything it needs (the idea, its id and its
    /// timestamps) is in the entry, so a replay writes exactly the same idea.
    /// </summary>
    public async Task<object?> Resolve(RpcRequest req, CancellationToken ct) => await Guard(() => ResolveCore(req, ct)).ConfigureAwait(false);

    private async Task<object?> ResolveCore(RpcRequest req, CancellationToken ct)
    {
        var id = req.Required("id");
        var action = (req.Str("action") ?? "").Trim().ToLowerInvariant();
        if (action is not ("save" or "done" or "discard")) throw new RpcException("bad_request", "action must be \"save\", \"done\" or \"discard\"");
        var edit = req.Prop("edit") is { ValueKind: JsonValueKind.Object } e ? JsonObject.Create(e.Clone()) : null;
        await RecoverAsync(ct).ConfigureAwait(false); // an interrupted answer is finished before this one

        // "discard" touches the pending file only: it is one write, and that write is atomic.
        if (action == "discard")
        {
            var card = await MutateAsync(f =>
            {
                var found = Card(f, id) ?? throw Gone();
                Validate(action, found);
                Remove(f, id);
                return (JsonNode)found.DeepClone();
            }, ct).ConfigureAwait(false);
            PublishResolved(id, action, card);
            return new JsonObject { ["saved"] = null, ["discarded"] = true };
        }

        // 1. the answer, durable, with the idea it will write already built (so a replay writes the same idea)
        var card0 = Card(await ReadPendingAsync(ct).ConfigureAwait(false), id) ?? throw Gone();
        Validate(action, card0);
        var entry = new JsonObject
        {
            ["id"] = "op_" + Guid.NewGuid().ToString("N")[..10],
            ["action"] = action,
            ["cardId"] = id,
            ["at"] = IdeaOps.Now(),
            ["applied"] = false,
        };
        if (action == "save") entry["idea"] = await DraftAsync(card0, edit, ct).ConfigureAwait(false);
        else entry["ideaId"] = IdeaOps.Str(card0["ideaId"]);
        JsonObject op = (JsonObject)(await MutateAsync(f =>
        {
            var card = Card(f, id) ?? throw Gone(); // another window answered it while we were building the idea
            // One window answers a card. A claim left by an answer that is still in the journal means another window is
            // on it right now; if that one dies the journal entry finishes its answer at the next start.
            var claim = IdeaOps.Str(card["claim"]);
            if (claim is { Length: > 0 } && claim != IdeaOps.Str(entry["id"]) && Op(f, claim) is not null)
                throw new RpcException("conflict", "Another window is answering that card.");
            card["claim"] = IdeaOps.Str(entry["id"]);
            Ops(f).Add((JsonObject)entry.DeepClone());
            return (JsonNode)entry.DeepClone();
        }, ct).ConfigureAwait(false))!;

        // 2. the backlog; 3. the card and the journal entry leave the pending file
        try
        {
            var result = await ApplyAsync(op, ct).ConfigureAwait(false);
            var card = await MutateAsync(f =>
            {
                Op(f, IdeaOps.Str(op["id"]))!["applied"] = true;
                var card = Card(f, IdeaOps.Str(op["cardId"]));
                Remove(f, IdeaOps.Str(op["cardId"]));
                DropOp(f, IdeaOps.Str(op["id"]));
                return (JsonNode?)card?.DeepClone();
            }, ct).ConfigureAwait(false);
            PublishResolved(IdeaOps.Str(op["cardId"]), action, card);
            return result;
        }
        catch
        {
            // Nothing reached the backlog: take the answer back out, so the card stays answerable.
            await DropOpQuietlyAsync(IdeaOps.Str(op["id"]), ct).ConfigureAwait(false);
            throw;
        }
    }

    private static RpcException Gone() => new("not_found", "That card is gone (already answered, discarded, or NetPI restarted).");

    /// <summary>A file we cannot read or do not understand, or one we cannot write, is the caller's answer, not a crash.</summary>
    private static async Task<object?> Guard(Func<Task<object?>> body)
    {
        try { return await body().ConfigureAwait(false); }
        catch (IdeasFileException ex) { throw new RpcException("invalid_file", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new RpcException("io_error", ex.Message); }
    }

    /// <summary>What the card is about has to match what the user answered, before the card is consumed.</summary>
    private static void Validate(string action, JsonObject card)
    {
        var kind = IdeaOps.Str(card["kind"]) ?? "save";
        if (action == "done" && kind != "done") throw new RpcException("bad_request", "That card asks to save an idea; answer \"save\" or \"discard\".");
        if (action == "save" && kind == "done") throw new RpcException("bad_request", "That card marks an existing idea done; answer \"done\" or \"discard\".");
        if (action == "done" && IdeaOps.Str(card["ideaId"]) is not { Length: > 0 }) throw new RpcException("bad_request", "That card is not about an idea.");
        if (action == "save" && (IdeaOps.Str(card["title"]) ?? "").Trim().Length == 0) throw new RpcException("bad_request", "An idea needs a title.");
    }

    /// <summary>
    /// The idea a "save" answers writes: the card's title and summary (the user's edit wins), stamped with the card's
    /// project and its own session entry. Built once, here, so the journal holds the finished idea.
    /// </summary>
    private async Task<JsonObject> DraftAsync(JsonObject card, JsonObject? edit, CancellationToken ct)
    {
        if (edit is not null)
        {
            if (edit["title"] is JsonValue t && t.TryGetValue<string>(out var title) && title.Trim().Length > 0) card["title"] = title.Trim();
            if (edit["summary"] is JsonValue sv && sv.TryGetValue<string>(out var summary)) card["summary"] = summary.Trim();
        }
        if ((IdeaOps.Str(card["title"]) ?? "").Trim().Length == 0) throw new RpcException("bad_request", "An idea needs a title.");
        var project = card["project"] as JsonObject;
        var sessionId = IdeaOps.Str(card["sessionId"]);
        return await store.ReadAsync(locator.GlobalFile(), file =>
        {
            var idea = IdeaOps.CreateIdea(new JsonObject
            {
                ["title"] = IdeaOps.Str(card["title"]),
                ["summary"] = IdeaOps.Str(card["summary"]),
            }, file.Ideas, "user", sessionId is { Length: > 0 } sid ? sid : null, keepExtraFields: true);
            IdeaOps.SetProject(idea,
                project?["id"] is JsonValue pid && pid.TryGetValue<string>(out var id) ? id : null,
                project?["name"] is JsonValue pname && pname.TryGetValue<string>(out var name) ? name : null);
            IdeaOps.AddSessionEntry(idea, new JsonObject
            {
                ["sessionId"] = sessionId,
                ["title"] = IdeaOps.Str(card["sessionTitle"]),
                ["at"] = IdeaOps.Now(),
                ["seen"] = true,
            });
            return (JsonObject)idea.DeepClone();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Put the answer into the backlog. Idempotent: the same entry applied twice saves one idea.</summary>
    private async Task<JsonObject> ApplyAsync(JsonObject op, CancellationToken ct)
    {
        if (IdeaOps.Str(op["action"]) == "done")
        {
            var target = IdeaOps.Str(op["ideaId"]) ?? "";
            var marked = await store.UpdateAsync(locator.GlobalFile(), file =>
            {
                var idea = IdeaOps.Find(file.Ideas, target) ?? throw new RpcException("not_found", $"No idea {target}.");
                IdeaOps.ApplyPatch(idea, new JsonObject { ["status"] = "done" }, fromUi: true);
                return (JsonNode?)idea.DeepClone();
            }, ct).ConfigureAwait(false);
            return new JsonObject { ["saved"] = marked, ["discarded"] = false, ["marked"] = "done" };
        }

        var saved = await store.UpdateAsync(locator.GlobalFile(), file =>
        {
            var idea = (JsonObject)op["idea"]!.DeepClone();
            var id = IdeaOps.Str(idea["id"]) ?? "";
            if (IdeaOps.Find(file.Ideas, id) is { } already && Same(already, idea)) return (JsonNode?)idea; // a replay
            if (IdeaOps.Find(file.Ideas, id) is not null) idea["id"] = IdeaOps.NewId("idea-", new HashSet<string?>(), 6);
            file.Ideas.Add(idea);
            return (JsonNode?)idea.DeepClone();
        }, ct).ConfigureAwait(false);
        return new JsonObject { ["saved"] = saved, ["discarded"] = false };
    }

    /// <summary>The idea already in the file is the one this answer wrote (same id, title and creation time).</summary>
    private static bool Same(JsonObject stored, JsonObject idea) =>
        IdeaOps.Str(stored["title"]) == IdeaOps.Str(idea["title"]) && IdeaOps.Str(stored["createdAt"]) == IdeaOps.Str(idea["createdAt"]);

    /// <summary>Two titles, compared the way the duplicate-card check needs them.</summary>
    private static bool Same(JsonNode? a, JsonNode? b) => IdeaOps.Str(a) == IdeaOps.Str(b);

    /// <summary>
    /// Finish answers that were interrupted between the journal and the backlog (a crash, a reload, a failed write).
    /// Called at start and before every card is answered or listed; applying an entry twice is harmless, so a recovery
    /// that overlaps a live one changes nothing.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        JsonArray ops;
        try { ops = (JsonArray?)Ops(await ReadPendingAsync(ct).ConfigureAwait(false))?.DeepClone() ?? []; }
        catch (IdeasFileException) { return; } // the file is broken: the cards in it are the user's, leave them
        foreach (var node in ops.OfType<JsonObject>())
        {
            var id = IdeaOps.Str(node["id"]) ?? "";
            var cardId = IdeaOps.Str(node["cardId"]) ?? "";
            try
            {
                if (node["applied"] is not JsonValue v || !v.TryGetValue<bool>(out var applied) || !applied)
                    await ApplyAsync(node, ct).ConfigureAwait(false);
                var card = await MutateAsync(f =>
                {
                    Remove(f, cardId);
                    DropOp(f, id);
                    return (JsonNode?)Card(f, cardId)?.DeepClone();
                }, ct).ConfigureAwait(false);
                PublishResolved(cardId, IdeaOps.Str(node["action"]), card);
                ctx.Logger.LogInformation("Ideas: finished the interrupted answer of card {Card} ({Action})", cardId, IdeaOps.Str(node["action"]));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.Logger.LogWarning("Ideas: the interrupted answer of card {Card} is still unfinished: {Message}", cardId, ex.Message);
            }
        }
    }

    private void PublishResolved(string? id, string? action, JsonNode? card) =>
        ctx.Events.Publish(ResolvedEvent, new JsonObject { ["id"] = id, ["action"] = action, ["card"] = card?.DeepClone() });

    private async Task DropOpQuietlyAsync(string? opId, CancellationToken ct)
    {
        try { await MutateAsync(f => { DropOp(f, opId); return true; }, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { ctx.Logger.LogWarning("Ideas: the journal entry {Op} could not be taken out: {Message}", opId, ex.Message); }
    }

    private static JsonObject? Card(JsonObject pending, string? id)
    {
        foreach (var node in (pending["suggestions"] as JsonArray ?? []).OfType<JsonObject>())
            if (IdeaOps.Str(node["id"]) == id) return node;
        return null;
    }

    private static void Remove(JsonObject pending, string? id)
    {
        var list = pending["suggestions"] as JsonArray ?? [];
        for (var k = 0; k < list.Count; k++)
            if (list[k] is JsonObject s && IdeaOps.Str(s["id"]) == id) { list.RemoveAt(k); return; }
    }

    private static JsonArray Ops(JsonObject pending) => pending["ops"] as JsonArray ?? [];

    private static JsonObject? Op(JsonObject pending, string? id)
    {
        foreach (var node in Ops(pending).OfType<JsonObject>())
            if (IdeaOps.Str(node["id"]) == id) return node;
        return null;
    }

    private static void DropOp(JsonObject pending, string? id)
    {
        var ops = Ops(pending);
        for (var k = 0; k < ops.Count; k++)
            if (ops[k] is JsonObject s && IdeaOps.Str(s["id"]) == id) { ops.RemoveAt(k); return; }
    }

    // ------------------------------------------------------------------ the pending file

    // Suggestions and the per-session check marks, in one small file next to the ideas (never inside it: a card is not
    // an idea until the user says so). Written atomically under two locks: this class's own semaphore (the save check
    // and the commit check, which remembers the last commit it read per repository, share the file) and the OS lock
    // every plugin instance takes, so a reload swap cannot interleave a read-modify-write with the instance it is
    // replacing.
    private string PendingFile() => Path.Combine(Path.GetDirectoryName(locator.GlobalFile())!, "ideas-pending.json");

    private Task<T> MutateAsync<T>(Func<JsonObject, T> mutate, CancellationToken ct) => MutatePendingAsync(mutate, ct);

    /// <summary>
    /// The pending file, read, changed and written under the locks above. A call that changes nothing writes nothing.
    /// </summary>
    internal async Task<T> MutatePendingAsync<T>(Func<JsonObject, T> mutate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = PendingFile();
            return await _files.WithFileAsync(file, async token =>
            {
                var root = await ReadPendingAsync(file, token).ConfigureAwait(false);
                var before = root.ToJsonString(PendingOptions);
                var result = mutate(root);
                // Most calls change nothing (a sweep with no new commit, a repository already seen): then the rewrite is pure
                // churn — duplicate mtime, and a duplicate rewrite of the user's cards file for the same content.
                if (root.ToJsonString(PendingOptions) != before)
                    await WritePendingAsync(file, root, token).ConfigureAwait(false);
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The pending file as it is on disk: a plain read that never writes. Listing the cards used to go through a
    /// read-modify-write, so opening the window rewrote the file the user's answers live in.
    /// </summary>
    internal async Task<JsonObject> ReadPendingAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = PendingFile();
            return await _files.WithFileAsync(file, token => ReadPendingAsync(file, token), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The pending file, or a fresh one. A file that cannot be read or has the wrong shape is reported and left alone:
    /// the cards in it are the user's, and replacing them with empty structures would throw them away silently.
    /// </summary>
    private async Task<JsonObject> ReadPendingAsync(string file, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(file)) return New();
                var root = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false),
                    documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
                if (root is null) throw new IdeasFileException($"{file} must contain a JSON object.");
                Check(root, file);
                return root;
            }
            catch (Exception ex) when (ex is JsonException or IOException && attempt < 2) { last = ex; await Task.Delay(50, ct).ConfigureAwait(false); }
        }
        throw new IdeasFileException($"{file} cannot be read ({last?.Message}). The cards in it are left alone; fix or delete the file.");
    }

    /// <summary>
    /// The shape of the pending file. A missing key is fine (an older version, or a file being written); a key of the
    /// wrong type is not — that is a file we do not understand, and writing it back would destroy whatever it holds.
    /// </summary>
    private static void Check(JsonObject root, string file)
    {
        foreach (var (key, array) in new[] { ("suggestions", true), ("ops", true), ("checked", false), ("repos", false) })
        {
            var node = root[key];
            if (node is null) { root[key] = array ? new JsonArray() : new JsonObject(); continue; }
            if (array ? node is not JsonArray : node is not JsonObject)
                throw new IdeasFileException($"{file}: \"{key}\" must be {(array ? "an array" : "an object")}. Fix the file; it will not be overwritten.");
        }
    }

    private static JsonObject New() => new()
    {
        ["suggestions"] = new JsonArray(),
        ["ops"] = new JsonArray(),
        ["checked"] = new JsonObject(),
        ["repos"] = new JsonObject(),
    };

    private static readonly JsonSerializerOptions PendingOptions = new() { WriteIndented = true };

    private static Task WritePendingAsync(string file, JsonObject root, CancellationToken ct) =>
        FileGate.WriteAtomicAsync(file, System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(PendingOptions)), ct);

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
                    lines.Add("User: " + Clip(Plain(m), 1500));
                    break;
                case MessageRole.Assistant:
                    var text = Plain(m);
                    var tools = m.Parts.OfType<ToolCallPart>().Select(c => c.Name).ToList();
                    if (text.Length == 0 && tools.Count == 0) break;
                    var line = "Agent: " + Clip(text, 800);
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

    private async Task<List<JsonObject>> OpenAsync(string? projectId, CancellationToken ct)
    {
        try
        {
            return await store.ReadAsync(locator.GlobalFile(), f => IdeaOps.All(f.Ideas)
                .Where(i => IdeaOps.Str(i["status"]) is not ("done" or "rejected"))
                .Where(i => IdeaOps.MatchesProject(i, projectId, includeUnbound: true))
                .Select(i => (JsonObject)i.DeepClone())
                .ToList(), ct).ConfigureAwait(false);
        }
        catch (IdeasFileException ex) { throw new RpcException("invalid_file", ex.Message); }
    }

    /// <summary>
    /// The model the check runs on: exactly the one the user configured. There is no fallback to whatever else is in
    /// the catalog — a check that silently runs on another model (a paid one, or a much weaker one) is worse than no
    /// check, and the user is told instead.
    /// </summary>
    private async Task<ModelInfo?> ResolveModelAsync(CancellationToken ct)
    {
        var name = Setting("ideas.model", DefaultModel) is { Length: > 0 } m ? m.Trim() : DefaultModel;
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

    private static int Tries(JsonObject mark) => mark["tries"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    /// <summary>An idea timestamp (an ISO string, <see cref="IdeaOps.Now"/>); null when it is missing or not one.</summary>
    private static DateTimeOffset? Stamp(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at : null;

    private static string OneLine(string? s) => string.Join(' ', (s ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
