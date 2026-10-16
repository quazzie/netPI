using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Diagnostics;

/// <summary>
/// <c>diag.search</c>: find text in every session ever stored — what was said, which tools were called with what, what they
/// answered, which failed — without opening the database. Read-only, over the session store the plugins already use
/// (<see cref="IPluginContext.Sessions"/>): sessions newest first, each read back from its newest message in pages, so a
/// recent hit is found at once and an old one costs a longer scan.
/// <para>A scan has a time budget instead of a silent cap: when it is spent, or the hit limit is reached, the answer says
/// what was covered and carries a <c>nextCursor</c> that continues exactly there. <c>done: true</c> means everything was
/// looked at.</para>
/// </summary>
public sealed class HistorySearch(IPluginContext ctx)
{
    /// <summary>Messages read per store call, and sessions per list call.</summary>
    public const int MessagePage = 100, SessionPage = 200;

    public const int DefaultLimit = 50, MaxLimit = 500;
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(20), MaxBudget = TimeSpan.FromSeconds(120);

    /// <summary>The characters of context kept on each side of a match.</summary>
    private const int Around = 120;

    private sealed record Spec(string? Query, Regex? Rx, bool Text, bool Calls, bool Results, string? Role, string? Tool, bool? IsError,
        string? CallId, DateTimeOffset? From, DateTimeOffset? To);

    public JsonObject Search(RpcRequest r, CancellationToken ct)
    {
        var spec = Parse(r);
        var limit = Math.Clamp(r.Int("limit") ?? DefaultLimit, 1, MaxLimit);
        var budget = r.Int("budgetSeconds") is { } b ? TimeSpan.FromSeconds(Math.Clamp(b, 1, MaxBudget.TotalSeconds)) : DefaultBudget;
        var (offset, before) = ParseCursor(r.Str("cursor"));
        var sessionId = r.Str("sessionId");
        var projectId = r.Str("projectId");
        var query = new SessionQuery
        {
            ProjectId = projectId, IncludeArchived = r.Bool("includeArchived") ?? true, IncludeSubagents = r.Bool("includeSubagents") ?? true,
            Limit = SessionPage,
        };

        var clock = Stopwatch.StartNew();
        var hits = new JsonArray();
        var sessionsRead = 0;
        var messagesRead = 0;
        string? next = null;

        string? Cursor(int sessionOffset, long? beforeSeq) => $"{sessionOffset}:{beforeSeq}";
        bool OutOfTime() => clock.Elapsed >= budget;

        // a named session is the one to read; otherwise every session, newest first, a page of the list at a time
        IEnumerable<(SessionInfo Session, int Index)> Sessions()
        {
            if (sessionId is not null)
            {
                if (ctx.Sessions.GetSession(sessionId) is { } one) yield return (one, 0);
                yield break;
            }
            var index = offset;
            while (true)
            {
                query.Offset = index;
                var page = ctx.Sessions.ListSessions(query);
                foreach (var s in page) yield return (s, index++);
                if (page.Count < SessionPage) yield break;
            }
        }

        var first = true;
        foreach (var (session, index) in Sessions())
        {
            ct.ThrowIfCancellationRequested();
            // a session last touched before the window holds nothing in it; the list is newest-updated first except pinned ones
            if (spec.From is { } from && session.UpdatedAt < from) { if (!session.Pinned) { break; } else continue; }
            if (!first && OutOfTime()) { next = Cursor(index, null); break; }
            var startBefore = first ? before : null;
            first = false;
            sessionsRead++;
            long? cursorSeq = startBefore;
            var stopped = false;
            while (true)
            {
                var page = ctx.Sessions.GetMessages(session.Id, cursorSeq, MessagePage);
                if (page.Count == 0) break;
                messagesRead += page.Count;
                var calls = ToolCalls.Index(page);
                var passed = false;
                for (var i = page.Count - 1; i >= 0; i--)
                {
                    var m = page[i];
                    if (spec.From is { } f && m.CreatedAt < f) { passed = true; break; }
                    if (spec.To is { } t && m.CreatedAt > t) continue;
                    foreach (var hit in Match(spec, session, m, calls)) hits.Add(hit);
                    // the limit is checked between messages, so every hit of the last one is in (the answer may hold a few more)
                    if (hits.Count >= limit) { next = Cursor(index, m.Seq); stopped = true; break; }
                }
                if (stopped || passed || page.Count < MessagePage) break;
                cursorSeq = page[0].Seq;
                if (OutOfTime()) { next = Cursor(index, cursorSeq); stopped = true; break; }
            }
            if (stopped) break;
        }

        var done = next is null;
        var result = new JsonObject
        {
            ["hits"] = hits,
            ["done"] = done,
            ["scanned"] = new JsonObject { ["sessions"] = sessionsRead, ["messages"] = messagesRead, ["elapsedMs"] = clock.ElapsedMilliseconds },
        };
        if (next is not null)
        {
            result["nextCursor"] = next;
            result["note"] = hits.Count >= limit
                ? "The hit limit was reached: pass nextCursor as cursor for the next older hits."
                : $"The {budget.TotalSeconds:0}s budget ended the scan: pass nextCursor as cursor to continue, or narrow it with sessionId, projectId, from/to, name or scope.";
        }
        return result;
    }

    private static Spec Parse(RpcRequest r)
    {
        var query = r.Str("query");
        if (string.IsNullOrEmpty(query)) query = null;
        var tool = r.Str("name");
        var errors = r.Bool("errors") == true ? true : (bool?)null;
        var callId = r.Str("callId");
        var role = r.Str("role");
        if (query is null && tool is null && errors is null && callId is null && role is null)
            throw new RpcException("bad_request", "diag.search needs something to look for: query, or name (a tool), errors, callId or role.");
        var scope = (r.Str("scope") ?? "all").ToLowerInvariant();
        var text = scope is "all" or "text";
        var calls = scope is "all" or "tool-call" or "tool";
        var results = scope is "all" or "tool-result" or "tool";
        if (scope is not ("all" or "text" or "tool-call" or "tool-result" or "tool"))
            throw new RpcException("bad_request", "scope is one of: all, text, tool-call, tool-result, tool.");
        // a tool, an error flag or a call id says what is wanted: tool parts, not what people wrote
        if (tool is not null || errors is not null || callId is not null) text = false;
        if (errors is not null) calls = false;
        Regex? rx = null;
        if (query is not null && r.Bool("regex") == true)
        {
            try { rx = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex) { throw new RpcException("bad_request", "query is not a valid regular expression: " + ex.Message); }
        }
        return new Spec(query, rx, text, calls, results, role, tool, errors, callId, Time(r, "from"), Time(r, "to"));
    }

    private static DateTimeOffset? Time(RpcRequest r, string name)
    {
        if (r.Str(name) is not { } s) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
            ? t : throw new RpcException("bad_request", $"'{name}' is not a date or time: {s}");
    }

    private static (int Offset, long? Before) ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (0, null);
        var parts = cursor.Split(':', 2);
        if (!int.TryParse(parts[0], out var offset) || offset < 0 || (parts.Length > 1 && parts[1].Length > 0 && !long.TryParse(parts[1], out _)))
            throw new RpcException("bad_request", "cursor is the nextCursor of an earlier diag.search.");
        return (offset, parts.Length > 1 && parts[1].Length > 0 ? long.Parse(parts[1]) : null);
    }

    private IEnumerable<JsonObject> Match(Spec spec, SessionInfo session, ChatMessage m, IReadOnlyDictionary<string, ToolCallPart> calls)
    {
        if (spec.Role is not null && !string.Equals(m.Role.ToString(), spec.Role, StringComparison.OrdinalIgnoreCase)) yield break;
        if (spec.Text && m.Role != MessageRole.Tool)
        {
            var text = m.Text;
            if (text.Length > 0 && Snippet(spec, text) is { } snippet) yield return Hit(session, m, "text", snippet);
        }
        if (spec.Calls)
            foreach (var c in m.ToolCalls)
            {
                var (name, args) = ToolCalls.Effective(c);
                if (spec.CallId is not null && c.Id != spec.CallId) continue;
                if (spec.Tool is not null && !Named(spec.Tool, name, c.Name)) continue;
                if (Snippet(spec, name + " " + args) is { } snippet)
                {
                    var h = Hit(session, m, "tool-call", snippet);
                    h["toolName"] = name;
                    h["callId"] = c.Id;
                    yield return h;
                }
            }
        if (spec.Results)
            foreach (var t in m.ToolResults)
            {
                if (spec.CallId is not null && t.CallId != spec.CallId) continue;
                var name = ToolCalls.EffectiveName(t, calls);
                if (spec.Tool is not null && !Named(spec.Tool, name, t.Name)) continue;
                if (spec.IsError is { } err && t.IsError != err) continue;
                if (Snippet(spec, t.Content) is { } snippet)
                {
                    var h = Hit(session, m, "tool-result", snippet);
                    h["toolName"] = name;
                    h["callId"] = t.CallId;
                    h["isError"] = t.IsError;
                    yield return h;
                }
            }
    }

    private static bool Named(string wanted, string effective, string raw) =>
        string.Equals(wanted, effective, StringComparison.OrdinalIgnoreCase) || string.Equals(wanted, raw, StringComparison.OrdinalIgnoreCase);

    private static JsonObject Hit(SessionInfo session, ChatMessage m, string kind, string snippet) => new()
    {
        ["sessionId"] = session.Id, ["title"] = session.Title, ["seq"] = m.Seq, ["role"] = m.Role.ToString().ToLowerInvariant(),
        ["kind"] = kind, ["createdAt"] = m.CreatedAt.ToString("O", CultureInfo.InvariantCulture), ["snippet"] = snippet,
    };

    /// <summary>The match with some context, or null when the text does not match. With no query every text matches and the
    /// snippet is its start.</summary>
    private static string? Snippet(Spec spec, string text)
    {
        int at, length;
        if (spec.Query is null) { at = 0; length = 0; }
        else if (spec.Rx is { } rx)
        {
            Match m;
            try { m = rx.Match(text); }
            catch (RegexMatchTimeoutException) { return null; }
            if (!m.Success) return null;
            (at, length) = (m.Index, m.Length);
        }
        else
        {
            at = text.IndexOf(spec.Query, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            length = spec.Query.Length;
        }
        var start = Math.Max(0, at - Around);
        var end = Math.Min(text.Length, at + Math.Min(length, 400) + Around + (spec.Query is null ? Around : 0));
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }
}
