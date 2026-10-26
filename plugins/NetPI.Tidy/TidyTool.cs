using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tidy;

/// <summary>
/// <c>tidy { action: "sweep", staleDays? }</c>: what is left behind across the repositories (<see cref="TidySweep"/>).
/// Read-only. The weekly cleanup is a NetPI schedule whose prompt calls it and acts on the list (docs/TOOLS.md).
/// </summary>
internal sealed class TidyTool(IPluginContext ctx) : IAgentTool, IReadOnlyCalls
{
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    public ToolDefinition Definition { get; } = new()
    {
        Name = "tidy",
        Label = "Tidy",
        Category = "workspace",
        Description = "Find what agents left behind across the repositories: stale worktrees and branches (merged or not), stashes, old uncommitted changes and old scratch. Read-only.",
        Help =
            "action sweep: one block per repository. [merged] items (fully merged into the base branch and clean) are garbage under the repository rules: " +
            "remove the worktree and delete its branch from the main checkout (git worktree remove, git branch -d). Everything else is somebody's work: " +
            "report it, do not delete it. Repositories are the projects' folders plus tidy.sweepRoots, minus tidy.sweepExclude; only what had no activity " +
            "for staleDays (default tidy.staleDays) counts.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("sweep") },
                ["staleDays"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 365 },
            },
        },
    };

    public bool IsReadOnly(JsonElement args) => true;

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var action = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : "sweep";
        if (action is not (null or "" or "sweep")) return ToolResult.Error($"Unknown action '{action}': the only action is sweep.");
        var days = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("staleDays", out var d) && d.TryGetInt32(out var n)
            ? Math.Clamp(n, 0, 365) : (int)Math.Clamp(ctx.Settings.GetOr("tidy.staleDays", 3L), 0, 365);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        List<TidySweep.Item> items;
        try { items = await TidySweep.SweepAsync(Roots(), ctx.Settings.GetStrings("tidy.sweepExclude"), days, DateTimeOffset.UtcNow, budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ToolResult.Error($"The sweep did not finish within {Budget.TotalMinutes:0} minutes."); }
        return ToolResult.Ok(TidySweep.Render(items, days), new
        {
            staleDays = days,
            items = items.Select(i => new { repo = i.Repo, kind = i.Kind, text = i.Text, path = i.Path, branch = i.Branch }),
        });
    }

    /// <summary>The projects' folders and <c>tidy.sweepRoots</c>; each sweep reads them anew, so a new project is included without a restart.</summary>
    private List<string> Roots()
    {
        var roots = ctx.Sessions.ListProjects().Select(p => p.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        roots.AddRange(ctx.Settings.GetStrings("tidy.sweepRoots"));
        return roots;
    }
}
