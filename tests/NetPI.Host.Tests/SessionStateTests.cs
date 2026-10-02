using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Events;

namespace NetPI.Host.Tests;

/// <summary>
/// The owner of per-session state a plugin keeps. Sessions are not a bounded set — every subagent spawn creates one and
/// <c>sessions.delete</c> removes its rows but nothing in this process — so the map has to drop the entry when the
/// session is deleted, or it tracks every session the process has ever seen (idea-bv3iw4). Six plugins kept six maps
/// and none of them released anything, which is why the rule lives in one type.
/// </summary>
public static class SessionStateTests
{
    public static void Register(TestRunner r)
    {
        r.Add("session state: deleting a session drops its entry, and only its entry", DeletesWithTheSession);
        r.Add("session state: forget, clear, and the no-bus map the caller releases itself", ForgetAndClear);
        r.Add("session state: every plugin that kept a per-session map owns its release", ThePluginsDelegateToTheOwner);
    }

    private static async Task DeletesWithTheSession()
    {
        await using var bus = new EventBus(NullLogger.Instance);
        var state = new SessionState<string>(bus);
        state.GetOrAdd("ses_1", _ => "gate one");
        state.GetOrAdd("ses_2", _ => "gate two");
        state.GetOrAdd("ses_3", _ => "gate three");
        Check.Equal(3, state.Count);
        Check.Equal("gate two", state["ses_2"]);
        Check.True(state.Contains("ses_3") && state.TryGetValue("ses_3", out var three) && three == "gate three");

        // what sessions.delete publishes: { id } as a payload object
        bus.Publish(EventTypes.SessionDeleted, new JsonObject { ["id"] = "ses_2" });
        await bus.FlushAsync();
        Check.Equal(2, state.Count, "the deleted session's entry is gone");
        Check.False(state.Contains("ses_2"));
        Check.True(state.Sessions.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(["ses_1", "ses_3"]));

        // an event with no id (some other shape) must not empty the map
        bus.Publish(EventTypes.SessionDeleted, new JsonObject());
        bus.Publish(EventTypes.SessionDeleted, new JsonObject { ["id"] = "" });
        await bus.FlushAsync();
        Check.Equal(2, state.Count, "an event without a session id releases nothing");

        // the envelope form works too: a bus that carries the session itself
        bus.Publish(EventTypes.SessionDeleted, new JsonObject(), "ses_3");
        await bus.FlushAsync();
        Check.Equal(1, state.Count);
        Check.True(state.Forget("ses_1"), "the caller's own release");
        Check.Equal(0, state.Count);
        Check.False(state.Forget("ses_1"), "forgetting an entry that is already gone says so");
    }

    private static Task ForgetAndClear()
    {
        // without a bus the owner releases the entries itself (and the count is what a leak test watches)
        var own = new SessionState<int>();
        own["ses_1"] = 1;
        own.GetOrAdd("ses_2", _ => 2);
        Check.Equal(2, own.Count);
        Check.True(own.Forget("ses_1"));
        Check.Equal(1, own.Count);
        own.Clear();
        Check.Equal(0, own.Count);
        Check.False(own.Contains("ses_2"));
        Check.False(own.TryGetValue("ses_2", out _));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The six maps that were never released, one line each: the owner is constructed with the plugin's bus (so the
    /// release cannot be forgotten at the call site) or released from the plugin's own session.deleted hook. Read from
    /// the sources because these are private fields of plugin classes, as a plugin leak is invisible from outside.
    /// </summary>
    private static Task ThePluginsDelegateToTheOwner()
    {
        foreach (var (file, gone) in new[]
        {
            ("plugins/NetPI.Context/ToolNotices.cs", "ConcurrentDictionary<string, object> _gates"),
            ("plugins/NetPI.Context/ProjectNotices.cs", "ConcurrentDictionary<string, object> _gates"),
            ("plugins/NetPI.Skills/SkillNotices.cs", "ConcurrentDictionary<string, object> _gates"),
            ("plugins/NetPI.Compaction/CompactionService.cs", "ConcurrentDictionary<string, SemaphoreSlim> _locks"),
            ("plugins/NetPI.Goal/Goals.cs", "ConcurrentDictionary<string, int> _started"),
        })
        {
            var source = File.ReadAllText(Path.Combine(T.RepoRoot, file.Replace('/', Path.DirectorySeparatorChar)));
            Check.Contains(source, "SessionState<", $"{file}: its per-session map is owned by SessionState<T>");
            Check.NotContains(source, gone, $"{file}: the raw map is gone");
        }

        // the gate maps that take the bus themselves, and the two that a plugin's own session.deleted hook releases
        foreach (var file in new[] { "plugins/NetPI.Context/ProjectNotices.cs", "plugins/NetPI.Skills/SkillNotices.cs", "plugins/NetPI.Compaction/CompactionService.cs" })
        {
            var source = File.ReadAllText(Path.Combine(T.RepoRoot, file.Replace('/', Path.DirectorySeparatorChar)));
            Check.Contains(source, "new(ctx.Events)", $"{file}: the map is built with the plugin's bus, so deletion releases it");
        }
        var goals = File.ReadAllText(Path.Combine(T.RepoRoot, "plugins", "NetPI.Goal", "GoalPlugin.cs"));
        Check.Contains(goals, "EventTypes.SessionDeleted", "the goal plugin releases its per-session state on deletion");
        var prompts = File.ReadAllText(Path.Combine(T.RepoRoot, "plugins", "NetPI.Context", "SystemPromptBuilder.cs"));
        Check.Contains(prompts, "_reset.TryRemove", "deleting a session drops the reset counter Reset only ever writes");
        return Task.CompletedTask;
    }
}
