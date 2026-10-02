using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Diagnostics;

/// <summary>
/// The agent's own view of the harness: the <c>diag.*</c> methods as one read-only tool, so answering "why did my tools
/// change" or "what is failing" is a tool call instead of a <c>node scripts/netpi.mjs</c> round trip that needs the
/// project path and Node (docs/DEBUGGING.md).
/// <para>Read-only by construction: the action picks the method out of a fixed list of the inspecting ones, so
/// <c>reload</c> and everything else that changes the app is not reachable from here — the tool has no way to name it.
/// The one exception is the <c>rpc</c> action, which reaches <em>any</em> method the host marks read-only
/// (<see cref="IRpcRegistry.Register"/>'s <c>readOnly</c>), because wrapping thirteen methods is not the same as reaching
/// the app: without it, an agent that needs <c>events.recent</c> or <c>sessions.get</c> has to read the live RPC token out
/// of <c>server.json</c> and hand-roll a POST. Unmarked means "may write", so the default is closed (idea-de1s7t).</para>
/// </summary>
public sealed class DiagTool(IPluginContext ctx) : IAgentTool
{
    /// <summary>
    /// The actions, in the order the manual lists them: the method to call, what the action is for, and whether it
    /// filters by session (then the calling session is the default, and "all" means no filter).
    /// </summary>
    internal static readonly (string Action, string Method, string What, bool PerSession)[] Actions =
    [
        ("overview", "diag.overview", "app, process, plugins, models, agents with holders and waiters, active runs, model calls, running tools, processes, problems, the other diag methods", false),
        ("problems", "diag.problems", "what looks wrong now, worst first", false),
        ("calls", "diag.calls", "model calls, newest first (running ones too)", true),
        ("call", "diag.call", "one model call in detail: id or correlationId", false),
        ("tools", "diag.tools", "tool calls, newest first", true),
        ("tool", "diag.tool", "one tool call with its arguments and the text the model got: callId", false),
        ("journal", "diag.journal", "the events that matter as a timeline, oldest first (no per-token events)", true),
        ("run", "diag.run", "one run in depth: sessionId or agentId", true),
        ("toolsets", "diag.toolsets", "a session's tools now and every change with the cause (a plugin reload, a profile, the user, a setting)", true),
        ("messages", "sessions.messages", "a page of a session's messages in full (role, text, tool calls and their results), oldest first: beforeSeq pages back", true),
        ("logs", "diag.logs", "log entries, oldest first", false),
        ("settings", "diag.settings", "the settings document without secrets", false),
        ("failures", "diag.failures", "failed requests the providers saved, newest first", false),
        ("failure", "diag.failure", "one saved failed request with its body: name", false),
        ("snapshot", "diag.snapshot", "plugins, tools, RPC methods, recent events, logs and runtime in one go", false),
        ("event", "diag.event", "the full payload of one recent bus event: seq", false),
        ("rpc", "", "any RPC method the host marks read-only (action rpc: method, params?) — the way to reach what diag does not wrap", false),
    ];

    /// <summary>Methods the <c>rpc</c> action must never call: itself, or the tool has a way to call itself.</summary>
    internal static readonly string[] RpcDenied = ["diag.rpc", "diag.reload"];

    /// <summary>How long the <c>rpc</c> action waits before it gives up on a read-only method that hangs.</summary>
    public static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How much of a method's answer goes into the result before it is cut (the tool output limits).</summary>
    public const int RpcMaxChars = 200_000;

    /// <summary>Actions that filter by session: the calling session is the default, "all" means no filter.</summary>
    private static bool PerSession(string action) => Actions.FirstOrDefault(a => a.Action == action).PerSession;

    /// <summary>
    /// Parameters an action has to rename on the way: the tool speaks <c>sessionId</c> everywhere, but the method it
    /// forwards to may want another name (<c>sessions.messages</c> takes <c>id</c>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Renames =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["messages"] = "sessionId:id" };

    public ToolDefinition Definition { get; } = new()
    {
        Name = "diag",
        Label = "Diagnostics",
        Category = "general",
        SummaryArg = "action",
        ReadOnly = true,
        Description = "Inspect the running app: overview, problems, model and tool calls, events, runs, tool-set changes, a chat's messages, logs, settings, failed requests — and rpc for any other read-only method.",
        Help = """
            Read-only look inside NetPI itself (the same JSON as the methods behind it, docs/DEBUGGING.md). Nothing here
            changes the app: there is no reload, no setting write, no control of any kind.
            Start with action overview, then problems (what looks wrong), journal (the timeline), logs (filtered), and
            toolsets (why a chat's tools changed). "What changed in my environment, when and why" is toolsets: it returns
            the session's tools now, the baseline they started from and every change with its cause
            (plugin-reload with the plugin ids, profile, user, settings).
            messages is the full text of a chat: a page of its messages (role, text, tool calls, tool results), oldest
            first, with beforeSeq to page back. It is how you read another chat - what it was asked, and what it answered.
            The arguments are the method's own: limit (how many, newest first), sessionId, runId/agentId, id (a model
            call), callId (a tool call), name (a tool or a saved failed request), type (an event type prefix), sinceSeq,
            beforeSeq, level (debug|info|warn|error), category, contains (a substring of the message), sinceMinutes,
            maxChars, events, seq.
            The actions that work on one session (calls, tools, journal, run, toolsets, messages) use the calling session
            unless you give another sessionId; sessionId "all" means no filter. journal is the exception that reads
            global: with a type and no sessionId it looks at every session, because a type filter that silently stayed
            inside this chat is how you conclude an event never happened.
            rpc reaches anything else that only reads: rpc { action: "rpc", method: "events.recent", params: { max: 100 } }.
            Only methods the host marks read-only are reachable, so nothing that changes the app goes through it. What is
            reachable: rpc { action: "rpc", method: "rpc.list" } — the same list the outside script prints.
            """,
        PromptGuidelines =
        [
            "Diagnose the harness itself with the diag tool (read-only): action overview, then problems, journal, logs or toolsets.",
        ],
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = Enum([.. Actions.Select(a => a.Action)]),
                ["limit"] = Int("how many entries, newest first (the method's own default when omitted)"),
                ["sessionId"] = Str("a session; the calling one for calls, tools, journal, run, toolsets and messages, \"all\" for none"),
                ["runId"] = Str("calls: only one run (its agent id)"),
                ["agentId"] = Str("run: one agent (instead of sessionId)"),
                ["id"] = Int("call: the model call (from calls)"),
                ["correlationId"] = Str("call: stable identifier from a physical owner"),
                ["callId"] = Str("tool: the tool call (from tools)"),
                ["name"] = Str("tools: only that tool; failure: the saved request's file name"),
                ["type"] = Str("journal: an event type or prefix (\"agent\", \"tool.\")"),
                ["sinceSeq"] = Int("journal: only events after this seq"),
                ["beforeSeq"] = Int("messages: page back to the messages before this seq"),
                ["errors"] = Bool("calls, tools: only the failed ones"),
                ["running"] = Bool("calls, tools: only the ones running now"),
                ["detail"] = Bool("calls: the request and response with them"),
                ["level"] = Enum(["debug", "info", "warn", "error"], "logs: at least this level"),
                ["category"] = Str("logs: the logger's category"),
                ["contains"] = Str("logs: only messages containing this"),
                ["sinceMinutes"] = Int("logs: only the last N minutes"),
                ["maxChars"] = Int("failure: cut the body at N characters (default 200000)"),
                ["events"] = Int("snapshot: how many recent events (default 200)"),
                ["seq"] = Int("event: the event's seq (from journal or snapshot)"),
                ["method"] = Str("rpc: the RPC method to call, if it only reads (rpc.list is one)"),
                ["params"] = new JsonObject { ["type"] = "object", ["description"] = "rpc: the method's own parameters" },
            },
            ["required"] = new JsonArray("action"),
        },
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var a = Args(args);
        var action = (a["action"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        if (action.Length == 0) return ToolResult.Error("Missing 'action'. The actions are: " + string.Join(", ", Actions.Select(x => x.Action)) + ".");
        var known = Actions.FirstOrDefault(x => x.Action == action);
        if (action == "rpc") return await RpcAsync(a, ct).ConfigureAwait(false);
        if (known.Method is not { } method)
            return ToolResult.Error(
                $"Unknown action \"{action}\". This tool only reads: use one of {string.Join(", ", Actions.Select(x => x.Action))}. " +
                "Changing the app (reloading plugins, writing settings) is up to the user: /reload, or the Diagnostics tab.");

        var parameters = Parameters(a, action, context.SessionId);
        try
        {
            var result = await ctx.Rpc.InvokeAsync(method, parameters, ct).ConfigureAwait(false);
            return Cut(NetPiJson.ToNode(result));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RpcException ex) { return ToolResult.Error($"{method} failed: {ex.Message}"); }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "diag tool: {Action} failed", action);
            return ToolResult.Error($"{known.Method} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The passthrough: call any method the host marks read-only, and only those. The check is the registration's own
    /// <c>readOnly</c> flag (a fact, in <c>rpc.list</c>), not a name pattern here, so a method cannot become reachable
    /// because it was renamed — and an unmarked one stays out until its author says it only reads.
    /// </summary>
    private async Task<ToolResult> RpcAsync(JsonObject args, CancellationToken ct)
    {
        var method = (args["method"]?.GetValue<string>() ?? "").Trim();
        if (method.Length == 0)
            return ToolResult.Error("Missing 'method': the RPC to call, e.g. \"events.recent\". What is reachable: action rpc with method \"rpc.list\".");
        if (RpcDenied.Contains(method, StringComparer.Ordinal))
            return ToolResult.Error($"\"{method}\" is not reachable from here.");
        var info = ctx.Rpc.List().FirstOrDefault(m => string.Equals(m.Method, method, StringComparison.Ordinal));
        if (info is null)
            return ToolResult.Error($"Unknown RPC method \"{method}\". The list is \"rpc.list\" (action rpc, method \"rpc.list\").");
        if (!info.ReadOnly)
            return ToolResult.Error(
                $"\"{method}\" may change the app, so this tool will not call it. {info.Description} Changing the app is up to the user: /reload, or the Diagnostics tab.");
        object? result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RpcTimeout);
            result = await ctx.Rpc.InvokeAsync(method, args["params"] ?? new JsonObject(), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Error($"\"{method}\" did not answer within {RpcTimeout.TotalSeconds:0}s — a method that only reads should not take that long.");
        }
        catch (OperationCanceledException) { throw; }
        catch (RpcException ex) { return ToolResult.Error($"{method} failed: {ex.Message}"); }
        return Cut(NetPiJson.ToNode(result));
    }

    /// <summary>
    /// A method's answer as the tool result, cut at <see cref="RpcMaxChars"/> like the rpc passthrough did — every
    /// wrapped action goes through here now. A cut answer also goes into no <c>Details</c>: the runtime persists that
    /// node in the session database while the model only ever sees the cut text, so a multi-megabyte
    /// <c>messages</c> page would be written to disk in full to show 20 000 characters (idea-5oitnm).
    /// </summary>
    private static ToolResult Cut(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        if (text.Length <= RpcMaxChars) return ToolResult.Ok(text, node);
        return ToolResult.Ok(text[..RpcMaxChars] + $"… [cut at {RpcMaxChars} characters: ask for less, or filter it]", null);
    }

    /// <summary>The call's own arguments, minus the action, plus the session the action defaults to.</summary>
    internal static JsonObject Parameters(JsonObject args, string action, string sessionId)
    {
        var p = new JsonObject();
        foreach (var kv in args)
        {
            if (kv.Key is "action" or null || kv.Value is null) continue;
            p[kv.Key] = kv.Value.DeepClone();
        }
        if (!PerSession(action)) return p;
        // the session-scoped actions look at the calling chat unless another one is named ("all": no filter)
        // A journal query with a type and no session asks about every session: leaving it inside the calling chat is how
        // "that event never fired" gets concluded from a filter that could not have seen it (idea-de1s7t).
        if (action == "journal" && p["type"] is not null && !p.ContainsKey("sessionId")) return p;
        if (p["sessionId"] is not JsonValue v) p["sessionId"] = sessionId;
        else if (v.TryGetValue<string>(out var named) && named.Trim() is "all" or "*") p.Remove("sessionId");
        // a method that names the session differently gets it under that name
        if (Renames.TryGetValue(action, out var rename))
        {
            var parts = rename.Split(':');
            if (p.Remove(parts[0], out var value)) p[parts[1]] = value;
        }
        return p;
    }

    private static JsonObject Args(JsonElement args)
    {
        var o = new JsonObject();
        if (args.ValueKind == JsonValueKind.Object)
            foreach (var p in args.EnumerateObject()) o[p.Name] = NetPiJson.ToNode(p.Value);
        return o;
    }

    private static JsonObject Enum(string[] values, string? description = null) => new()
    {
        ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray()),
    };

    private static JsonObject Str(string? description = null) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Int(string? description = null) => new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject Bool(string? description = null) => new() { ["type"] = "boolean", ["description"] = description };
}
