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
    public async ValueTask<string> BuildAsync(PromptContext context, CancellationToken ct)
    {
        if (prompts.Get(context.Session.Id) is { } frozen) return frozen;
        return prompts.Freeze(context.Session.Id, await RenderAsync(context, ct).ConfigureAwait(false));
    }

    /// <summary>What a session is sent: its stored prompt, or (before its first model call) a fresh render that is not stored.</summary>
    public async ValueTask<(string Prompt, bool Frozen)> PreviewAsync(PromptContext context, CancellationToken ct) =>
        prompts.Get(context.Session.Id) is { } frozen ? (frozen, true) : (await RenderAsync(context, ct).ConfigureAwait(false), false);

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
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _tools = new(StringComparer.Ordinal);
    private IDatabase? _db;

    public void Initialize()
    {
        try
        {
            var db = ctx.Db;
            db.Migrate("context",
                "CREATE TABLE IF NOT EXISTS context_prompts (session_id TEXT PRIMARY KEY, prompt TEXT NOT NULL, created_at TEXT NOT NULL)",
                // the tool names of a session's first model call: the baseline for "tools" notices
                "CREATE TABLE IF NOT EXISTS context_tools (session_id TEXT PRIMARY KEY, tools TEXT NOT NULL)");
            _db = db;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Session prompts table unavailable; frozen prompts are kept in memory only");
        }
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
    public string Freeze(string sessionId, string prompt)
    {
        var stored = _cache.GetOrAdd(sessionId, prompt);
        if (!ReferenceEquals(stored, prompt) || _db is null) return stored;
        try
        {
            _db.Execute("INSERT OR IGNORE INTO context_prompts (session_id, prompt, created_at) VALUES (@sessionId, @prompt, @now)",
                new { sessionId, prompt, now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Storing the prompt of {Session} failed", sessionId); }
        return stored;
    }

    /// <summary>The tool names of the session's first model call, or null before it.</summary>
    public IReadOnlyList<string>? GetTools(string sessionId)
    {
        if (_tools.TryGetValue(sessionId, out var names)) return names;
        if (_db is null) return null;
        try
        {
            var json = _db.Scalar<string>("SELECT tools FROM context_tools WHERE session_id = @sessionId", new { sessionId });
            if (json is null) return null;
            return _tools.GetOrAdd(sessionId, (JsonNode.Parse(json) as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? []);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Reading the tool baseline of {Session} failed", sessionId);
            return null;
        }
    }

    /// <summary>Store the tool names of the session's first model call; returns the stored list (the first one wins).</summary>
    public IReadOnlyList<string> FreezeTools(string sessionId, IReadOnlyList<string> names)
    {
        var stored = _tools.GetOrAdd(sessionId, names);
        if (!ReferenceEquals(stored, names) || _db is null) return stored;
        try
        {
            _db.Execute("INSERT OR IGNORE INTO context_tools (session_id, tools) VALUES (@sessionId, @tools)",
                new { sessionId, tools = new JsonArray(names.Select(n => (JsonNode?)n).ToArray()).ToJsonString() });
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Storing the tool baseline of {Session} failed", sessionId); }
        return stored;
    }

    public void Delete(string sessionId)
    {
        _cache.TryRemove(sessionId, out _);
        _tools.TryRemove(sessionId, out _);
        if (_db is null) return;
        try
        {
            _db.Execute("DELETE FROM context_prompts WHERE session_id = @sessionId", new { sessionId });
            _db.Execute("DELETE FROM context_tools WHERE session_id = @sessionId", new { sessionId });
        }
        catch (Exception ex) { ctx.Logger.LogDebug(ex, "Deleting the prompt of {Session} failed", sessionId); }
    }
}
