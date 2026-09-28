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
    public const string SuggestedEvent = "ideas.suggested";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // Single-token letters for the options (the last one used is "none"); more open ideas than letters: the newest stay out.
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // The digest the two checks read: what the user asked and what came back, tools by name. Never the raw transcript:
    // the measured runs used a digest, and a whole 200-turn session would not fit the check's window anyway.
    private const int MaxChars = 12000;

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

    public void Register(IRpcRegistry rpc)
    {
        rpc.Register("ideas.closed", Closed,
            "A session tab was closed: { sessionId } → { checked: bool, reason } — attaches the chat to the idea it worked on and offers a card when it leaves an unsaved plan (background)");
        rpc.Register("ideas.suggestions", Suggestions,
            "Cards waiting for the user (a plan a closed chat left unsaved): { } → { suggestions: [...] }");
        rpc.Register("ideas.resolve", Resolve,
            "Answer a card: { id, action: \"save\" | \"discard\", edit?: { title?, summary? } } → { saved: idea | null }");
    }

    // ------------------------------------------------------------------ the check

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
        if (messages.Any(m => m.Role == MessageRole.Assistant && m.StopReason is "aborted" or "error")) return Result("unfinished");

        // One check per message count: a tab closed, reopened and closed again does not ask twice, but new turns earn a
        // new one. Read, mark and prune in one pass over the file: _gate is not reentrant, so this is one call, not three.
        var already = await MutateAsync(root =>
        {
            var marks = root["checked"] as JsonObject;
            if (marks is null) root["checked"] = marks = new JsonObject();
            if (marks[sessionId] is JsonObject seen && (seen["n"]?.GetValue<int>() ?? 0) >= users) return true;
            marks[sessionId] = new JsonObject { ["n"] = users, ["at"] = IdeaOps.Now() };
            var cutoff = DateTimeOffset.UtcNow.AddDays(-MarkKeepDays);
            foreach (var key in marks.Select(p => p.Key).ToList())
                if (marks[key] is JsonObject m && Stamp(m["at"]) is { } at && at < cutoff)
                    marks.Remove(key);
            return false;
        }, ct).ConfigureAwait(false);
        if (already) return Result("already");

        // The tab is already gone: everything from here runs in the background and only the cards are left.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        ctx.Track(cts);
        _ = Task.Run(() => RunAsync(session, messages, cts.Token), CancellationToken.None);
        return Result("started");
    }

    private async Task RunAsync(SessionInfo session, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var digest = Digest(messages);
        if (digest.Length < 80) return;
        var project = session.ProjectId is { } pid ? ctx.Sessions.GetProject(pid) : null;

        try
        {
            var open = await OpenAsync(project?.Id, ct).ConfigureAwait(false);
            if (open.Count > 0 && ctx.Rpc.Exists("decide.decision"))
                await AttachAsync(open, session, digest, ct).ConfigureAwait(false);

            if (!ctx.Models.Cached.Any()) await ctx.Models.ListAsync(ct: ct).ConfigureAwait(false);
            var draft = await SaveCheckAsync(session, digest, ct).ConfigureAwait(false);
            if (draft is null) return;

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

            await MutateAsync<object?>(f => { f["suggestions"]!.AsArray().Add(suggestion); return null; }, ct).ConfigureAwait(false);
            ctx.Events.Publish(SuggestedEvent, new JsonObject { ["suggestion"] = suggestion.DeepClone() });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning("Ideas: the save check on {Session} failed: {Message}", session.Id, ex.Message);
        }
    }

    /// <summary>Which open idea the chat worked on: a <c>sessions</c> entry on that idea, no question asked.</summary>
    private async Task AttachAsync(List<JsonObject> open, SessionInfo session, string digest, CancellationToken ct)
    {
        var candidates = open.Take(Letters.Length - 1).ToList();
        var none = Letters[candidates.Count].ToString();
        var list = new StringBuilder();
        for (var k = 0; k < candidates.Count; k++)
        {
            list.Append(Letters[k]).Append(") [").Append(IdeaOps.ProjectLabel(candidates[k])).Append("] ").Append(IdeaOps.Str(candidates[k]["title"]));
            if (OneLine(IdeaOps.Str(candidates[k]["summary"])) is { Length: > 0 } s) list.Append(" — ").Append(Clip(s, 240));
            list.Append('\n');
        }
        list.Append(none).Append(") none of these: the conversation is about something else");

        var answer = await DecideAsync(new JsonObject
        {
            ["role"] = "system",
            ["content"] = "You decide which of the user's backlog of open ideas a finished conversation with an AI coding " +
                          "agent was about, so the conversation can be attached to it. Reply with the letter of the best option only.\n\n" +
                          "Open ideas:\n" + list,
        }, "Which open idea is this conversation about? " +
           "Pick it when the conversation worked on it (built, tested, discussed or changed its plan), " +
           $"pick {none} when it is about something else. Answer with one letter.",
            Enumerable.Range(0, candidates.Count + 1).Select(k => Letters[k].ToString()).ToArray(), ct).ConfigureAwait(false);
        if (answer is null) return;
        var best = candidates[answer.Value.Index];
        if (answer.Value.P < Math.Clamp(Setting("ideas.attachThreshold", DefaultAttachThreshold), 0.3, 0.99)
            || answer.Value.P <= answer.Value.None) return;

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
            if (IdeaOps.Str(best["id"]) is { Length: > 0 } id && IdeaOps.Find(f.Ideas, id) is { } found)
                IdeaOps.AddSessionEntry(found, entry);
            return null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The generative check: NOTHING, or a drafted title and summary.</summary>
    private async Task<(string Title, string Summary)?> SaveCheckAsync(SessionInfo session, string digest, CancellationToken ct)
    {
        var model = await ResolveModelAsync(ct).ConfigureAwait(false);
        if (model is null)
        {
            ctx.Logger.LogWarning("Ideas: the save check cannot run: model {Model} is not in the catalog.", Setting("ideas.model", DefaultModel));
            return null;
        }
        var maxOut = Math.Clamp(model.MaxOutputTokens is > 0 and var m ? Math.Min(m, 1024) : 1024, 256, 4096);
        var request = new ModelRequest
        {
            Model = model,
            SystemPrompt = SaveSystem,
            Messages = [ChatMessage.UserText($"Conversation with the agent:\n<<<\n{digest}\n>>>\n\nDoes it leave something worth keeping?")],
            ReasoningEffort = EffortFor(model, "low"),
            MaxOutputTokens = maxOut,
            SessionId = session.Id,
            Purpose = "other",
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
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

    private async Task<(int Index, double P, double None)?> DecideAsync(JsonObject system, string question, string[] labels, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var raw = await ctx.Rpc.InvokeAsync("decide.decision", new JsonObject
            {
                ["model"] = Setting("ideas.model", DefaultModel) is { Length: > 0 } m ? m.Trim() : DefaultModel,
                ["messages"] = new JsonArray(system),
                ["branches"] = new JsonArray(new JsonObject { ["id"] = "pick", ["content"] = question, ["labels"] = new JsonArray(labels.Select(l => (JsonNode)l!).ToArray()) }),
            }, cts.Token).ConfigureAwait(false);
            var answer = raw as JsonObject ?? JsonSerializer.SerializeToNode(raw) as JsonObject;
            if (answer?["branches"] is not JsonArray { Count: > 0 } branches || branches[0]?["probabilities"] is not JsonObject probs) return null;
            double P(string label) => probs[label] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
            var index = -1;
            for (var k = 0; k < labels.Length; k++)
                if (index < 0 || P(labels[k]) > P(labels[index])) index = k;
            return (index, P(labels[index]), P(labels[^1]));
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

    public async Task<object?> Suggestions(RpcRequest req, CancellationToken ct) =>
        new JsonObject { ["suggestions"] = await MutateAsync(f => (JsonNode)f["suggestions"]!.DeepClone(), ct).ConfigureAwait(false) };

    public async Task<object?> Resolve(RpcRequest req, CancellationToken ct)
    {
        var id = req.Required("id");
        var action = (req.Str("action") ?? "").Trim().ToLowerInvariant();
        if (action is not ("save" or "discard")) throw new RpcException("bad_request", "action must be \"save\" or \"discard\"");
        var edit = req.Prop("edit") is { ValueKind: JsonValueKind.Object } e ? JsonObject.Create(e.Clone()) : null;

        JsonObject? found = null;
        await MutateAsync<object?>(f =>
        {
            var list = f["suggestions"]!.AsArray();
            for (var k = 0; k < list.Count; k++)
                if (list[k] is JsonObject s && IdeaOps.Str(s["id"]) == id) { found = s; list.RemoveAt(k); break; }
            return null;
        }, ct).ConfigureAwait(false);
        if (found is null) throw new RpcException("not_found", "That card is gone (already answered, or NetPI restarted).");

        if (action == "discard") return new JsonObject { ["saved"] = null, ["discarded"] = true };
        if (edit is not null)
        {
            if (edit["title"] is JsonValue t && t.TryGetValue<string>(out var t2) && t2.Trim().Length > 0) found["title"] = t2.Trim();
            if (edit["summary"] is JsonValue sv && sv.TryGetValue<string>(out var s2)) found["summary"] = s2.Trim();
        }
        var title = (IdeaOps.Str(found["title"]) ?? "").Trim();
        if (title.Length == 0) throw new RpcException("bad_request", "An idea needs a title.");
        var project = found["project"] as JsonObject;
        var sessionId = IdeaOps.Str(found["sessionId"]);
        var sessionTitle = IdeaOps.Str(found["sessionTitle"]);

        return new JsonObject
        {
            ["discarded"] = false,
            ["saved"] = await store.UpdateAsync(locator.GlobalFile(), file =>
            {
                var idea = IdeaOps.CreateIdea(new JsonObject
                {
                    ["title"] = title,
                    ["summary"] = IdeaOps.Str(found["summary"]),
                }, file.Ideas, "user", sessionId is { Length: > 0 } sid ? sid : null, keepExtraFields: true);
                IdeaOps.SetProject(idea,
                    project?["id"] is JsonValue pid && pid.TryGetValue<string>(out var id) ? id : null,
                    project?["name"] is JsonValue pname && pname.TryGetValue<string>(out var name) ? name : null);
                IdeaOps.AddSessionEntry(idea, new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["title"] = sessionTitle,
                    ["at"] = IdeaOps.Now(),
                    ["seen"] = true,
                });
                file.Ideas.Add(idea);
                return (JsonNode?)idea.DeepClone();
            }, ct).ConfigureAwait(false),
        };
    }

    // ------------------------------------------------------------------ the pending file

    // Suggestions and the per-session check marks, in one small file next to the ideas (never inside it: a card is not
    // an idea until the user says so). Written atomically, under one lock.
    private string PendingFile() => Path.Combine(Path.GetDirectoryName(locator.GlobalFile())!, "ideas-pending.json");

    private async Task<T> MutateAsync<T>(Func<JsonObject, T> mutate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = PendingFile();
            var root = await ReadAsync(file, ct).ConfigureAwait(false);
            var result = mutate(root);
            await WriteAsync(file, root, ct).ConfigureAwait(false);
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The pending file, or a fresh one. A file that cannot be read after three tries (a half-written one from a crash,
    /// or a lock held by another process) is not overwritten with an empty backlog: the cards in it are the user's.
    /// </summary>
    private async Task<JsonObject> ReadAsync(string file, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(file)) return New();
                var root = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false),
                    documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
                if (root is null) return New();
                if (root["suggestions"] is not JsonArray) root["suggestions"] = new JsonArray();
                if (root["checked"] is not JsonObject) root["checked"] = new JsonObject();
                return root;
            }
            catch (Exception ex) when (ex is JsonException or IOException && attempt < 2) { last = ex; await Task.Delay(50, ct).ConfigureAwait(false); }
        }
        ctx.Logger.LogWarning("Ideas: {File} cannot be read, the cards in it are left alone: {Message}", file, last?.Message);
        return New();
    }

    private static JsonObject New() => new() { ["suggestions"] = new JsonArray(), ["checked"] = new JsonObject() };

    private static async Task WriteAsync(string file, JsonObject root, CancellationToken ct)
    {
        var tmp = file + ".tmp";
        await File.WriteAllTextAsync(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        File.Move(tmp, file, overwrite: true); // atomic on the same volume
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The conversation as the checks read it: the user turns whole, the answers and tools clipped.</summary>
    internal static string Digest(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case MessageRole.User:
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("User: ").Append(Clip(Plain(m), 1500));
                    break;
                case MessageRole.Assistant:
                    var text = Plain(m);
                    var tools = m.Parts.OfType<ToolCallPart>().Select(c => c.Name).ToList();
                    if (text.Length == 0 && tools.Count == 0) break;
                    sb.Append("\nAgent: ").Append(Clip(text, 800));
                    if (tools.Count > 0) sb.Append("\n  used: ").Append(string.Join(", ", tools.Distinct()));
                    break;
            }
            if (sb.Length > MaxChars) break;
        }
        return Clip(sb.ToString(), MaxChars);
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

    private async Task<ModelInfo?> ResolveModelAsync(CancellationToken ct)
    {
        var name = Setting("ideas.model", DefaultModel) is { Length: > 0 } m ? m.Trim() : DefaultModel;
        return await ctx.Models.FindAsync(name, ct).ConfigureAwait(false) ?? ctx.Models.Cached.FirstOrDefault();
    }

    /// <summary>The model's lowest reasoning effort when it has any; null is the model default (null = no reasoning block).</summary>
    private static string? EffortFor(ModelInfo model, string wanted)
    {
        var r = model.Reasoning;
        if (r is null || !r.Supported) return null;
        var efforts = r.Efforts;
        return efforts.FirstOrDefault(e => string.Equals(e, wanted, StringComparison.OrdinalIgnoreCase))
            ?? efforts.OrderByDescending(e => Rank(e)).FirstOrDefault();
        static int Rank(string e) => e.ToLowerInvariant() switch { "none" or "off" => 0, "minimal" => 1, "low" => 2, _ => 3 };
    }

    private T Setting<T>(string key, T fallback)
    {
        try { return ctx.Settings.Get(key, fallback) ?? fallback; }
        catch { return fallback; }
    }

    private static JsonObject Result(string reason) => new() { ["checked"] = reason is "started" or "already", ["reason"] = reason };

    /// <summary>An idea timestamp (an ISO string, <see cref="IdeaOps.Now"/>); null when it is missing or not one.</summary>
    private static DateTimeOffset? Stamp(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at : null;

    private static string OneLine(string? s) => string.Join(' ', (s ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
