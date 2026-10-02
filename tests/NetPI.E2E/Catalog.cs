namespace NetPI.E2E;

/// <summary>
/// What the tests are called and grouped as. An id is <c>area.name</c>; the area is also a tag, so <c>--tag retry</c> runs every
/// retry.* test. <c>smoke</c> is the short list that covers each boundary once (the everyday check); everything else stays in
/// the full run. Keep this list small and keep it honest: <c>--list</c> shows it, and <c>--self-test</c> fails on a tag
/// for an id that does not exist.
/// </summary>
public static class Catalog
{
    /// <summary>Tags beyond the area. smoke = one representative per boundary (startup, chat+tool round trip, the provider
    /// adapters, control and scheduling, persistence and reload). ui = drives a browser (needs node + Playwright).</summary>
    private static readonly Dictionary<string, string> Extra = new()
    {
        ["startup.plugins"] = "smoke",
        ["chat.stream"] = "smoke",
        ["tools.files"] = "smoke",
        ["provider.chat-transport"] = "smoke",
        ["provider.anthropic-thinking"] = "smoke",
        ["control.abort-stream"] = "smoke",
        ["slots.subagents"] = "smoke scheduling",
        ["lifecycle.shutdown-restart"] = "smoke lifecycle",
        ["reload.all-plugins"] = "smoke lifecycle",
        ["reload.file-change"] = "lifecycle",
        ["reload.provider-midstream"] = "lifecycle",
        ["reload.runtime-midrun"] = "lifecycle",
        ["agents.setup"] = "scheduling",
        ["slots.three-chats"] = "scheduling",
        ["slots.abort-queued"] = "scheduling",
        ["subagents.nested"] = "scheduling",
        ["subagents.report-wakes-parent"] = "scheduling",
        ["subagents.wait-steer"] = "scheduling",
        ["subagents.abort-orchestrator"] = "scheduling",
        ["subagents.abort-one"] = "scheduling",
        ["sessions.delete-orchestrator"] = "scheduling",
        ["inspect.diag"] = "needs-node",
        ["ui.mcp-panel"] = "needs-node",
        ["ui.idea-image"] = "needs-node ideas",
        ["ui.smoke"] = "needs-node",
    };

    public static IReadOnlyCollection<string> ExtraIds => Extra.Keys;

    public static string Area(string id)
    {
        var dot = id.IndexOf('.');
        return dot < 0 ? id : id[..dot];
    }

    public static IReadOnlyList<string> TagsOf(string id)
    {
        var tags = new List<string> { Area(id) };
        if (Extra.TryGetValue(id, out var more)) tags.AddRange(more.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return tags;
    }

    /// <summary>What a test is expected to cost when the timing history has no figure for it yet (ms).</summary>
    public static int DefaultEstimateMs(TestCase c) => Area(c.Id) == "ui" ? 20_000 : 4_000;
}
