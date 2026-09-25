using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Guardrails;

/// <summary>
/// Guardrails: fast checks of every tool call before it runs, with no model call and no change to the prompt (see
/// <see cref="RuleSet"/>). A rule that matches blocks the call: the model reads why, and nothing ran. A rule written
/// <c>ask:</c> asks the user first, on the tool's row in the chat: <c>guard.asked</c> (unscoped, so every window hears of
/// it), answered with <c>guard.answer { callId, allow }</c>, <c>guard.closed</c> when it stops waiting. Meanwhile the run
/// waits with its instance given back (<see cref="IAgentRuntime.WaitYieldedAsync"/>). In a subagent an ask rule blocks:
/// nobody watches its chat. The checks catch the plain cases (a command named in a variable or built at run time gets
/// through); they are not a sandbox.
/// </summary>
[NetPiPlugin("netpi.guardrails", Name = "Guardrails", Description = "Checks tool calls before they run: blocks dangerous commands and changes to protected paths, or asks you first", Order = 67)]
public sealed class GuardrailsPlugin : INetPiPlugin
{
    private Approvals? _approvals;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "guardrails", Title = "Guardrails", Group = "Tools", Order = 5,
            Settings =
            [
                SettingInfo.Bool("guardrails.enabled", "Check tool calls", true, "Before a tool runs, against the rules below. Fast: patterns and paths only."),
                SettingInfo.List("guardrails.commands", "Blocked commands", RuleSet.DefaultCommands,
                    "Regular expressions, tried on each part of a bash, pwsh or ssh_run command (split at new lines, ; && || | &), ignoring case. Start a line with ask: to ask you first instead of blocking."),
                SettingInfo.List("guardrails.paths", "Protected paths", RuleSet.DefaultPaths,
                    "Files and folders the agent may not change: write and edit refuse them, and so do bash and pwsh commands that name them (the read tool still reads them). ~ is your home. Start a line with ask: to ask you first."),
            ],
        });
        var approvals = _approvals = new Approvals(context);
        context.Services.Register<IAgentHook>(new GuardHook(context, approvals));
        context.Rpc.Register("guard.pending", (req, _) => Task.FromResult<object?>(approvals.List(req.Str("sessionId"))),
            "Tool calls waiting for the user's OK (guardrails ask rules): { sessionId? } → { sessionId, callId, agentId, tool, kind, subject, rule, askedAt }[]");
        context.Rpc.Register("guard.answer", (req, _) => Task.FromResult<object?>(approvals.Answer(req.Required("callId"), req.Bool("allow") ?? false)),
            "Allow or refuse a tool call that waits for the user's OK: { callId, allow } → true");
        return Task.CompletedTask;
    }

    /// <summary>What still waits is refused, so its run goes on instead of hanging.</summary>
    public Task StopAsync(CancellationToken ct)
    {
        _approvals?.RefuseAll();
        return Task.CompletedTask;
    }
}

/// <summary>Tool calls that wait for the user's OK, by call id.</summary>
internal sealed class Approvals(IPluginContext ctx)
{
    private readonly ConcurrentDictionary<string, Entry> _byCall = new(StringComparer.Ordinal);

    internal sealed class Entry
    {
        public required string SessionId { get; init; }
        public required string CallId { get; init; }
        public required string AgentId { get; init; }
        public required string Tool { get; init; }
        public required Verdict Verdict { get; init; }
        public DateTimeOffset AskedAt { get; } = DateTimeOffset.UtcNow;
        public TaskCompletionSource<bool> Allowed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Entry Add(string sessionId, string agentId, ToolCallPart call, Verdict verdict)
    {
        var e = new Entry { SessionId = sessionId, CallId = call.Id, AgentId = agentId, Tool = call.Name, Verdict = verdict };
        _byCall[e.CallId] = e;
        ctx.Events.Publish("guard.asked", Json(e));
        return e;
    }

    /// <summary>Stops waiting: <paramref name="status"/> is allowed | denied | steered | cancelled.</summary>
    public void Close(Entry e, string status)
    {
        if (!_byCall.TryRemove(new KeyValuePair<string, Entry>(e.CallId, e))) return;
        ctx.Events.Publish("guard.closed", new JsonObject { ["sessionId"] = e.SessionId, ["callId"] = e.CallId, ["status"] = status });
    }

    public JsonArray List(string? sessionId) =>
        new([.. _byCall.Values.Where(e => sessionId is null || e.SessionId == sessionId).OrderBy(e => e.AskedAt).Select(e => (JsonNode)Json(e))]);

    public bool Answer(string callId, bool allow)
    {
        if (!_byCall.TryGetValue(callId, out var e) || !e.Allowed.TrySetResult(allow))
            throw new RpcException("not_found", "No tool call waits for your OK with that id (it was answered, or its run ended).");
        Close(e, allow ? "allowed" : "denied");
        return true;
    }

    public void RefuseAll()
    {
        foreach (var e in _byCall.Values) e.Allowed.TrySetResult(false);
    }

    private static JsonObject Json(Entry e) => new()
    {
        ["sessionId"] = e.SessionId,
        ["callId"] = e.CallId,
        ["agentId"] = e.AgentId,
        ["tool"] = e.Tool,
        ["kind"] = e.Verdict.Kind,
        ["subject"] = e.Verdict.Subject,
        ["rule"] = e.Verdict.Rule,
        ["askedAt"] = e.AskedAt.ToString("O"),
    };
}

/// <summary>
/// Last of the before-tool hooks, so it checks the arguments that will run. Fails closed: a call it can't check is
/// blocked, with why.
/// </summary>
internal sealed class GuardHook(IPluginContext ctx, Approvals approvals) : IAgentHook
{
    public int Order => 10_000;

    private sealed record Cached(string Key, RuleSet Rules);
    private volatile Cached? _cache; // swapped whole: hooks of several agents read it at once

    /// <summary>The rules of the current settings, parsed again only when they change.</summary>
    internal RuleSet Rules()
    {
        var commands = List("guardrails.commands", RuleSet.DefaultCommands);
        var paths = List("guardrails.paths", RuleSet.DefaultPaths);
        var key = string.Join('\n', commands) + "\n\0\n" + string.Join('\n', paths);
        if (_cache is { } c && c.Key == key) return c.Rules;
        var rules = RuleSet.Parse(commands, paths, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (var p in rules.Problems) ctx.Logger.LogWarning("Guardrails: {Problem}", p);
        _cache = new Cached(key, rules);
        return rules;
    }

    public async ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call)
    {
        var ct = turn.Run.CancellationToken;
        try
        {
            if (!Enabled()) return null;
            var rules = Rules();
            if (rules.Count == 0) return null;
            JsonElement args;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
                args = doc.RootElement.Clone();
            }
            catch (JsonException) { return null; } // the tool refuses invalid JSON itself: nothing runs
            var run = turn.Run;
            var verdict = rules.Check(call.Name, args, path => Resolve(run, call, path));
            if (verdict is null) return null;
            if (verdict.Action == GuardAction.Block) return Block(Why(verdict) + " Nothing ran. If it is really needed, tell the user what and why: they can do it themselves or change the rule.");
            if (run.Agent.IsSubagent)
                return Block(Why(verdict) + " It asks the user first, and a subagent can't ask: nobody watches its chat. Nothing ran; say in your report what you needed.");
            return await AskAsync(run, call, verdict, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Guardrails could not check {Tool}", call.Name);
            return Block($"the guardrails could not check this call ({ex.Message}), so it did not run.");
        }
    }

    private async ValueTask<ToolCallDecision?> AskAsync(AgentRunContext run, ToolCallPart call, Verdict verdict, CancellationToken ct)
    {
        var entry = approvals.Add(run.Session.Id, run.Agent.Id, call, verdict);
        var status = "cancelled";
        try
        {
            var runtime = run.Services.Get<IAgentRuntime>();
            var answered = runtime is null
                ? await WaitAsync(entry.Allowed.Task, ct).ConfigureAwait(false)
                : await runtime.WaitYieldedAsync(run.Agent.Id, entry.Allowed.Task, "waiting for your OK", null, ct).ConfigureAwait(false);
            if (!answered)
            {
                status = "steered";
                return Block("the user wrote a new message instead of answering whether it may run; the message follows. Nothing ran.");
            }
            var allowed = await entry.Allowed.Task.ConfigureAwait(false);
            status = allowed ? "allowed" : "denied";
            return allowed ? null : Block($"the user said no to {Subject(verdict)}. Nothing ran.");
        }
        finally
        {
            approvals.Close(entry, status); // a no-op after guard.answer closed it
        }
    }

    private static async Task<bool> WaitAsync(Task until, CancellationToken ct)
    {
        await until.WaitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static ToolCallDecision Block(string reason) => new() { Block = true, Reason = reason };

    private static string Subject(Verdict v) => v.Kind == "command" ? $"`{v.Subject}`" : $"changing {v.Subject}";

    private static string Why(Verdict v) => v.Kind == "command"
        ? $"`{v.Subject}` matches the guardrail `{v.Rule}` (guardrails.commands)."
        : $"{v.Subject} is protected by the guardrail `{v.Rule}` (guardrails.paths).";

    private static string Resolve(AgentRunContext run, ToolCallPart call, string path) => new ToolContext
    {
        SessionId = run.Session.Id,
        AgentId = run.Agent.Id,
        CallId = call.Id,
        Cwd = run.Cwd,
        Services = run.Services,
        Events = run.Events,
    }.ResolvePath(path);

    private bool Enabled()
    {
        try { return ctx.Settings.Get("guardrails.enabled", true); }
        catch { return true; }
    }

    private List<string> List(string path, IReadOnlyList<string> fallback)
    {
        try
        {
            if (ctx.Settings.GetNode(path) is JsonArray a)
                return [.. a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim())];
        }
        catch { }
        return [.. fallback];
    }
}
