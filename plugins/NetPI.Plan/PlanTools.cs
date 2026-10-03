using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Plan;

/// <summary><c>plan_submit</c>: the plan, for the user to approve, revise or cancel. Waits for the decision (see <see cref="PlanService.SubmitAsync"/>).</summary>
internal sealed class PlanSubmitTool(PlanService service) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "plan_submit",
        Label = "Plan",
        Category = "plan",
        Description = "Plan mode: submit the finished plan for the user's decision (approve, changes, cancel) and wait for it.",
        Help =
            "Call it once you understand the task. title: a few words. summary: what will be done and why, in a few sentences. steps: the plan in " +
            "order, each { text (one imperative sentence), detail? (how, or why it comes here) }. files: what changes, each a path or { path, note }. " +
            "risks: what could go wrong. tests: how it is verified. openQuestions: what is still undecided. Concrete beats complete: no filler steps, " +
            "no restating the task. The reply tells you the decision: approved (carry it out), changes (revise, then call plan_submit again) or cancelled.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string" },
                ["summary"] = new JsonObject { ["type"] = "string" },
                ["steps"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["text"] = new JsonObject { ["type"] = "string" },
                            ["detail"] = new JsonObject { ["type"] = "string" },
                        },
                        ["required"] = new JsonArray("text"),
                    },
                },
                ["files"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string" },
                            ["note"] = new JsonObject { ["type"] = "string" },
                        },
                        ["required"] = new JsonArray("path"),
                    },
                },
                ["risks"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["tests"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["openQuestions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            },
            ["required"] = new JsonArray("title", "steps"),
        },
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct) =>
        PlanBody.TryParse(args, out var plan, out var error) ? service.SubmitAsync(context, plan, ct) : Task.FromResult(ToolResult.Error(error));
}

/// <summary><c>plan_enter</c>: the model offers plan mode for a large or unclear change; the user answers on a card.</summary>
internal sealed class PlanEnterTool(PlanService service) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "plan_enter",
        Label = "Plan mode",
        Category = "plan",
        Description = "Offer plan mode to the user (they confirm) before a large, risky or unclear change: nothing changes until a plan is approved.",
        Help =
            "For work that touches many files, has several reasonable approaches, or could do damage if misread. The user sees your reason on a card " +
            "and enters plan mode or declines; on entering you explore read-only and submit a plan with plan_submit. Not for small, clear tasks.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["reason"] = new JsonObject { ["type"] = "string" } },
        },
        PromptGuidelines =
        [
            "For a large, risky or unclear change, offer plan mode with plan_enter before touching anything; for a small, clear task just do the work.",
        ],
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct) =>
        service.OfferAsync(context, PlanBody.Clip(new ToolArgs(args).Str("reason", "why"), 400), ct);
}
