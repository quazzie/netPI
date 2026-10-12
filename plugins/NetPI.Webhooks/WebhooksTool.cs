using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Webhooks;

/// <summary>
/// The <c>webhooks</c> tool: what an agent may do with webhooks. It reads (list, get, history), tries a payload without starting
/// anything (test), and pauses one. It cannot create, enable, delete or re-key a webhook: a webhook is a door into the machine from
/// the network, its token must never pass through a chat, and a chat started by a webhook must not be able to widen what starts chats
/// (idea-96bh9b). Those are the user's, in the Webhooks tab.
/// </summary>
public sealed class WebhooksTool(IPluginContext ctx) : IAgentTool, IReadOnlyCalls
{
    private readonly IPluginContext _ctx = ctx;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "webhooks",
        Label = "Webhooks",
        Category = "webhooks",
        SummaryArg = "action",
        ReadOnly = false,
        Description = "Inbound webhooks that start chats (Checkmk alerts, CI, scripts): list, get, history, test a payload, pause. The user manages them in the Webhooks tab.",
        PromptGuidelines =
        [
            "A chat that starts with a webhook alert was started by the webhook named in its first message; its history (webhooks action history) shows what else arrived. Use action test to see what a payload would do before suggesting a change; tell the user to edit or enable webhooks in the Webhooks tab.",
        ],
        Help = """
            Actions:
            - list: every webhook, one line each (state, agent, project, last delivery).
            - get: one webhook by id, with its prompt template, match conditions and limits.
            - history: deliveries, newest first (id optional, limit default 20): started, skipped (and why) or failed.
            - test: id and payload (any JSON) — shows the filled-in prompt and whether the payload would be filtered, deduplicated or capped. Starts nothing.
            - pause: id — pauses a webhook (enabled false). Resuming, creating, deleting and re-keying are the user's, in the Webhooks tab.
            """,
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "string", ["description"] = "list | get | history | test | pause", ["enum"] = new JsonArray(["list", "get", "history", "test", "pause"]) },
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "get/history/test/pause: the webhook's id" },
                ["payload"] = new JsonObject { ["description"] = "test: the JSON payload to try" },
                ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "history: how many deliveries (default 20)" },
            },
        },
    };

    public bool IsReadOnly(JsonElement args) => new ToolArgs(args).Str("action")?.Trim().ToLowerInvariant() is null or "list" or "get" or "history" or "test" or "show";

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        try { return await RunAsync(args, ct).ConfigureAwait(false); }
        catch (ToolCallError ex) { return ToolResult.Error(ex.Message); }
    }

    private async Task<ToolResult> RunAsync(JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var action = a.Str("action")?.Trim().ToLowerInvariant() ?? "list";
        switch (action)
        {
            case "list":
            {
                var list = await RpcAsync<JsonArray>("webhooks.list", new JsonObject(), ct).ConfigureAwait(false);
                var all = list.OfType<JsonObject>().ToList();
                var head = all.Count == 0 ? "No webhooks. The user adds them in the Webhooks tab." : $"{all.Count} webhook{(all.Count == 1 ? "" : "s")} ({all.Count(WebhookStore.Enabled)} enabled):";
                return ToolResult.Ok(string.Join("\n", [head, .. all.Select(Line)]), new { kind = "webhooks", webhooks = all });
            }
            case "get" or "show":
            {
                var h = await RpcAsync<JsonObject>("webhooks.get", new JsonObject { ["id"] = RequireId(a) }, ct).ConfigureAwait(false);
                var text = string.Join("\n", [Line(h), "prompt: " + h["prompt"]?.GetValue<string>(), "title: " + (h["title"]?.GetValue<string>() ?? "(the name)"),
                    "match: " + (h["match"]?.ToJsonString() ?? "[]"), "dedupe: " + (h["dedupe"]?.ToJsonString() ?? "{}") +
                    $" · max {h["maxPerHour"]} per hour · {h["maxConcurrent"]} at once"]);
                return ToolResult.Ok(text, new { kind = "webhook", webhook = h });
            }
            case "history":
            {
                var rows = (await RpcAsync<JsonArray>("webhooks.history", new JsonObject { ["id"] = a.Str("id"), ["limit"] = a.Int("limit") ?? 20 }, ct).ConfigureAwait(false)).OfType<JsonObject>().ToList();
                var lines = rows.Count == 0 ? ["No deliveries yet."] : new[] { $"{rows.Count} deliveries, newest first:" }.Concat(rows.Select(h =>
                    string.Join(" · ", new[] { Local(h["at"]?.GetValue<string>()), h["status"]?.GetValue<string>() ?? "?", h["summary"]?.GetValue<string>(),
                        h["reason"]?.GetValue<string>(), h["sessionId"]?.GetValue<string>() is { Length: > 0 } s ? "chat " + s : null }.Where(x => !string.IsNullOrEmpty(x))))).ToList();
                return ToolResult.Ok(string.Join("\n", lines), new { kind = "webhook-history", runs = rows });
            }
            case "test":
            {
                var id = RequireId(a);
                JsonNode? payload = new JsonObject();
                if (a.TryGet(out var p, "payload"))
                    payload = p.ValueKind == JsonValueKind.String ? ParseOrWrap(p.GetString() ?? "") : JsonNode.Parse(p.GetRawText());
                var plan = await RpcAsync<JsonObject>("webhooks.test", new JsonObject { ["id"] = id, ["payload"] = payload, ["run"] = false }, ct).ConfigureAwait(false);
                var skip = plan["skip"]?.GetValue<string>();
                var text = (skip is null ? "This payload would start a chat" + (plan["agent"]?.GetValue<string>() is { Length: > 0 } ag ? $" on agent {ag}" : "") + "." : "This payload would be skipped: " + skip + ".")
                    + "\ntitle: " + plan["title"]?.GetValue<string>() + "\nprompt:\n" + plan["prompt"]?.GetValue<string>();
                return ToolResult.Ok(text, new { kind = "webhook-test", plan });
            }
            case "pause":
            {
                var id = RequireId(a);
                var h = await RpcAsync<JsonObject>("webhooks.update", new JsonObject { ["id"] = id, ["enabled"] = false }, ct).ConfigureAwait(false);
                return ToolResult.Ok($"Paused {id} “{h["name"]?.GetValue<string>()}”. The user resumes it in the Webhooks tab.", new { kind = "webhook", webhook = h });
            }
            case "add" or "create" or "new" or "update" or "enable" or "resume" or "delete" or "rotate":
                return ToolResult.Error("Creating, changing, enabling, re-keying and deleting webhooks is up to the user, in the Webhooks tab (a webhook is a door in from the network, and its token must not pass through a chat). You can list, get, history, test and pause.");
            default:
                return ToolResult.Error($"Unknown webhooks action '{action}' (list, get, history, test, pause).");
        }
    }

    private static JsonNode ParseOrWrap(string text)
    {
        try { if (JsonNode.Parse(text) is { } n) return n; } catch (JsonException) { /* plain text */ }
        return new JsonObject { ["body"] = text };
    }

    private static string RequireId(ToolArgs a) =>
        a.Str("id")?.Trim() is { Length: > 0 } id ? id : throw new ToolCallError("this action needs an id (action list shows them)");

    private static string Line(JsonObject h)
    {
        var state = WebhookStore.Enabled(h) ? "enabled" : h["proposedBy"] is not null ? "proposed (the user enables it)" : "paused";
        var last = h["lastStatus"]?.GetValue<string>() is { } st
            ? st + (h["lastReason"]?.GetValue<string>() is { Length: > 0 } r ? ": " + Template.Clip(r, 80) : "") + " " + Local(h["lastAt"]?.GetValue<string>())
            : "never";
        return string.Join(" · ", h["id"]?.GetValue<string>() ?? "?", h["name"]?.GetValue<string>() ?? "?", state,
            "agent " + (h["agent"]?.GetValue<string>() is { Length: > 0 } ag ? ag : "default"), "last " + last);
    }

    private static string Local(string? iso)
    {
        if (iso is null || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return "–";
        return at.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    }

    private async Task<T> RpcAsync<T>(string method, JsonObject p, CancellationToken ct) where T : class
    {
        try
        {
            var node = (JsonNode?)await _ctx.Rpc.InvokeAsync(method, p, ct).ConfigureAwait(false);
            return node is T typed ? typed : (T)(object)node!.DeepClone();
        }
        catch (RpcException ex)
        {
            throw new ToolCallError(ex.Message + (ex.Code == "not_found" ? " Use action list for the ids." : ""));
        }
    }

    private sealed class ToolCallError(string message) : Exception(message);
}
