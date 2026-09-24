using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Todo;

/// <summary>
/// The agent's checklist for the current task: <c>todo_write</c> replaces the session's list, which is kept in the
/// session's meta (<c>meta.todo</c>: <c>[{ text, status }]</c>, status pending | in_progress | done) so the UI can show it
/// and it survives restarts. The tool result repeats the list for the model; when compaction removed it while items are
/// open, a "todo" notice brings the list back (<see cref="TodoNotices"/>).
/// </summary>
[NetPiPlugin("netpi.todo", Name = "Todo", Description = "A checklist the agent keeps for multi-step work (todo_write), shown above the composer", Order = 65)]
public sealed class TodoPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Tools.Register(new TodoWriteTool(context));
        context.Services.Register<IAgentHook>(new TodoNotices(context));
        return Task.CompletedTask;
    }
}

/// <summary>
/// The model reads its list from its last <c>todo_write</c> result. Compaction can summarize that away while items are
/// still open (the list itself lives on in the session meta), so before a model call with open items and no
/// <c>todo_write</c> or earlier "todo" notice left in the context, the current list is appended as a "todo" notice.
/// </summary>
internal sealed class TodoNotices(IPluginContext ctx) : IAgentHook
{
    public const string Kind = "todo";

    /// <summary>After compaction (-100) and the context notices (500–520).</summary>
    public int Order => 530;

    public async ValueTask OnBeforeModelCallAsync(AgentTurnContext turn)
    {
        if (Visible(turn.Messages)) return;
        var sessionId = turn.Run.Session.Id;
        var items = TodoWriteTool.Stored(ctx.Sessions.GetSession(sessionId));
        if (items.Count == 0 || items.All(i => i.Status == "done")) return;
        ctx.Sessions.AppendMessage(sessionId, ChatMessage.NoticeText(
            "Your todo list (its earlier updates were compacted away); keep it current with todo_write:\n" + TodoWriteTool.Lines(items), Kind));
        await turn.ReloadMessagesAsync().ConfigureAwait(false);
    }

    internal static bool Visible(IReadOnlyList<ChatMessage> context) => context.Any(m =>
        (m.Role == MessageRole.Notice && m.MetaString("kind") == Kind)
        || m.Parts.Any(p => p is ToolResultPart { Name: "todo_write" } or ToolCallPart { Name: "todo_write" }));
}

internal sealed record TodoItem(string Text, string Status);

internal sealed class TodoWriteTool(IPluginContext ctx) : IAgentTool
{
    public const string MetaKey = "todo";
    private const int MaxItems = 50;
    private const int MaxText = 300;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "todo_write",
        Label = "Todo",
        Category = "todo",
        Description =
            "Keep a checklist for the current task. Send the whole list every time: it replaces the previous one. " +
            "Status is pending, in_progress or done. Keep one item in_progress while you work on it and mark it done as soon " +
            "as it is finished; add, drop or reword items when the plan changes. The user sees the list. Use it for work with " +
            "three or more steps, not for simple requests. An empty list clears it.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "The complete checklist, in order.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["text"] = new JsonObject { ["type"] = "string", ["description"] = "What to do, as a short imperative." },
                            ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("pending", "in_progress", "done") },
                        },
                        ["required"] = new JsonArray("text", "status"),
                    },
                },
            },
            ["required"] = new JsonArray("items"),
        },
        PromptGuidelines =
        [
            "For work with several steps, plan it with todo_write and keep the list current: one item in_progress at a time, done as soon as it is finished.",
        ],
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!TryParse(args, out var items, out var error)) return Task.FromResult(ToolResult.Error(error));
        var list = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["text"] = i.Text, ["status"] = i.Status }).ToArray());
        try
        {
            ctx.Sessions.UpdateSession(context.SessionId, s =>
            {
                s.Meta ??= new JsonObject();
                s.Meta[MetaKey] = list;
            });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return Task.FromResult(ToolResult.Error($"Session {context.SessionId} not found."));
        }
        var done = items.Count(i => i.Status == "done");
        return Task.FromResult(ToolResult.Ok(Render(items, done), new
        {
            items = items.Select(i => new { text = i.Text, status = i.Status }).ToArray(),
            done,
            total = items.Count,
        }));
    }

    internal static string Render(IReadOnlyList<TodoItem> items, int done) =>
        items.Count == 0 ? "Todo list cleared." : $"Todo list updated ({done}/{items.Count} done):\n" + Lines(items);

    internal static string Lines(IReadOnlyList<TodoItem> items) =>
        string.Join('\n', items.Select(i => (i.Status switch { "done" => "[x] ", "in_progress" => "[>] ", _ => "[ ] " }) + i.Text));

    /// <summary>The list kept in the session's meta.</summary>
    internal static List<TodoItem> Stored(SessionInfo? session) =>
        (session?.Meta?[MetaKey] as JsonArray ?? [])
        .Select(n => new TodoItem(n?["text"]?.GetValue<string>() ?? "", NormStatus(n?["status"]?.GetValue<string>())))
        .Where(i => i.Text.Length > 0)
        .ToList();

    /// <summary>
    /// Lenient: <c>items</c> (or todos / tasks / list) as objects or plain strings; text / content / title / task; status
    /// synonyms (completed, active, todo…). Arguments sent as a JSON string are unwrapped.
    /// </summary>
    internal static bool TryParse(JsonElement args, out List<TodoItem> items, out string error)
    {
        items = [];
        error = "";
        args = Unwrap(args);
        var arr = args.ValueKind == JsonValueKind.Array ? args : Get(args, "items", "todos", "tasks", "list", "todo");
        if (arr is { ValueKind: JsonValueKind.String } s) arr = Unwrap(s);
        if (arr is not { ValueKind: JsonValueKind.Array } list)
        {
            error = "todo_write needs \"items\": an array of { \"text\", \"status\" } (status: pending, in_progress or done).";
            return false;
        }
        foreach (var e in list.EnumerateArray())
        {
            string? text;
            string? status = null;
            if (e.ValueKind == JsonValueKind.String) text = e.GetString();
            else if (e.ValueKind == JsonValueKind.Object)
            {
                text = Str(e, "text", "content", "title", "task", "description", "name");
                status = Str(e, "status", "state");
                if (status is null && Get(e, "done", "completed") is { ValueKind: JsonValueKind.True }) status = "done";
            }
            else continue;
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            if (text.Length > MaxText) text = text[..MaxText] + "…";
            items.Add(new TodoItem(text, NormStatus(status)));
            if (items.Count == MaxItems) break;
        }
        return true;
    }

    internal static string NormStatus(string? status) => status?.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
    {
        "done" or "completed" or "complete" or "finished" or "x" or "[x]" => "done",
        "in_progress" or "inprogress" or "active" or "doing" or "current" or "started" or "working" => "in_progress",
        _ => "pending",
    };

    private static JsonElement Unwrap(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) return e;
        try
        {
            using var doc = JsonDocument.Parse(e.GetString() ?? "{}");
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return e; }
    }

    private static string Norm(string s) => s.Replace("_", "").Replace("-", "").ToLowerInvariant();

    private static JsonElement? Get(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            foreach (var p in e.EnumerateObject())
                if (Norm(p.Name) == Norm(name) && p.Value.ValueKind != JsonValueKind.Null) return p.Value;
        return null;
    }

    private static string? Str(JsonElement e, params string[] names) => Get(e, names) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
}
