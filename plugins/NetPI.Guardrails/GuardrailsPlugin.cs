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
/// without asking there, announced by <c>guard.cleared { by: "session" }</c>. The question is a wait for the user
/// (<see cref="UserWait"/>, kind <c>guard</c>): the call does not run, the run ends at it holding nothing, and the OK lets
/// it run in the run that goes on; it survives a reload of this plugin and a restart. In a subagent an ask rule blocks:
/// nobody watches its chat. With <c>guardrails.secondOpinion</c> on, a decision model reads a shell command before an ask
/// rule asks, and a confidently read-only one runs without asking (<see cref="SecondOpinion"/>, <c>guard.cleared</c>).
/// A deliberate wait longer than <c>guardrails.maxSleepSeconds</c> is a rule of its own (<see cref="Sleeps"/>), because a
/// regular expression cannot tell a five-minute sleep from a one-second one.
/// The checks catch the plain cases (a command named in a variable or built at run time gets through); they are not a
/// sandbox.
/// </summary>
[NetPiPlugin("netpi.guardrails", Name = "Guardrails", Description = "Checks tool calls before they run: blocks dangerous commands, long deliberate waits and changes to protected paths, or asks you first", Order = 67)]
public sealed class GuardrailsPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        // Run state of this plugin: a fork starts without it (the session service remembers the keys even when this plugin is not loaded at a fork).
        context.Sessions.DeclareForkReset("guardrailsAllowed");
        context.Services.Register(new SettingsSection
        {
            Id = "guardrails", Title = "Guardrails", Group = "Tools", Order = 5,
            Settings =
            [
                SettingInfo.Bool("guardrails.enabled", "Check tool calls", true, "Before a tool runs, against the rules below. Fast: patterns and paths only."),
                SettingInfo.List("guardrails.commands", "Blocked commands", RuleSet.DefaultCommands,
                    "Regular expressions, tried on each part of a bash, pwsh or ssh run command (split at new lines, ; && || | &), ignoring case. Start a line with ask: to ask you first instead of blocking. Start a line with the tools it applies to — bash, pwsh, ssh, windows, comma-separated, a colon after (bash: ^…, before any ask:/block:) to limit it to those: a pwsh started by the pwsh tool is not a bash pwsh."),
                SettingInfo.List("guardrails.paths", "Protected paths", RuleSet.DefaultPaths,
                    "Files and folders the agent may not change: write and edit refuse them, and so do bash and pwsh commands that name them (the read tool still reads them). ~ is your home. Start a line with ask: to ask you first."),
                SettingInfo.Number("guardrails.maxSleepSeconds", "Longest deliberate wait", Sleeps.DefaultMaxSeconds,
                    "A bash, pwsh or ssh run command that waits longer than this (sleep, Start-Sleep, ping -n/-c, the script a shell runs with -c) is refused or asks, so the agent waits for a command instead of sleeping through the wait. 0 switches it off.", 0, 3600, "s"),
                SettingInfo.Bool("guardrails.askReflect", "Agent reconsiders before asking", true,
                    "The first time a call meets an ask: rule the agent is told why and may take another way (pwsh instead of powershell, diag instead of reading ~/.netpi by shell). Only the same call again asks you, with the agent's reason on the question."),
                SettingInfo.Choice("guardrails.maxSleepAction", "On a long wait", "block", ["block", "ask"],
                    "block: the call does not run and the model reads why (a shell call with a matching timeout is the wait). ask: you are asked on the tool's row in the chat, and can allow it for the rest of that chat."),
                SettingInfo.Bool("guardrails.secondOpinion", "Second opinion before asking", false,
                    "Before an ask: rule asks you about a shell command, a decision model reads it (the Decide plugin, through AiGateway); a command it finds confidently harmless runs without asking. Blocking rules and write/edit are never relaxed; without an answer you are asked."),
                SettingInfo.Str("guardrails.secondOpinionModel", "Second-opinion model", SecondOpinion.DefaultModel,
                    "qwen3.8-27b (the NInfer chat model, about 0.3 s) was measured on 852 real commands; kev-9b calls too many local commands remote."),
                SettingInfo.Number("guardrails.secondOpinionThreshold", "Second-opinion threshold", SecondOpinion.DefaultThreshold,
                    "A command runs without asking only when p(destructive), p(stops a process) and p(changes a remote) are each below it. p(read-only) is asked and shown, but not required: the model underrates builds and test runs as read-only (dotnet test 0.05-0.4), so a read-only bar cleared none of 50 hand-labelled hard commands, while the risk questions alone at 0.2 cleared 20 of their 39 read-only ones and none of the risky ones.", 0.01, 0.5),
            ],
        });
        var outcomes = new GuardOutcomes(context.Data, context.Logger);
        var approvals = new Approvals(context, outcomes);
        context.Rpc.Register(new RpcMethod("guard.outcomes", "What became of the guard questions: { result? (cleared|session|allowed|allowed-session|denied|steered|cancelled|stopped|subagent|reflect), limit? (50) } → { summary: { result: count }, rows: [{ at, result, sessionId, tool, kind, rule, subject, opinion? }] } (newest first)", ReadOnly: true, Params: [RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("result")]),
            (req, _) => Task.FromResult<object?>(outcomes.Read(req.Str("result"), req.Int("limit") ?? 50)));
        context.Services.Register<IAgentHook>(new GuardHook(context, approvals, new SecondOpinion(context)));
        context.Rpc.Register(new RpcMethod("guard.pending", "Tool calls waiting for the user's OK (guardrails ask rules): { sessionId? } → { approvalId, sessionId, callId, agentId, tool, kind, subject, rule, askedAt, opinion? }[]", ReadOnly: true, Params: [RpcParam.Opt("sessionId")]),
            (req, _) => Task.FromResult<object?>(approvals.List(req.Str("sessionId"))));
        context.Rpc.Register(new RpcMethod("guard.answer", "Allow or refuse a tool call that waits for the user's OK: { approvalId, allow, scope?: \"once\" | \"session\" } → true; scope session (with allow) " +
            "allows the rule that asked for the rest of that chat, and the other calls of that chat waiting on the same rule", ReadOnly: false, Params: [RpcParam.Req("approvalId"), RpcParam.Opt("allow", RpcParamType.Boolean), RpcParam.Opt("scope")]),
            async (req, ct) => await approvals.AnswerAsync(req.Required("approvalId"), req.Bool("allow") ?? false,
                forSession: string.Equals(req.Str("scope"), "session", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false));
        // the cards, from the runtime's waits of this kind (sync handlers: an async one left alive roots this load context)
        context.Events.Subscribe(EventTypes.AgentWaiting, e =>
        {
            if (e.As<JsonObject>()?["wait"] is JsonObject w && (string?)w["kind"] == Approvals.Kind) context.Events.Publish("guard.asked", Approvals.Json(w));
        });
        context.Events.Subscribe(EventTypes.AgentWaitEnded, e =>
        {
            if (e.As<JsonObject>() is not { } d || (string?)d["kind"] != Approvals.Kind || (string?)d["reason"] == "answered") return;   // an answer closes its own
            approvals.Closed((string)d["waitId"]!, (string?)d["sessionId"] ?? "", (string?)d["callId"], (string?)d["tool"] ?? "", d["data"] as JsonObject,
                (string?)d["reason"] == "steered" ? "steered" : "cancelled");
        });
        return Task.CompletedTask;
    }

    /// <summary>Nothing to refuse: the questions are the runtime's, and they wait on through a reload of this plugin.</summary>
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Tool calls that wait for the user's OK: the runtime's waits of kind <c>guard</c> (<see cref="UserWait"/>), each with an
/// approval id of its own. This plugin keeps none of them, so a reload of it loses none.
/// </summary>
internal sealed class Approvals(IPluginContext ctx, GuardOutcomes? outcomes = null)
{
    public const string Kind = "guard";
    public GuardOutcomes? Outcomes => outcomes;

    /// <summary>The wait a call makes to ask before it runs: its card's data, and what the call reads when it does not run.</summary>
    public static UserWait Wait(ToolCallPart call, Verdict verdict, Opinion? opinion, string? reason = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = Kind,
        Activity = "waiting for your OK",
        Data = new JsonObject
        {
            ["tool"] = call.Name, ["kind"] = verdict.Kind, ["subject"] = verdict.Subject, ["rule"] = verdict.Rule, ["opinion"] = opinion?.ToJson(),
            ["reason"] = reason,
        },
        IfSteered = ToolResult.Error("Blocked: the user wrote a new message instead of answering whether it may run; the message follows. Nothing ran."),
        IfCancelled = ToolResult.Error("Blocked: the chat was stopped before the user answered whether it may run. Nothing ran."),
    };

    private IAgentRuntime Runtime => ctx.Services.Get<IAgentRuntime>() ?? throw new RpcException("unavailable", "The agent runtime is not running.");

    private IEnumerable<WaitingCall> Waiting(string? sessionId) =>
        ctx.Services.Get<IAgentRuntime>()?.Waits(sessionId, Kind).Where(w => !w.Answered) ?? [];

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

    public JsonArray List(string? sessionId) => new([.. Waiting(sessionId).Select(w => (JsonNode)Json(NetPiJson.ToNode(w)!.AsObject()))]);

    /// <summary>
    /// The user's OK, or no: allowed, the call runs in the run that goes on (the hook lets that rule through for it); refused,
    /// it is blocked. With <paramref name="forSession"/> the rule is allowed for the rest of the chat, and so are that
    /// chat's other calls waiting on it.
    /// </summary>
    public async Task<bool> AnswerAsync(string approvalId, bool allow, bool forSession, CancellationToken ct)
    {
        forSession &= allow;
        var w = Waiting(null).FirstOrDefault(x => x.Id == approvalId)
                ?? throw new RpcException("not_found", "No tool call waits for your OK with that id (it was answered, or its chat moved on).");
        var rule = (string?)w.Data?["rule"] ?? "";
        if (forSession) AllowInSession(w.SessionId, rule);
        var resolution = allow
            ? WaitResolution.Allow()
            : WaitResolution.Answer(ToolResult.Error($"Blocked: the user said no to {Subject((string?)w.Data?["kind"], (string?)w.Data?["subject"])}. Nothing ran."));
        if (!await Runtime.ResolveAsync(w.Id, resolution, ct).ConfigureAwait(false))
            throw new RpcException("not_found", "No tool call waits for your OK with that id (it was answered, or its chat moved on).");
        Closed(w.Id, w.SessionId, w.CallId, w.Tool, w.Data, allow ? "allowed" : "denied", forSession);
        if (forSession)
            foreach (var other in Waiting(w.SessionId).Where(o => (string?)o.Data?["rule"] == rule).ToList())
                if (await Runtime.ResolveAsync(other.Id, WaitResolution.Allow(), ct).ConfigureAwait(false))
                    Closed(other.Id, other.SessionId, other.CallId, other.Tool, other.Data, "allowed", forSession: true);
        return true;
    }

    /// <summary>A wait stopped: <paramref name="status"/> is allowed | denied | steered | cancelled. The card closes, the outcome is kept.</summary>
    public void Closed(string approvalId, string sessionId, string? callId, string tool, JsonObject? data, string status, bool forSession = false)
    {
        var closed = new JsonObject { ["approvalId"] = approvalId, ["sessionId"] = sessionId, ["callId"] = callId, ["status"] = status };
        if (forSession) closed["scope"] = "session";
        ctx.Events.Publish("guard.closed", closed);
        outcomes?.Record(status == "allowed" && forSession ? "allowed-session" : status, sessionId, tool,
            (string?)data?["kind"] ?? "", (string?)data?["rule"] ?? "", (string?)data?["subject"] ?? "", data?["opinion"] as JsonObject);
    }

    /// <summary>A waiting call as the card, guard.pending and guard.asked show it, from the runtime's wait.</summary>
    internal static JsonObject Json(JsonObject wait) => new()
    {
        ["approvalId"] = wait["id"]?.DeepClone(),
        ["sessionId"] = wait["sessionId"]?.DeepClone(),
        ["callId"] = wait["callId"]?.DeepClone(),
        ["agentId"] = wait["agentId"]?.DeepClone(),
        ["tool"] = wait["data"]?["tool"]?.DeepClone() ?? wait["tool"]?.DeepClone(),
        ["kind"] = wait["data"]?["kind"]?.DeepClone(),
        ["subject"] = wait["data"]?["subject"]?.DeepClone(),
        ["rule"] = wait["data"]?["rule"]?.DeepClone(),
        ["askedAt"] = wait["since"]?.DeepClone(),
        ["opinion"] = wait["data"]?["opinion"]?.DeepClone(),
        ["reason"] = wait["data"]?["reason"]?.DeepClone(),
    };

    internal static string Subject(string? kind, string? subject) => kind == "path" ? $"changing {subject}" : $"`{subject}`";
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
            var maxSleep = MaxSleep();
            if (rules.Count == 0 && maxSleep <= 0) return null;
            // The arguments as the tools read them (NetPI.ToolArgs): a string-encoded root - a double-encoded one
            // included - unwraps to the object the tool will run with, so the guard judges the call that runs.
            // Unreadable arguments read no properties: the tool refuses invalid JSON itself, so nothing runs.
            var args = ToolArgs.Parse(call.Arguments);
            var run = turn.Run;
            // The typed sleep rule: no regular expression can say "longer than N seconds". It is one verdict beside the
            // rules', never instead of them: `sleep 60; rm -rf ~` is blocked by the rule whatever the sleep says, and a sleep
            // the user allowed does not allow what is in the same command.
            var sleep = Sleeps.Check(call.Name, args, maxSleep, AskOnSleep());
            var rule = rules.Count == 0 ? null : rules.Check(call.Name, args, path => Resolve(run, call, path));
            // the user's OK for this call (the run that goes on after it): that rule lets it through, another still asks
            var allowed = turn.AllowedCalls.TryGetValue(call.Id, out var w) && w.Kind == Approvals.Kind ? (string?)w.Data?["rule"] : null;
            foreach (var verdict in Ordered(rule, sleep))
            {
                if (verdict.Action != GuardAction.Block && allowed is not null && verdict.Rule == allowed) continue;
                if (await JudgeAsync(turn, call, args, verdict, ct).ConfigureAwait(false) is { } decision) return decision;
            }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Guardrails could not check {Tool}", call.Name);
            return Block($"the guardrails could not check this call ({ex.Message}), so it did not run.");
        }
    }

    /// <summary>The verdicts of one call, the ones that block first (any of them ends the call), then the rules' ask, then the sleep's.</summary>
    private static IEnumerable<Verdict> Ordered(Verdict? rule, Verdict? sleep)
    {
        var all = new[] { rule, sleep }.OfType<Verdict>().ToList();
        return all.Where(v => v.Action == GuardAction.Block).Concat(all.Where(v => v.Action != GuardAction.Block));
    }

    /// <summary>One verdict: a block, a clearance (allowed for the chat, or a harmless second opinion), or the user's answer. Null: the call may go on.</summary>
    private async ValueTask<ToolCallDecision?> JudgeAsync(AgentTurnContext turn, ToolCallPart call, ToolArgs args, Verdict verdict, CancellationToken ct)
    {
        var run = turn.Run;
        if (verdict.Action == GuardAction.Block) return Block(Why(verdict) + " Nothing ran. If it is really needed, tell the user what and why: they can do it themselves or change the rule.");
        // The user allowed this ask rule for the rest of the chat (guard.answer scope "session").
        if (approvals.AllowedInSession(run.Session.Id, verdict.Rule))
        {
            ctx.Events.Publish("guard.cleared", new JsonObject
            {
                ["sessionId"] = run.Session.Id, ["callId"] = call.Id, ["agentId"] = run.Agent.Id, ["tool"] = call.Name,
                ["kind"] = verdict.Kind, ["subject"] = verdict.Subject, ["rule"] = verdict.Rule, ["by"] = "session",
            });
            approvals.Outcomes?.Record("session", run.Session.Id, call.Name, verdict, null);
            return null;
        }
        // An ask rule on a command: a decision model may clear a confidently read-only one (SecondOpinion). Neither a
        // path verdict nor a long sleep is one: a path ask is about where the call writes, which the model does not
        // read, and a long wait is a deliberate sleep the rule has already priced.
        Opinion? opinion = null;
        if (verdict.Kind == "command" && RuleSet.CommandOf(call.Name, args) is { Length: > 0 } command)
        {
            opinion = await secondOpinion.AskAsync(call.Name, command, run.Cwd, RuleSet.HostOf(args), ct, run).ConfigureAwait(false);
            if (opinion is { Harmless: true })
            {
                ctx.Events.Publish("guard.cleared", new JsonObject
                {
                    ["sessionId"] = run.Session.Id, ["callId"] = call.Id, ["agentId"] = run.Agent.Id, ["tool"] = call.Name,
                    ["kind"] = verdict.Kind, ["subject"] = verdict.Subject, ["rule"] = verdict.Rule, ["by"] = "opinion", ["opinion"] = opinion.ToJson(),
                });
                approvals.Outcomes?.Record("cleared", run.Session.Id, call.Name, verdict, opinion);
                return null;
            }
        }
        if (run.Agent.IsSubagent)
        {
            approvals.Outcomes?.Record("subagent", run.Session.Id, call.Name, verdict, opinion);
            return Block(Why(verdict) + " It asks the user first, and a subagent can't ask: nobody watches its chat. Nothing ran; say in your report what you needed.");
        }
        // Reflect first (guardrails.askReflect): the first time a call meets an ask rule the agent hears why and gets the
        // chance to take another way - most asks were a call that had one (powershell for pwsh, ~/.netpi for diag). Only
        // the same call again (same rule, same subject, this chat) asks the user, with the agent's reason on the card.
        string? reason = null;
        if (AskReflect())
        {
            if (!_reflected.Seen(run.Session.Id, verdict))
            {
                _reflected.Note(run.Session.Id, verdict);
                approvals.Outcomes?.Record("reflect", run.Session.Id, call.Name, verdict, opinion);
                ctx.Events.Publish("guard.reflect", new JsonObject
                {
                    ["sessionId"] = run.Session.Id, ["callId"] = call.Id, ["agentId"] = run.Agent.Id, ["tool"] = call.Name,
                    ["kind"] = verdict.Kind, ["subject"] = verdict.Subject, ["rule"] = verdict.Rule,
                });
                return Block(Reflect(verdict));
            }
            reason = ReasonOf(turn.LatestAssistant);
        }
        // ask: the call waits for the user's OK (the run ends at it; guard.answer goes on with it, allowed or blocked)
        return new ToolCallDecision { Wait = Approvals.Wait(call, verdict, opinion, reason) };
    }

    private readonly Reflections _reflected = new();

    /// <summary>What the agent reads the first time: why, the way around it this rule usually has, and how to insist.</summary>
    internal static string Reflect(Verdict v) =>
        "Not run yet: " + Why(v) + " This needs the user's OK. " + Alternative(v) +
        "If there is another way to get what you need, take it. If this exact call is really needed, say in one sentence in " +
        "your message why (the user reads it on the question), then make the same call again: then the user is asked.";

    /// <summary>The usual way around the default ask rules.</summary>
    private static string Alternative(Verdict v) =>
        // (a powershell command already hears "use pwsh" in Why)
        v.Kind == "path" && v.Rule.Contains(".netpi", StringComparison.OrdinalIgnoreCase)
            ? "This is NetPI's own data: read its state with the diag tool (overview, settings, errors, logs, messages) or the read " +
              "tool, which may read here; change a setting only when the user asked for that change. "
            : "";

    /// <summary>The agent's words around the call, for the user's question card: the text of the message that made it.</summary>
    internal static string? ReasonOf(ChatMessage? latest)
    {
        var text = latest?.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= 400 ? text : text[..400] + "…";
    }

    private bool AskReflect()
    {
        try { return ctx.Settings.Get("guardrails.askReflect", true); }
        catch { return true; }
    }

    private static ToolCallDecision Block(string reason) => new() { Block = true, Reason = reason };

    private static string Why(Verdict v) => v.Kind switch
    {
        "sleep" => $"`{v.Subject}` waits {v.Detail} (guardrails.maxSleepSeconds). Run the work itself with a matching " +
            "`timeout` \u2014 the call returns the moment it finishes \u2014 or start it with `background: true` and wait for it with `process wait`.",
        "command" => $"`{v.Subject}` matches the guardrail `{v.Rule}` (guardrails.commands)." +
            (IsWindowsPowerShell(v.Subject)
                ? " Windows PowerShell 5.1 is not used here: run it with `pwsh` (PowerShell 7) or the pwsh tool."
                : IsPwsh(v.Subject) ? " PowerShell 7 is a tool here, not a program a shell command starts: run it with the pwsh tool."
                : ""),
        _ => $"{v.Subject} is protected by the guardrail `{v.Rule}` (guardrails.paths).",
    };

    private static bool IsWindowsPowerShell(string command) =>
        System.Text.RegularExpressions.Regex.IsMatch(command, @"^\W*(\S*[\\/])?powershell(\.exe)?(\s|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsPwsh(string command) =>
        System.Text.RegularExpressions.Regex.IsMatch(command, @"^\W*(\S*[\\/])?pwsh(\.exe)?(\s|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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

    /// <summary>The longest wait a command may sleep through (0: the rule is off).</summary>
    private double MaxSleep()
    {
        try { return ctx.Settings.Get("guardrails.maxSleepSeconds", Sleeps.DefaultMaxSeconds); }
        catch { return Sleeps.DefaultMaxSeconds; }
    }

    /// <summary>Whether a long wait blocks or asks you first.</summary>
    private bool AskOnSleep()
    {
        try { return ctx.Settings.Get("guardrails.maxSleepAction", "block") is { Length: > 0 } action && action.Trim().Equals("ask", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
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
