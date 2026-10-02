using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Guardrails;

/// <summary>
/// Guardrails: fast checks of every tool call before it runs, with no model call and no change to the prompt (see
/// <see cref="RuleSet"/>). A rule that matches blocks the call: the model reads why, and nothing ran. A rule written
/// <c>ask:</c> asks the user first, on the tool's row in the chat: <c>guard.asked</c> (unscoped, so every window hears of
/// it), answered with <c>guard.answer { approvalId, allow, scope? }</c>, <c>guard.closed</c> when it stops waiting. With
/// <c>scope: "session"</c> the rule is allowed for the rest of that chat (session meta <c>guardrailsAllowed</c>): it runs
/// without asking there, announced by <c>guard.cleared { by: "session" }</c>. Meanwhile the run
/// waits with its instance given back (<see cref="IAgentRuntime.WaitYieldedAsync"/>). In a subagent an ask rule blocks:
/// nobody watches its chat. With <c>guardrails.secondOpinion</c> on, a decision model reads a shell command before an ask
/// rule asks, and a confidently read-only one runs without asking (<see cref="SecondOpinion"/>, <c>guard.cleared</c>).
/// The checks catch the plain cases (a command named in a variable or built at run time gets through); they are not a
/// sandbox.
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
                    "Regular expressions, tried on each part of a bash, pwsh or ssh run command (split at new lines, ; && || | &), ignoring case. Start a line with ask: to ask you first instead of blocking."),
                SettingInfo.List("guardrails.paths", "Protected paths", RuleSet.DefaultPaths,
                    "Files and folders the agent may not change: write and edit refuse them, and so do bash and pwsh commands that name them (the read tool still reads them). ~ is your home. Start a line with ask: to ask you first."),
                SettingInfo.Bool("guardrails.secondOpinion", "Second opinion before asking", false,
                    "Before an ask: rule asks you about a shell command, a decision model reads it (the Decide plugin, through AiGateway); a command it finds confidently harmless runs without asking. Blocking rules and write/edit are never relaxed; without an answer you are asked."),
                SettingInfo.Str("guardrails.secondOpinionModel", "Second-opinion model", SecondOpinion.DefaultModel,
                    "qwen3.8-27b (the NInfer chat model, about 0.3 s) was measured on 852 real commands; kev-9b calls too many local commands remote."),
                SettingInfo.Number("guardrails.secondOpinionThreshold", "Second-opinion threshold", SecondOpinion.DefaultThreshold,
                    "A command runs without asking only when p(destructive), p(stops a process) and p(changes a remote) are each below it. p(read-only) is asked and shown, but not required: the model underrates builds and test runs as read-only (dotnet test 0.05-0.4), so a read-only bar cleared none of 50 hand-labelled hard commands, while the risk questions alone at 0.2 cleared 20 of their 39 read-only ones and none of the risky ones.", 0.01, 0.5),
            ],
        });
        var approvals = _approvals = new Approvals(context);
        context.Services.Register<IAgentHook>(new GuardHook(context, approvals, new SecondOpinion(context)));
        context.Rpc.Register("guard.pending", (req, _) => Task.FromResult<object?>(approvals.List(req.Str("sessionId"))),
            "Tool calls waiting for the user's OK (guardrails ask rules): { sessionId? } → { approvalId, sessionId, callId, agentId, tool, kind, subject, rule, askedAt, opinion? }[]");
        context.Rpc.Register("guard.answer", (req, _) => Task.FromResult<object?>(approvals.Answer(req.Required("approvalId"), req.Bool("allow") ?? false,
                forSession: string.Equals(req.Str("scope"), "session", StringComparison.OrdinalIgnoreCase))),
            "Allow or refuse a tool call that waits for the user's OK: { approvalId, allow, scope?: \"once\" | \"session\" } → true; scope session (with allow) " +
            "allows the rule that asked for the rest of that chat, and the other calls of that chat waiting on the same rule");
        return Task.CompletedTask;
    }

    /// <summary>What still waits is refused, so its run goes on instead of hanging.</summary>
    public Task StopAsync(CancellationToken ct)
    {
        _approvals?.RefuseAll();
        return Task.CompletedTask;
    }
}

/// <summary>Tool calls that wait for the user's OK, by unique approval id.</summary>
internal sealed class Approvals(IPluginContext ctx)
{
    private readonly ConcurrentDictionary<string, Entry> _byApproval = new(StringComparer.Ordinal);

    internal sealed class Entry
    {
        public string ApprovalId { get; } = Guid.NewGuid().ToString("N");
        public required string SessionId { get; init; }
        public required string CallId { get; init; }
        public required string AgentId { get; init; }
        public required string Tool { get; init; }
        public required Verdict Verdict { get; init; }
        public Opinion? Opinion { get; init; }
        public DateTimeOffset AskedAt { get; } = DateTimeOffset.UtcNow;
        public TaskCompletionSource<bool> Allowed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Entry Add(string sessionId, string agentId, ToolCallPart call, Verdict verdict, Opinion? opinion)
    {
        var e = new Entry { SessionId = sessionId, CallId = call.Id, AgentId = agentId, Tool = call.Name, Verdict = verdict, Opinion = opinion };
        _byApproval[e.ApprovalId] = e;
        ctx.Events.Publish("guard.asked", Json(e));
        return e;
    }

    /// <summary>Stops waiting: <paramref name="status"/> is allowed | denied | steered | cancelled.</summary>
    public bool Close(Entry e, string status, bool forSession = false)
    {
        if (!_byApproval.TryRemove(new KeyValuePair<string, Entry>(e.ApprovalId, e))) return false;
        var data = new JsonObject { ["approvalId"] = e.ApprovalId, ["sessionId"] = e.SessionId, ["callId"] = e.CallId, ["status"] = status };
        if (forSession) data["scope"] = "session";
        ctx.Events.Publish("guard.closed", data);
        return true;
    }

    /// <summary>The session meta key holding the ask rules the user allowed for the rest of that chat.</summary>
    public const string SessionKey = "guardrailsAllowed";

    /// <summary>The user allowed this ask rule for the rest of the chat (only ask rules; a block is never stored here).</summary>
    public bool AllowedInSession(string sessionId, string rule) =>
        ctx.Sessions.GetSession(sessionId)?.Meta?[SessionKey] is JsonArray a && a.Any(x => x is JsonValue v && v.TryGetValue<string>(out var r) && r == rule);

    private void AllowInSession(string sessionId, string rule)
    {
        if (AllowedInSession(sessionId, rule)) return;
        ctx.Sessions.UpdateSession(sessionId, s =>
        {
            s.Meta ??= new JsonObject();
            if (s.Meta[SessionKey] is not JsonArray a) s.Meta[SessionKey] = a = new JsonArray();
            a.Add(rule);
        });
    }

    public JsonArray List(string? sessionId) =>
        new([.. _byApproval.Values.Where(e => sessionId is null || e.SessionId == sessionId).OrderBy(e => e.AskedAt).Select(e => (JsonNode)Json(e))]);

    public bool Answer(string approvalId, bool allow, bool forSession = false)
    {
        // Closed first, then released: the waiting run's own Close is then a no-op, and a second answer finds nothing.
        forSession &= allow;
        if (!_byApproval.TryGetValue(approvalId, out var e) || e.Allowed.Task.IsCompleted)
            throw new RpcException("not_found", "No tool call waits for your OK with that id (it was answered, or its run ended).");
        if (!Close(e, allow ? "allowed" : "denied", forSession))
            throw new RpcException("not_found", "No tool call waits for your OK with that id (it was answered, or its run ended).");
        if (forSession) AllowInSession(e.SessionId, e.Verdict.Rule);
        e.Allowed.TrySetResult(allow);
        if (forSession)
            foreach (var other in _byApproval.Values.Where(o => o.SessionId == e.SessionId && o.Verdict.Rule == e.Verdict.Rule).ToList())
                if (Close(other, "allowed", forSession: true)) other.Allowed.TrySetResult(true);
        return true;
    }

    public void RefuseAll()
    {
        foreach (var e in _byApproval.Values) e.Allowed.TrySetResult(false);
    }

    private static JsonObject Json(Entry e) => new()
    {
        ["approvalId"] = e.ApprovalId,
        ["sessionId"] = e.SessionId,
        ["callId"] = e.CallId,
        ["agentId"] = e.AgentId,
        ["tool"] = e.Tool,
        ["kind"] = e.Verdict.Kind,
        ["subject"] = e.Verdict.Subject,
        ["rule"] = e.Verdict.Rule,
        ["askedAt"] = e.AskedAt.ToString("O"),
        ["opinion"] = e.Opinion?.ToJson(),
    };
}

/// <summary>
/// Last of the before-tool hooks, so it checks the arguments that will run. Fails closed: a call it can't check is
/// blocked, with why.
/// </summary>
internal sealed class GuardHook(IPluginContext ctx, Approvals approvals, SecondOpinion secondOpinion) : IAgentHook
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
            // The user allowed this ask rule for the rest of the chat (guard.answer scope "session").
            if (approvals.AllowedInSession(run.Session.Id, verdict.Rule))
            {
                ctx.Events.Publish("guard.cleared", new JsonObject
                {
                    ["sessionId"] = run.Session.Id, ["callId"] = call.Id, ["agentId"] = run.Agent.Id, ["tool"] = call.Name,
                    ["kind"] = verdict.Kind, ["subject"] = verdict.Subject, ["rule"] = verdict.Rule, ["by"] = "session",
                });
                return null;
            }
            // An ask rule on a shell command: a decision model may clear a confidently read-only one (SecondOpinion). A path
            // verdict on anything but a local shell is about where the call writes (an ssh download), not the command text.
            Opinion? opinion = null;
            if ((verdict.Kind != "path" || RuleSet.LocalShellTools.Contains(call.Name)) && RuleSet.CommandOf(call.Name, args) is { Length: > 0 } command)
            {
                opinion = await secondOpinion.AskAsync(call.Name, command, run.Cwd, RuleSet.HostOf(args), ct, run).ConfigureAwait(false);
                if (opinion is { Harmless: true })
                {
                    ctx.Events.Publish("guard.cleared", new JsonObject
                    {
                        ["sessionId"] = run.Session.Id, ["callId"] = call.Id, ["agentId"] = run.Agent.Id, ["tool"] = call.Name,
                        ["kind"] = verdict.Kind, ["subject"] = verdict.Subject, ["rule"] = verdict.Rule, ["by"] = "opinion", ["opinion"] = opinion.ToJson(),
                    });
                    return null;
                }
            }
            if (run.Agent.IsSubagent)
                return Block(Why(verdict) + " It asks the user first, and a subagent can't ask: nobody watches its chat. Nothing ran; say in your report what you needed.");
            return await AskAsync(run, call, verdict, opinion, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Guardrails could not check {Tool}", call.Name);
            return Block($"the guardrails could not check this call ({ex.Message}), so it did not run.");
        }
    }

    private async ValueTask<ToolCallDecision?> AskAsync(AgentRunContext run, ToolCallPart call, Verdict verdict, Opinion? opinion, CancellationToken ct)
    {
        var entry = approvals.Add(run.Session.Id, run.Agent.Id, call, verdict, opinion);
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
