using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

// The collections this plugin keeps in its own storage (ctx.Data); PromptStore is the only code that touches them. A one-off
// migration of an existing home is written from this block: name, key, document fields, index fields.
//   context_prompts  key: session id                     index fields: (none)
//       { prompt: string, createdAt: string, promptRevision: int }
//   context_tools    key: session id                     index fields: (none)
//       { tools: string[], sinceSeq: int }
//   context_sent     key: "<sessionId>:<version>"        index fields: sessionId (text), version (int), afterSeq (int)
//       { sessionId: string, version: int, afterSeq: int, prompt: string, createdAt: string, inherited: true,
//         tools: [{ name: string, description: string, parameters: object, revision: string }] }
//     `inherited` is set only on the rows a fork copied from the session it was forked from (they were sent by that one),
//     so a fork's own first call does not record the same prompt beside them.

namespace NetPI.Context;

/// <summary>
/// Renders every registered <see cref="IPromptSection"/> in ascending order, skipping empty ones, once per session: the
/// prompt a session got at its first model call is stored and reused for every later call. The system prompt starts
/// every request, and changing it would make the backend re-prefill the whole conversation, so settings and plugin
/// changes apply to new sessions and state changes during a session arrive as notices.
/// </summary>
internal sealed class SystemPromptBuilder(IPluginContext ctx, PromptStore prompts) : ISystemPromptBuilder
{
    public const string SentEvent = "context.prompt";

    public async ValueTask<string> BuildAsync(PromptContext context, CancellationToken ct)
    {
        var sessionId = context.Session.Id;
        var capturedRevision = SessionPrompt.Revision(context.Session);
        if (prompts.GetCompatible(context.Session) is { } frozen) return frozen;
        // a fork's first call: the prompt the original had at the fork point (session.forked copies it too, but may come later)
        if (SessionPrompt.Fallback(context.Session) is null && !prompts.WasReset(sessionId) && PromptStore.ForkedFrom(context.Session) is { } fork)
        {
            prompts.Fork(fork.SessionId, sessionId, fork.Seq);
            if (prompts.Get(sessionId) is { } copied) return copied;
        }
        var rendered = SessionPrompt.Fallback(context.Session) ?? await RenderAsync(context, ct).ConfigureAwait(false);
        if (ctx.Sessions.GetSession(sessionId) is { } current && SessionPrompt.Revision(current) != capturedRevision)
            throw new InvalidOperationException("The session identity changed while its prompt was rendered; render the current revision.");
        var stored = prompts.Freeze(sessionId, rendered, capturedRevision);
        if (ReferenceEquals(stored, rendered))
        {
            // this call sends a new prompt: keep it with the tools it goes with, for the chat to show (context.prompts)
            long afterSeq = 0;
            try { afterSeq = ctx.Sessions.GetMessages(sessionId, null, 1) is [.., var last] ? last.Seq : 0; } catch { }
            var version = prompts.RecordSent(sessionId, rendered, context.Tools, afterSeq);
            ctx.Events.Publish(new BusEvent
            {
                Type = SentEvent, SessionId = sessionId,
                Data = new JsonObject { ["sessionId"] = sessionId, ["version"] = version, ["afterSeq"] = afterSeq },
            });
        }
        return stored;
    }

    /// <summary>What a session is sent: its stored prompt, or (before its first model call) a fresh render that is not stored.</summary>
    public async ValueTask<(string Prompt, bool Frozen)> PreviewAsync(PromptContext context, CancellationToken ct) =>
        prompts.GetCompatible(context.Session) is { } frozen ? (frozen, true) : (await RenderAsync(context, ct).ConfigureAwait(false), false);

    public async ValueTask<string> RenderAsync(PromptContext context, CancellationToken ct)
    {
        // GetAll returns highest registration priority first; OrderBy is stable, so equal orders keep that.
        var sections = ctx.Services.GetAll<IPromptSection>().OrderBy(s => s.Order).ToList();
        var parts = new List<string>(sections.Count);
        foreach (var section in sections)
        {
            ct.ThrowIfCancellationRequested();
            string? text;
            try
            {
                text = await section.RenderAsync(context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Prompt section {Section} failed", section.Id);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
        }
        return string.Join("\n\n", parts);
    }
}

/// <summary>
/// The system prompt each session got at its first model call, in the plugin's own collections (see the file's header for
/// their shape), cached in memory. Every call is best effort: the store is required, a failing one only costs what it
/// wrote.
/// </summary>
internal sealed class PromptStore(IPluginContext ctx)
{
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reset = new(StringComparer.Ordinal);
    public bool WasReset(string sessionId) => _reset.ContainsKey(sessionId);
    private readonly ConcurrentDictionary<string, ToolBaseline> _tools = new(StringComparer.Ordinal);
    // the name→revision of the tools of the last prompt a session was sent: the per-turn definition-notice diff compares
    // against it, so an unchanged session does not re-read and re-parse its sent prompts on every model call (idea-l1o09d)
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _lastSent = new(StringComparer.Ordinal);
    private IDataCollection _prompts = null!;
    private IDataCollection _baselines = null!;
    private IDataCollection _sent = null!;

    /// <summary>Open the collections (the store creates what it does not have).</summary>
    public void Initialize()
    {
        _prompts = ctx.Data.Collection("context_prompts", new CollectionSpec());
        // the tool names of a session's first model call: the baseline for "tools" notices. A baseline taken later
        // (after context.reset) ignores the "tools" notices up to its sinceSeq.
        _baselines = ctx.Data.Collection("context_tools", new CollectionSpec());
        // every prompt a session was sent (the first, and one after each context.reset) with its tool definitions
        _sent = ctx.Data.Collection("context_sent", new CollectionSpec().Text("sessionId").Integer("version").Integer("afterSeq"));
    }

    private static long Number(JsonObject? doc, string field, long fallback = 0) =>
        doc?[field] is JsonValue v
            // the value comes back as the type it was written with: int in memory, long from a column
            ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : fallback
            : fallback;

    private static string? Text(JsonObject? doc, string field) =>
        doc?[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The key of one sent prompt: the session and the version it was sent as (session ids are a prefix and 12 alphanumerics).</summary>
    private static string SentKey(string sessionId, int version) => $"{sessionId}:{version}";

    public string? GetCompatible(SessionInfo session)
    {
        if (Get(session.Id) is not { } prompt) return null;
        try
        {
            var stored = _revisions.GetOrAdd(session.Id, id => Number(_prompts.Get(id), "promptRevision"));
            if (stored == SessionPrompt.Revision(session)) return prompt;
            Reset(session.Id);
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Reading prompt revision failed for {Session}", session.Id); }
        return null;
    }

    public string? Get(string sessionId)
    {
        if (_cache.TryGetValue(sessionId, out var prompt)) return prompt;
        try { prompt = Text(_prompts.Get(sessionId), "prompt"); }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Reading the stored prompt of {Session} failed", sessionId);
            return null;
        }
        return prompt is null ? null : _cache.GetOrAdd(sessionId, prompt);
    }

    /// <summary>Store a session's first prompt; when another call stored one first, that one is returned.</summary>
    public string Freeze(string sessionId, string prompt, long? capturedRevision = null)
    {
        var stored = _cache.GetOrAdd(sessionId, prompt);
        var revision = capturedRevision ?? (ctx.Sessions.GetSession(sessionId) is { } session ? SessionPrompt.Revision(session) : 0);
        _revisions.TryAdd(sessionId, revision);
        if (!ReferenceEquals(stored, prompt)) return stored;
        try
        {
            _prompts.Insert(sessionId, new JsonObject
            {
                ["prompt"] = prompt, ["createdAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["promptRevision"] = revision,
            });
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Storing the prompt of {Session} failed", sessionId); }
        return stored;
    }

    /// <summary>The tool names of the session's first model call (or the first after a reset), or null before it.</summary>
    public ToolBaseline? GetTools(string sessionId)
    {
        if (_tools.TryGetValue(sessionId, out var baseline)) return baseline;
        try
        {
            var doc = _baselines.Get(sessionId);
            if (doc is null) return null;
            var names = (doc["tools"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() ?? [];
            return _tools.GetOrAdd(sessionId, new ToolBaseline(names, Number(doc, "sinceSeq")));
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Reading the tool baseline of {Session} failed", sessionId);
            return null;
        }
    }

    /// <summary>
    /// Store the tool names of the session's first model call; <paramref name="sinceSeq"/> is the last message seq then
    /// (older "tools" notices do not apply to this baseline). Returns the stored baseline (the first one wins).
    /// </summary>
    public ToolBaseline FreezeTools(string sessionId, IReadOnlyList<string> names, long sinceSeq = 0)
    {
        var baseline = new ToolBaseline(names, sinceSeq);
        var stored = _tools.GetOrAdd(sessionId, baseline);
        if (!ReferenceEquals(stored, baseline)) return stored;
        try
        {
            _baselines.Insert(sessionId, new JsonObject
            {
                ["tools"] = new JsonArray(names.Select(n => (JsonNode?)n).ToArray()), ["sinceSeq"] = sinceSeq,
            });
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Storing the tool baseline of {Session} failed", sessionId); }
        return stored;
    }

    /// <summary>
    /// Keep a prompt the session is sent from now on, with the definitions of the tools it goes with and the last message
    /// seq before it; returns its version (1 for the first).
    /// </summary>
    public int RecordSent(string sessionId, string prompt, IReadOnlyList<ToolDefinition> tools, long afterSeq)
    {
        var definitions = new JsonArray([.. tools.Select(t => (JsonNode?)new JsonObject
        {
            ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone(), ["revision"] = t.Revision,
        })]);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        try
        {
            // the version is the highest the session has plus one: read it and write the row in one transaction
            var version = ctx.Data.Transaction(() =>
            {
                // A fork goes on with the prompt it inherited, which the rows it copied already hold. Its own first call
                // renders that same text as its own instance, so it cannot tell it is the one it inherited (Freeze keeps
                // the copy's instance) and records it as a prompt the fork was sent: without this the fork's history has
                // that prompt twice, and every later fork of it inherits both.
                if (NewestSent(sessionId) is { Inherited: true } inherited && inherited.Prompt == prompt) return inherited.Version;
                var v = NextVersion(sessionId);
                _sent.Insert(SentKey(sessionId, v), new JsonObject
                {
                    ["sessionId"] = sessionId, ["version"] = v, ["afterSeq"] = afterSeq,
                    ["prompt"] = prompt, ["tools"] = definitions, ["createdAt"] = now,
                });
                return v;
            });
            if (version > 0)
            {
                // the last prompt the session was sent, by name and revision: the per-turn definition diff reads it in memory
                var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var t in tools) if (t.Revision is { } r) revisions[t.Name] = r;
                _lastSent[sessionId] = revisions;
            }
            return version;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Keeping the prompt of {Session} failed", sessionId);
            return 0;
        }
    }

    /// <summary>The version the next prompt a session is sent gets: one past the highest it already has.</summary>
    private int NextVersion(string sessionId) => (NewestSent(sessionId)?.Version ?? 0) + 1;

    /// <summary>The newest row a session was sent, or null when it has been sent none.</summary>
    private SentRow? NewestSent(string sessionId)
    {
        var doc = _sent.Find(new DataQuery().Eq("sessionId", sessionId).Order("version", descending: true).Take(1)).FirstOrDefault()?.Doc;
        return doc is null ? null : new SentRow((int)Number(doc, "version"), Text(doc, "prompt") ?? "", doc["inherited"]?.GetValue<bool>() ?? false);
    }

    /// <summary>One row of <c>context_sent</c>: which version it is, the prompt it holds and whether a fork copied it.</summary>
    private sealed record SentRow(int Version, string Prompt, bool Inherited);

    /// <summary>
    /// The name→revision of the tools of the last prompt a session was sent (see <see cref="RecordSent"/>): kept in memory so
    /// the per-turn definition-notice diff does not re-read and re-parse <c>context_sent</c> on every model call (idea-l1o09d).
    /// </summary>
    public IReadOnlyDictionary<string, string>? LastSentRevisions(string sessionId)
    {
        if (_lastSent.TryGetValue(sessionId, out var cached)) return cached;
        try
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var last = _sent.Find(new DataQuery().Eq("sessionId", sessionId).Order("version", descending: true).Take(1)).FirstOrDefault();
            foreach (var entry in (last?.Doc["tools"] as JsonArray)?.OfType<JsonObject>() ?? [])
                if (entry["name"] is JsonValue v && v.TryGetValue<string>(out var name)
                    && entry["revision"] is JsonValue r && r.TryGetValue<string>(out var revision)) map[name] = revision;
            _lastSent[sessionId] = map;
            return map;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Reading the last-sent tool revisions of {Session} failed", sessionId);
            return null;
        }
    }

    /// <summary>The prompts the session was sent, oldest first (see <see cref="RecordSent"/>).</summary>
    public List<SentPrompt> Sent(string sessionId)
    {
        try
        {
            return [.. _sent.Find(new DataQuery().Eq("sessionId", sessionId).Order("version")).Select(d => new SentPrompt(
                (int)Number(d.Doc, "version"), Number(d.Doc, "afterSeq"), Text(d.Doc, "createdAt") ?? "",
                Text(d.Doc, "prompt") ?? "", d.Doc["tools"]?.ToJsonString() ?? "[]"))];
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Reading the prompts of {Session} failed", sessionId);
            return [];
        }
    }

    /// <summary><c>context.reset</c>: forget the session's current prompt and tool baseline; the next call renders them again.</summary>
    public void Reset(string sessionId)
    {
        _reset[sessionId] = 0;
        _cache.TryRemove(sessionId, out _);
        _revisions.TryRemove(sessionId, out _);
        _tools.TryRemove(sessionId, out _);
        try
        {
            _prompts.Delete(sessionId);
            _baselines.Delete(sessionId);
        }
        catch (Exception ex) { ctx.Logger.LogDebug(ex, "Resetting the prompt of {Session} failed", sessionId); }
    }

    /// <summary>A fork's origin (<c>meta.forkedFrom</c>, see <c>sessions.fork</c>): the session and the last seq it copied.</summary>
    public static (string SessionId, long Seq)? ForkedFrom(SessionInfo session) =>
        session.Meta?["forkedFrom"] is JsonObject f && f["sessionId"] is JsonValue id && id.TryGetValue<string>(out var from) && from.Length > 0
            ? (from, f["seq"] is JsonValue s && s.TryGetValue<long>(out var seq) ? seq : 0)
            : null;

    /// <summary>
    /// A fork (<c>session.forked</c>) goes on with the prompt the original was sent at the fork point (the latest version
    /// sent after a message up to <paramref name="upToSeq"/>) and that call's tools as its baseline, so its next call starts
    /// with the prefix the backend saw; the prompts sent up to then are its history. Nothing when the original had no model
    /// call yet (the fork renders its own). Idempotent: a stored prompt, baseline or version is kept.
    /// </summary>
    public void Fork(string from, string to, long upToSeq)
    {
        if (ctx.Sessions.GetSession(to)?.Meta?[SessionPrompt.ForkResetKey]?.GetValue<bool>() == true) return;
        // Runtime history spans gaps when Context was absent; retain any available Context tool/history records too.
        var inherited = ctx.Sessions.GetSession(to) is { } target ? SessionPrompt.Fallback(target) : null;
        var all = Sent(from);
        var sent = all.Where(p => p.AfterSeq <= upToSeq).ToList();
        string? prompt = inherited;
        ToolBaseline? tools = null;
        if (sent.Count > 0)
        {
            var v = sent[^1];
            prompt ??= v.Prompt;
            try
            {
                var names = (JsonNode.Parse(v.ToolsJson) as JsonArray)?.Select(t => t?["name"]?.GetValue<string>()).OfType<string>().ToList();
                if (names is not null) tools = new ToolBaseline(names, v.AfterSeq);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
        }
        else if (all.Count == 0 && Get(from) is { } current)
        {
            // a session from before the prompts it was sent were kept: its prompt, and its baseline when it predates the fork
            prompt ??= current;
            if (GetTools(from) is { } b && b.SinceSeq <= upToSeq) tools = b;
        }
        if (prompt is null) return;

        Freeze(to, prompt);
        if (tools is not null) FreezeTools(to, tools.Names, tools.SinceSeq);
        try
        {
            // the prompts sent up to the fork point, with the versions they were sent as: one transaction, and a
            // version the fork already has is kept
            ctx.Data.Transaction(() =>
            {
                foreach (var row in _sent.Find(new DataQuery().Eq("sessionId", from).Le("afterSeq", upToSeq).Order("version")))
                {
                    var copy = (JsonObject)row.Doc.DeepClone();
                    copy["sessionId"] = to;
                    copy["inherited"] = true;   // sent by the session it was forked from, not by this one
                    _sent.Insert(SentKey(to, (int)Number(copy, "version")), copy);
                }
            });
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Copying the prompts of {Session} to its fork failed", from); }
    }

    /// <summary>A deleted session: its prompts and tool baseline, and what it was sent.</summary>
    public void Delete(string sessionId)
    {
        Reset(sessionId);
        _reset.TryRemove(sessionId, out _);   // Reset only ever writes it: without this it was the one entry a deleted chat left behind
        _lastSent.TryRemove(sessionId, out _);
        try { _sent.DeleteWhere(new DataQuery().Eq("sessionId", sessionId)); }
        catch (Exception ex) { ctx.Logger.LogDebug(ex, "Deleting the prompts of {Session} failed", sessionId); }
    }
}

/// <summary>The tools a session's prompt was rendered with, and the last message seq at that time.</summary>
internal sealed record ToolBaseline(IReadOnlyList<string> Names, long SinceSeq);

/// <summary>A prompt a session was sent: its version, the last message seq before it, when, the prompt and the tools (JSON).</summary>
internal sealed record SentPrompt(int Version, long AfterSeq, string CreatedAt, string Prompt, string ToolsJson);
