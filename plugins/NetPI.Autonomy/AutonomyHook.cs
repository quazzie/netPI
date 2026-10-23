using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Autonomy;

/// <summary>
/// After the guardrails (<see cref="Order"/> 10 100), so it sees only calls that will run. In a chat started by a schedule or a
/// webhook (or a subagent of one) a call that changes something — not marked read-only by its tool — is checked
/// against the brakes (<c>autonomy.paused</c>, <c>autonomy.maxChangesPerHour</c>) and, when the chat read outside text,
/// against <c>autonomy.taintedBlocked</c>; a file it writes is copied first (the undo), and once it ran it is a journal row.
/// After any call of an external tool (<c>autonomy.externalTools</c>) the chat is tainted. The owner's own chats and their
/// subagents are never blocked or journaled here (their asks still go through <see cref="Grants"/>).
/// </summary>
internal sealed class AutonomyHook(IPluginContext ctx, Chats chats, AutonomyStore store, Options options, TimeProvider time) : IAgentHook
{
    public int Order => 10_100;

    /// <summary>File copies taken before a call ran, by chat and call id (two chats' providers may reuse an id), until its result is journaled.</summary>
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    /// <summary>Origins whose cap tripped, and when: one notice per origin per hour.</summary>
    private readonly ConcurrentDictionary<string, long> _braked = new();

    private sealed record Pending(long AtMs, List<JsonObject> Checkpoints, string? Note);

    private static string PendingKey(AgentRunContext run, ToolCallPart call) => run.Session.Id + "\0" + call.Id;

    public ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call)
    {
        var run = turn.Run;
        var session = chats.Get(run.Session.Id) ?? run.Session;
        var origin = chats.OriginOf(session);
        if (origin.IsUser) return ValueTask.FromResult<ToolCallDecision?>(null);
        var (name, raw) = ToolCalls.Effective(call);
        var args = ToolArgs.Parse(raw);
        if (ReadOnly(name, raw)) return ValueTask.FromResult<ToolCallDecision?>(null);

        if (chats.Tainted(session) && options.TaintedRule(name, ToolPathArgs.ActionOf(name, args)) is { } line)
        {
            store.Refused(origin, session, name, "tainted", Subject(name, args), rule: line);
            Changed("refusal");
            return Block($"This chat read text from outside (a webhook's payload, a web page, an MCP server), so `{name}` " +
                         $"may only read here (autonomy.taintedBlocked: `{line}`). Nothing ran. Go on without it, and say in your report what you would have done.");
        }
        if (options.Paused)
            return Block("The owner paused changes by chats nobody watches (the Autonomy tab). Nothing ran. Go on reading if that helps, and say in your report what you would have changed.");
        if (options.MaxChangesPerHour is > 0 and var cap)
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            if (store.Changes(origin.Id, now - 3_600_000) >= cap)
            {
                Braked(origin, session, cap, now);
                return Block($"Chats for {origin.Label} made {cap} changes in the last hour (autonomy.maxChangesPerHour), which looks like a loop. " +
                             "Nothing ran. Stop changing things, and say in your report what you were doing.");
            }
        }
        _pending[PendingKey(run, call)] = Checkpoint(run, call, name, args);
        return ValueTask.FromResult<ToolCallDecision?>(null);
    }

    public ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result)
    {
        try
        {
            var run = turn.Run;
            var (name, raw) = ToolCalls.Effective(call);
            var session = chats.Get(run.Session.Id) ?? run.Session;
            var origin = chats.OriginOf(session);
            if (IsExternal(name) && !origin.IsUser && !chats.Tainted(session)) chats.Taint(session.Id, name);
            _pending.TryRemove(PendingKey(run, call), out var pending);
            if (pending is null)
            {
                // a call the provider ran itself (the Claude CLI's own tools) was never asked here, so no brake or copy stood before
                // it: the journal still says what it changed, and counts toward the hourly cap of the calls after it
                if (!origin.IsUser && result.Details?["providerExecuted"] is JsonValue ran && ran.TryGetValue<bool>(out var executed) && executed && !ReadOnly(name, raw))
                {
                    store.Record(origin, session, name, Subject(name, ToolArgs.Parse(raw)), result.IsError, null, "run by the model's own tools: no copy, no brake");
                    Changed("journal");
                }
                Sweep();
                return ValueTask.CompletedTask;
            }
            foreach (var c in pending.Checkpoints) c["afterHash"] = Hash(c["path"]!.GetValue<string>());
            store.Record(origin, session, name, Subject(name, ToolArgs.Parse(raw)), result.IsError,
                pending.Checkpoints.Count switch { 0 => null, 1 => pending.Checkpoints[0], _ => new JsonObject { ["files"] = new JsonArray([.. pending.Checkpoints]) } },
                pending.Note);
            Changed("journal");
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Autonomy could not record {Tool}", call.Name); }
        return ValueTask.CompletedTask;
    }

    /// <summary>Whether a call only reads, by its tool's own marks (the same ones plan mode trusts).</summary>
    private bool ReadOnly(string name, string raw)
    {
        var tool = ctx.Tools.Get(name);
        if (tool?.Definition.ReadOnly == true) return true;
        if (tool is not IReadOnlyCalls rc) return false;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            return rc.IsReadOnly(ToolArgs.Unwrap(doc.RootElement).Clone());
        }
        catch (JsonException) { return false; }
    }

    private bool IsExternal(string name)
    {
        var reg = ctx.Tools.Registrations.FirstOrDefault(r => r.Tool.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return options.IsExternal(name, reg?.PluginId);
    }

    /// <summary>Copies of the local files a write or edit is about to change (the undo); none for other tools.</summary>
    private Pending Checkpoint(AgentRunContext run, ToolCallPart call, string name, ToolArgs args)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var list = new List<JsonObject>();
        string? note = null;
        if (name.Equals("write", StringComparison.OrdinalIgnoreCase) || name.Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var target in ToolPathArgs.WriteTargets(name, args))
            {
                try
                {
                    var path = Resolve(run, call, target);
                    var existed = File.Exists(path);
                    byte[]? content = null;
                    if (existed)
                    {
                        if (new FileInfo(path).Length > options.MaxCheckpointBytes) { note = $"{path} is larger than autonomy.maxCheckpointKb: no copy kept"; continue; }
                        content = File.ReadAllBytes(path);
                    }
                    list.Add(new JsonObject { ["id"] = store.Keep(path, existed, content), ["path"] = path, ["existed"] = existed });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    note = $"no copy of {target}: {ex.Message}";
                }
            }
        }
        return new Pending(now, list, note);
    }

    /// <summary>Copies of calls that never came back (blocked by a later hook, the run stopped) are dropped after ten minutes.</summary>
    private void Sweep()
    {
        var old = time.GetUtcNow().ToUnixTimeMilliseconds() - 600_000;
        foreach (var (id, p) in _pending)
            if (p.AtMs < old) _pending.TryRemove(id, out _);
    }

    private void Braked(Origin origin, SessionInfo session, int cap, long now)
    {
        if (_braked.TryGetValue(origin.Id, out var at) && now - at < 3_600_000) return;
        _braked[origin.Id] = now;
        ctx.Logger.LogWarning("Autonomy: {Origin} reached {Cap} changes in an hour; further changes are refused", origin.Label, cap);
        ctx.Events.Publish("autonomy.braked", new JsonObject
        {
            ["origin"] = origin.Id, ["label"] = origin.Label, ["sessionId"] = session.Id, ["title"] = session.Title, ["limit"] = cap,
        });
    }

    /// <summary>What the call did, in one line: the command it ran, the files it wrote, or its action.</summary>
    internal static string Subject(string name, ToolArgs args)
    {
        if (ToolPathArgs.WindowsOpenCommand(name, args) is { } open) return open;
        if (ToolPathArgs.CommandOf(name, args) is { Length: > 0 } command)
            return name.Equals("ssh", StringComparison.OrdinalIgnoreCase) && args.Str(ToolPathArgs.SshHostNames) is { Length: > 0 } host ? $"{host}: {command}" : command;
        if (name.Equals("write", StringComparison.OrdinalIgnoreCase) || name.Equals("edit", StringComparison.OrdinalIgnoreCase))
            return string.Join(", ", ToolPathArgs.WriteTargets(name, args));
        var action = ToolPathArgs.ActionOf(name, args);
        var detail = args.Str("host", "path", "url", "app", "name", "id");
        return string.Join(" ", new[] { action, detail }.Where(s => !string.IsNullOrEmpty(s)));
    }

    internal static string Hash(string path)
    {
        try { return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "unreadable"; }
    }

    private static string Resolve(AgentRunContext run, ToolCallPart call, string path) => new ToolContext
    {
        SessionId = run.Session.Id,
        AgentId = run.Agent.Id,
        CallId = call.Id,
        Cwd = run.Cwd,
        Services = run.Services,
        Events = run.Events,
    }.ResolvePath(path);

    private void Changed(string what) => ctx.Events.Publish("autonomy.changed", new JsonObject { ["what"] = what });

    private static ValueTask<ToolCallDecision?> Block(string reason) =>
        ValueTask.FromResult<ToolCallDecision?>(new ToolCallDecision { Block = true, Reason = reason });
}
