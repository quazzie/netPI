using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Ideas;

/// <summary>
/// The agent-facing tools for the per-project ideas bank (ideas.json in the
/// session workspace). "Store it in ideas" from the user becomes idea.add;
/// "pick an idea to continue" becomes idea.list + idea.get; progress updates
/// become idea.update.
/// </summary>
internal abstract class IdeaToolBase : IAgentTool
{
    /// <summary>Same flat (property → description) schema convention as the other plugin tools.</summary>
    internal static class Json
    {
        public static JsonElement Obj(params (string k, object? v)[] props)
        {
            var obj = new Dictionary<string, object?>();
            foreach (var (k, val) in props) obj[k] = val;
            return JsonSerializer.SerializeToElement(obj);
        }
    }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonElement Parameters { get; }
    public abstract IReadOnlyList<string> Guidelines { get; }
    public abstract ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken);

    /// <summary>The ideas bank of the CURRENT session's project (the tool's workspace).</summary>
    protected IdeasStore StoreFor(ToolContext context) => new(context.Workspace);

    protected static string? S(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    protected static string[]? Tags(JsonElement e)
        => e.TryGetProperty("tags", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : null;

    protected static ToolResult Ok(ToolContext ctx, string toolName, object payload)
        => new("", toolName, [new TextPart(JsonSerializer.Serialize(payload))]);

    protected static ToolResult Err(ToolContext ctx, string toolName, string msg)
        => new("", toolName, [new TextPart(msg)], IsError: true);
}

internal sealed class IdeaAddTool : IdeaToolBase
{
    public override string Name => "idea.add";
    public override string Description => "Store a new idea in this project's ideas bank (ideas.json in the project root). Use it when the user asks to store/keep/remember an idea, capture a discussion outcome, or says \"store it in ideas\". Returns the idea id (the user sees it in the Ideas panel).";
    public override JsonElement Parameters => IdeaToolBase.Json.Obj(
        ("title", "One-line idea title"),
        ("body", "The idea itself: what it is and why it matters"),
        ("notes", "Optional discussion notes"),
        ("plan", "Optional concrete plan/steps"),
        ("status", "Optional status (idea|planned|in-progress|done|dropped; default idea)"),
        ("tags", "Optional array of short tags"));
    public override IReadOnlyList<string> Guidelines =>
    [
        "Store the idea for the CURRENT project (the session workspace) — do not pass paths.",
        "When the user discusses an idea and asks to keep it, capture the whole point (title + body), not a fragment.",
    ];

    public override ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var title = S(context.Arguments, "title") ?? "";
            var store = StoreFor(context);
            var rec = store.Add(title, S(context.Arguments, "body"), S(context.Arguments, "notes"),
                S(context.Arguments, "plan"), S(context.Arguments, "status"), Tags(context.Arguments));
            return ValueTask.FromResult(Ok(context, Name, new
            {
                ok = true,
                idea = rec,
                file = store.FilePath,
                note = "Stored in the project's ideas bank — visible to the user in the Ideas panel.",
            }));
        }
        catch (IdeasStoreException ex) { return ValueTask.FromResult(Err(context, Name, ex.Message)); }
        catch (Exception ex) { return ValueTask.FromResult(Err(context, Name, $"idea.add failed: {ex.Message}")); }
    }
}

internal sealed class IdeaListTool : IdeaToolBase
{
    public override string Name => "idea.list";
    public override string Description => "List the ideas stored in this project's ideas bank (ideas.json in the project root). Optional status filter (idea|planned|in-progress|done|dropped).";
    public override JsonElement Parameters => IdeaToolBase.Json.Obj(
        ("status", "Optional: only ideas with this status"));
    public override IReadOnlyList<string> Guidelines =>
    [
        "Use this first when the user asks to pick/continue/choose an idea — then open the chosen one with idea.get.",
    ];

    public override ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var store = StoreFor(context);
            var status = S(context.Arguments, "status");
            var ideas = store.Load()
                .Where(i => string.IsNullOrEmpty(status) || string.Equals(i.Status, status, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.UpdatedAt)
                .Select(i => new { i.Id, i.Title, i.Status, i.Tags, i.UpdatedAt })
                .ToList();
            return ValueTask.FromResult(Ok(context, Name, new { ok = true, file = store.FilePath, count = ideas.Count, ideas }));
        }
        catch (IdeasStoreException ex) { return ValueTask.FromResult(Err(context, Name, ex.Message)); }
        catch (Exception ex) { return ValueTask.FromResult(Err(context, Name, $"idea.list failed: {ex.Message}")); }
    }
}

internal sealed class IdeaGetTool : IdeaToolBase
{
    public override string Name => "idea.get";
    public override string Description => "Read one idea from this project's ideas bank by id OR a fragment of its title (case-insensitive). Use it to discuss, plan, or work on a specific idea.";
    public override JsonElement Parameters => IdeaToolBase.Json.Obj(
        ("id", "The idea id or a fragment of its title"));
    public override IReadOnlyList<string> Guidelines =>
    [
        "The full record (body, notes, plan, status) is returned — work from the record, not from memory.",
    ];

    public override ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var id = S(context.Arguments, "id") ?? "";
            var store = StoreFor(context);
            var rec = store.Find(id);
            if (rec is null) return ValueTask.FromResult(Err(context, Name, $"no idea matching \"{id}\" — use idea.list to see what is stored"));
            return ValueTask.FromResult(Ok(context, Name, new { ok = true, file = store.FilePath, idea = rec }));
        }
        catch (IdeasStoreException ex) { return ValueTask.FromResult(Err(context, Name, ex.Message)); }
        catch (Exception ex) { return ValueTask.FromResult(Err(context, Name, $"idea.get failed: {ex.Message}")); }
    }
}

internal sealed class IdeaUpdateTool : IdeaToolBase
{
    public override string Name => "idea.update";
    public override string Description => "Update an idea in this project's ideas bank (by id or title fragment). Pass only the fields that change: notes (append discussion), plan (steps), status (idea|planned|in-progress|done|dropped), title, body, tags.";
    public override JsonElement Parameters => IdeaToolBase.Json.Obj(
        ("id", "The idea id or a fragment of its title"),
        ("title", "New title (only when changing it)"),
        ("body", "New body (replaces the existing body)"),
        ("notes", "New notes (replaces the existing notes)"),
        ("plan", "New plan (replaces the existing plan)"),
        ("status", "New status"),
        ("tags", "New tags array (replaces the existing tags)"));
    public override IReadOnlyList<string> Guidelines =>
    [
        "While working on an idea keep it current: set status to \"in-progress\" when starting and \"done\" when finished; update notes/plan as the work progresses.",
        "Pass only the fields that change — omitted fields are left as-is.",
    ];

    public override ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var id = S(context.Arguments, "id") ?? "";
            var store = StoreFor(context);
            var rec = store.Update(id, S(context.Arguments, "title"), S(context.Arguments, "body"),
                S(context.Arguments, "notes"), S(context.Arguments, "plan"),
                S(context.Arguments, "status"), Tags(context.Arguments));
            if (rec is null) return ValueTask.FromResult(Err(context, Name, $"no idea matching \"{id}\" — use idea.list to see what is stored"));
            return ValueTask.FromResult(Ok(context, Name, new { ok = true, file = store.FilePath, idea = rec }));
        }
        catch (IdeasStoreException ex) { return ValueTask.FromResult(Err(context, Name, ex.Message)); }
        catch (Exception ex) { return ValueTask.FromResult(Err(context, Name, $"idea.update failed: {ex.Message}")); }
    }
}

internal sealed class IdeaRemoveTool : IdeaToolBase
{
    public override string Name => "idea.remove";
    public override string Description => "Delete an idea from this project's ideas bank (by id or title fragment). Use it only when the user asks to drop/forget an idea.";
    public override JsonElement Parameters => IdeaToolBase.Json.Obj(
        ("id", "The idea id or a fragment of its title"));
    public override IReadOnlyList<string> Guidelines =>
    [
        "Prefer setting status to \"dropped\" over deleting, unless the user explicitly asks to remove it — the bank is the user's memory.",
    ];

    public override ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var id = S(context.Arguments, "id") ?? "";
            var store = StoreFor(context);
            var ok = store.Remove(id);
            return ValueTask.FromResult(Ok(context, Name, new { ok, file = store.FilePath, removed = ok ? id : (string?)null }));
        }
        catch (IdeasStoreException ex) { return ValueTask.FromResult(Err(context, Name, ex.Message)); }
        catch (Exception ex) { return ValueTask.FromResult(Err(context, Name, $"idea.remove failed: {ex.Message}")); }
    }
}
