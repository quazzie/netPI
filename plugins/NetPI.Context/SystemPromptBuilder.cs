using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

// The collections this plugin keeps in its own storage (ctx.Data); PromptStore is the only code that touches them. What a
// session was sent (prompts and which tool definitions went with them) is the session's own meta, see SessionPrompt.
//   context_tools     key: session id                     index fields: (none)
//       { tools: string[], sinceSeq: int }               the tool-notice baseline
//   context_tooldefs  key: ToolDefinition.DefinitionHash  index fields: (none)
//       { name: string, description: string, parameters: object }   content-addressed, shared by sessions, never deleted

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

    public async ValueTask<BuiltPrompt> BuildAsync(PromptContext context, CancellationToken ct)
    {
        var sessionId = context.Session.Id;
        var capturedRevision = SessionPrompt.Revision(context.Session);
        if (SessionPrompt.Fallback(context.Session) is { } frozen) return new BuiltPrompt(frozen);
        var (rendered, failed) = await RenderCoreAsync(context, ct).ConfigureAwait(false);
        var current = ctx.Sessions.GetSession(sessionId);
        if (current is not null && SessionPrompt.Revision(current) != capturedRevision)
            throw new InvalidOperationException("The session identity changed while its prompt was rendered; render the current revision.");
        // A new prompt after a final one (the session's identity was invalidated, e.g. a profile switch): the tools follow
        // the new setup, so the tool baseline is taken again (a provisional predecessor is the same first render)
        if (SessionPrompt.LastSent(context.Session) is { Provisional: false }) prompts.Reset(sessionId);
        prompts.PutDefinitions(context.Tools);
        if (failed > 0)
            // A section failed: this render lacks its part. Freezing it would keep the shortened prompt for the whole
            // session, so this call is sent it provisionally and the next call renders again.
            ctx.Logger.LogWarning("{Count} prompt section(s) failed for {Session}; the prompt is not frozen and renders again next call", failed, sessionId);
        return new BuiltPrompt(rendered, Final: failed == 0);
    }

    /// <summary>What a session is sent: its frozen prompt, or (before its first model call) a fresh render that is not stored.</summary>
    public async ValueTask<(string Prompt, bool Frozen)> PreviewAsync(PromptContext context, CancellationToken ct) =>
        SessionPrompt.Fallback(context.Session) is { } frozen ? (frozen, true) : (await RenderAsync(context, ct).ConfigureAwait(false), false);

    public async ValueTask<string> RenderAsync(PromptContext context, CancellationToken ct) =>
        (await RenderCoreAsync(context, ct).ConfigureAwait(false)).Prompt;

    /// <summary>The rendered prompt and how many sections threw (their parts are missing from it).</summary>
    private async ValueTask<(string Prompt, int Failed)> RenderCoreAsync(PromptContext context, CancellationToken ct)
    {
        var failed = 0;
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
                failed++;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
        }
        return (string.Join("\n\n", parts), failed);
    }
}

/// <summary>
/// The plugin's own collections (see the file's header for their shape): the tool-notice baselines and the content-addressed
/// tool definitions. Every call is best effort: a failing store only costs what it wrote.
/// </summary>
internal sealed class PromptStore(IPluginContext ctx)
{
    private readonly ConcurrentDictionary<string, ToolBaseline> _tools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _knownDefs = new(StringComparer.Ordinal);
    private IDataCollection _baselines = null!;
    private IDataCollection _defs = null!;

    /// <summary>Open the collections (the store creates what it does not have).</summary>
    public void Initialize()
    {
        // the tool names of a session's first model call: the baseline for "tools" notices. A baseline taken later
        // (after context.reset) ignores the "tools" notices up to its sinceSeq.
        _baselines = ctx.Data.Collection("context_tools", new CollectionSpec());
        _defs = ctx.Data.Collection("context_tooldefs", new CollectionSpec());
    }

    private static long Number(JsonObject? doc, string field, long fallback = 0) =>
        doc?[field] is JsonValue v
            // the value comes back as the type it was written with: int in memory, long from a column
            ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : fallback
            : fallback;

    /// <summary>Store the definitions not stored yet (by <see cref="ToolDefinition.DefinitionHash"/>).</summary>
    public void PutDefinitions(IReadOnlyList<ToolDefinition> tools)
    {
        foreach (var t in tools)
        {
            var hash = t.DefinitionHash;
            if (_knownDefs.ContainsKey(hash)) continue;
            try
            {
                if (_defs.Get(hash) is null)
                    _defs.Insert(hash, new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() });
                _knownDefs.TryAdd(hash, 0);
            }
            catch (Exception ex) { ctx.Logger.LogWarning(ex, "Storing the tool definition {Tool} failed", t.Name); }
        }
    }

    /// <summary>One stored definition (<c>{ name, description, parameters }</c>), or null.</summary>
    public JsonObject? Definition(string def)
    {
        try { return def.Length == 0 ? null : _defs.Get(def); }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Reading the tool definition {Def} failed", def);
            return null;
        }
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

    /// <summary><c>context.reset</c>: forget the session's tool baseline; the next call takes it again.</summary>
    public void Reset(string sessionId)
    {
        _tools.TryRemove(sessionId, out _);
        try { _baselines.Delete(sessionId); }
        catch (Exception ex) { ctx.Logger.LogDebug(ex, "Resetting the tool baseline of {Session} failed", sessionId); }
    }

    /// <summary>
    /// A fork (<c>session.forked</c>) goes on with the tools of the prompt the original was sent at the fork point: its own
    /// (trimmed) newest history entry. Nothing without one, or when the fork's prompt was reset. Idempotent: a stored baseline is kept.
    /// </summary>
    public void Fork(string to)
    {
        if (ctx.Sessions.GetSession(to) is not { } fork || fork.Meta?[SessionPrompt.ForkResetKey]?.GetValue<bool>() == true) return;
        if (SessionPrompt.LastSent(fork) is not { } last) return;
        FreezeTools(to, [.. last.Tools.Select(t => t.Name)], last.AfterSeq);
    }

    /// <summary>A deleted session: its tool baseline (the definitions are shared and stay).</summary>
    public void Delete(string sessionId) => Reset(sessionId);
}

/// <summary>The tools a session's prompt was rendered with, and the last message seq at that time.</summary>
internal sealed record ToolBaseline(IReadOnlyList<string> Names, long SinceSeq);
