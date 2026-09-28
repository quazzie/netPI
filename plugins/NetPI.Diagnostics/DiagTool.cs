using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Diagnostics;

/// <summary>
/// The agent's own view of the harness: the <c>diag.*</c> methods as one read-only tool, so answering "why did my tools
/// change" or "what is failing" is a tool call instead of a <c>node scripts/netpi.mjs</c> round trip that needs the
/// project path and Node (docs/DEBUGGING.md).
/// <para>Read-only by construction: the action picks the method out of a fixed list of the inspecting ones, so
/// <c>reload</c> and everything else that changes the app is not reachable from here — the tool has no way to name it.</para>
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
        ("call", "diag.call", "one model call in detail: id", false),
        ("tools", "diag.tools", "tool calls, newest first", true),
        ("tool", "diag.tool", "one tool call with its arguments and the text the model got: callId", false),
        ("journal", "diag.journal", "the events that matter as a timeline, oldest first (no per-token events)", true),
        ("run", "diag.run", "one run in depth: sessionId or agentId", true),
        ("toolsets", "diag.toolsets", "a session's tools now and every change with the cause (a plugin reload, a profile, the user, a setting)", true),
        ("logs", "diag.logs", "log entries, oldest first", false),
        ("settings", "diag.settings", "the settings document without secrets", false),
        ("failures", "diag.failures", "failed requests the providers saved, newest first", false),
        ("failure", "diag.failure", "one saved failed request with its body: name", false),
        ("snapshot", "diag.snapshot", "plugins, tools, RPC methods, recent events, logs and runtime in one go", false),
        ("event", "diag.event", "the full payload of one recent bus event: seq", false),
    ];

    /// <summary>Actions that filter by session: the calling session is the default, "all" means no filter.</summary>
    private static bool PerSession(string action) => Actions.FirstOrDefault(a => a.Action == action).PerSession;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "diag",
        Label = "Diagnostics",
        Category = "general",
        SummaryArg = "action",
        ReadOnly = true,
        Description = "Inspect the running app: overview, problems, model and tool calls, events, runs, tool-set changes, logs, settings, failed requests.",
        Help = """
            Read-only look inside NetPI itself (the same JSON as the diag.* RPCs, docs/DEBUGGING.md). Nothing here changes
            the app: there is no reload, no setting write, no control of any kind.
            Start with action overview, then problems (what looks wrong), journal (the timeline), logs (filtered), and
            toolsets (why a chat's tools changed). "What changed in my environment, when and why" is toolsets: it returns
            the session's tools now, the baseline they started from and every change with its cause
            (plugin-reload with the plugin ids, profile, user, settings).
            The arguments are the RPC's own: limit (how many, newest first), sessionId, runId/agentId, id (a model call),
            callId (a tool call), name (a tool or a saved failed request), type (an event type prefix), sinceSeq, level
            (debug|info|warn|error), category, contains (a substring of the message), sinceMinutes, maxChars, events, seq.
            The actions that work on one session (calls, tools, journal, run, toolsets) use the calling session unless
            you give another sessionId; sessionId "all" means no filter.
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
                ["sessionId"] = Str("a session; the calling one for calls, tools, journal, run and toolsets, \"all\" for none"),
                ["runId"] = Str("calls: only one run (its agent id)"),
                ["agentId"] = Str("run: one agent (instead of sessionId)"),
                ["id"] = Int("call: the model call (from calls)"),
                ["callId"] = Str("tool: the tool call (from tools)"),
                ["name"] = Str("tools: only that tool; failure: the saved request's file name"),
                ["type"] = Str("journal: an event type or prefix (\"agent\", \"tool.\")"),
                ["sinceSeq"] = Int("journal: only events after this seq"),
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
        if (known.Method is null)
            return ToolResult.Error(
                $"Unknown action \"{action}\". This tool only reads: use one of {string.Join(", ", Actions.Select(x => x.Action))}. " +
                "Changing the app (reloading plugins, writing settings) is up to the user: /reload, or the Diagnostics tab.");

        var parameters = Parameters(a, action, context.SessionId);
        try
        {
            var result = await ctx.Rpc.InvokeAsync(known.Method, parameters, ct).ConfigureAwait(false);
            var node = NetPiJson.ToNode(result);
            return ToolResult.Ok(node?.ToJsonString() ?? "null", node);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RpcException ex) { return ToolResult.Error($"{known.Method} failed: {ex.Message}"); }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "diag tool: {Action} failed", action);
            return ToolResult.Error($"{known.Method} failed: {ex.Message}");
        }
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
        if (p["sessionId"] is not JsonValue v) p["sessionId"] = sessionId;
        else if (v.TryGetValue<string>(out var named) && named.Trim() is "all" or "*") p.Remove("sessionId");
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
