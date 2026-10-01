using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

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

/// <summary>The system prompt each session got at its first model call (table <c>context_prompts</c>, cached in memory).</summary>
internal sealed class PromptStore(IPluginContext ctx)
{
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reset = new(StringComparer.Ordinal);
    public bool WasReset(string sessionId) => _reset.ContainsKey(sessionId);
    private readonly ConcurrentDictionary<string, ToolBaseline> _tools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<SentPrompt>> _sent = new(StringComparer.Ordinal); // without a database
    private IDatabase? _db;

    public void Initialize()
    {
        try
        {
            var db = ctx.Db;
            db.Migrate("context",
                "CREATE TABLE IF NOT EXISTS context_prompts (session_id TEXT PRIMARY KEY, prompt TEXT NOT NULL, created_at TEXT NOT NULL)",
                // the tool names of a session's first model call: the baseline for "tools" notices
                "CREATE TABLE IF NOT EXISTS context_tools (session_id TEXT PRIMARY KEY, tools TEXT NOT NULL)",
                // a baseline taken later (after context.reset) ignores the "tools" notices up to this seq
                "ALTER TABLE context_tools ADD COLUMN since_seq INTEGER NOT NULL DEFAULT 0",
                // every prompt a session was sent (the first, and one after each context.reset) with its tool definitions
                "CREATE TABLE IF NOT EXISTS context_sent (session_id TEXT NOT NULL, version INTEGER NOT NULL, after_seq INTEGER NOT NULL, " +
                    "prompt TEXT NOT NULL, tools TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY (session_id, version))",
                "ALTER TABLE context_prompts ADD COLUMN prompt_revision INTEGER NOT NULL DEFAULT 0");
            _db = db;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Session prompts table unavailable; frozen prompts are kept in memory only");
        }
    }

    public string? GetCompatible(SessionInfo session)
    {
        if (Get(session.Id) is not { } prompt) return null;
        try
        {
            var stored = _revisions.GetOrAdd(session.Id, id => _db?.Scalar<long?>("SELECT prompt_revision FROM context_prompts WHERE session_id = @id", new { id }) ?? 0);
            if (stored == SessionPrompt.Revision(session)) return prompt;
            Reset(session.Id);
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Reading prompt revision failed for {Session}", session.Id); }
        return null;
    }

    public string? Get(string sessionId)
    {
        if (_cache.TryGetValue(sessionId, out var prompt)) return prompt;
        if (_db is null) return null;
        try { prompt = _db.Scalar<string>("SELECT prompt FROM context_prompts WHERE session_id = @sessionId", new { sessionId }); }
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
        if (!ReferenceEquals(stored, prompt) || _db is null) return stored;
        try
        {
            _db.Execute("INSERT OR IGNORE INTO context_prompts (session_id, prompt, created_at, prompt_revision) VALUES (@sessionId, @prompt, @now, @revision)",
                new { sessionId, prompt, revision, now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Storing the prompt of {Session} failed", sessionId); }
        return stored;
    }

    /// <summary>The tool names of the session's first model call (or the first after a reset), or null before it.</summary>
    public ToolBaseline? GetTools(string sessionId)
    {
        if (_tools.TryGetValue(sessionId, out var baseline)) return baseline;
        if (_db is null) return null;
        try
        {
            var row = _db.QuerySingle("SELECT tools, since_seq FROM context_tools WHERE session_id = @sessionId", new { sessionId },
                r => (Tools: r.GetString("tools"), Since: r.GetInt64("since_seq")));
            if (row.Tools is null) return null;
            var names = (JsonNode.Parse(row.Tools) as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? [];
            return _tools.GetOrAdd(sessionId, new ToolBaseline(names, row.Since));
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
        if (!ReferenceEquals(stored, baseline) || _db is null) return stored;
        try
        {
            _db.Execute("INSERT OR IGNORE INTO context_tools (session_id, tools, since_seq) VALUES (@sessionId, @tools, @sinceSeq)",
                new { sessionId, tools = new JsonArray(names.Select(n => (JsonNode?)n).ToArray()).ToJsonString(), sinceSeq });
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
        var toolsJson = new JsonArray([.. tools.Select(t => (JsonNode?)new JsonObject
        {
            ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone(), ["revision"] = t.Revision,
        })]).ToJsonString();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (_db is null)
        {
            var list = _sent.GetOrAdd(sessionId, _ => []);
            lock (list)
            {
                list.Add(new SentPrompt(list.Count + 1, afterSeq, now, prompt, toolsJson));
                return list.Count;
            }
        }
        try
        {
            var version = (int)(_db.Scalar<long?>("SELECT MAX(version) FROM context_sent WHERE session_id = @sessionId", new { sessionId }) ?? 0) + 1;
            _db.Execute("INSERT INTO context_sent (session_id, version, after_seq, prompt, tools, created_at) VALUES (@sessionId, @version, @afterSeq, @prompt, @toolsJson, @now)",
                new { sessionId, version, afterSeq, prompt, toolsJson, now });
            return version;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Keeping the prompt of {Session} failed", sessionId);
            return 0;
        }
    }

    /// <summary>The prompts the session was sent, oldest first (see <see cref="RecordSent"/>).</summary>
    public List<SentPrompt> Sent(string sessionId)
    {
        if (_db is null)
        {
            if (!_sent.TryGetValue(sessionId, out var list)) return [];
            lock (list) return [.. list];
        }
        try
        {
            return _db.Query("SELECT version, after_seq, created_at, prompt, tools FROM context_sent WHERE session_id = @sessionId ORDER BY version",
                new { sessionId }, r => new SentPrompt((int)r.GetInt64("version"), r.GetInt64("after_seq"), r.GetString("created_at"),
                    r.GetString("prompt"), r.GetString("tools")));
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
        if (_db is null) return;
        try
        {
            _db.Execute("DELETE FROM context_prompts WHERE session_id = @sessionId", new { sessionId });
            _db.Execute("DELETE FROM context_tools WHERE session_id = @sessionId", new { sessionId });
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
        if (_db is null)
        {
            if (sent.Count == 0) return;
            var list = _sent.GetOrAdd(to, _ => []);
            lock (list)
                if (list.Count == 0) list.AddRange(sent);
            return;
        }
        try
        {
            _db.Execute("""
                INSERT OR IGNORE INTO context_sent (session_id, version, after_seq, prompt, tools, created_at)
                SELECT @to, version, after_seq, prompt, tools, created_at FROM context_sent WHERE session_id = @from AND after_seq <= @upToSeq
                """, new { to, from, upToSeq });
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Copying the prompts of {Session} to its fork failed", from); }
    }

    /// <summary>A deleted session: its prompts and tool baseline, and what it was sent.</summary>
    public void Delete(string sessionId)
    {
        Reset(sessionId);
        _sent.TryRemove(sessionId, out _);
        if (_db is null) return;
        try { _db.Execute("DELETE FROM context_sent WHERE session_id = @sessionId", new { sessionId }); }
        catch (Exception ex) { ctx.Logger.LogDebug(ex, "Deleting the prompts of {Session} failed", sessionId); }
    }
}

/// <summary>The tools a session's prompt was rendered with, and the last message seq at that time.</summary>
internal sealed record ToolBaseline(IReadOnlyList<string> Names, long SinceSeq);

/// <summary>A prompt a session was sent: its version, the last message seq before it, when, the prompt and the tools (JSON).</summary>
internal sealed record SentPrompt(int Version, long AfterSeq, string CreatedAt, string Prompt, string ToolsJson);
