using System.Text.Json;

namespace NetPI.Agent.Tests;

/// <summary>agent_spawn's taskFile: the file is read against the caller's working directory and becomes the task
/// (appended when a task is given too), per entry of a subagents batch; a file that cannot be read refuses the call.</summary>
public static class AgentSpawnTaskFileTests
{
    public static void Register(TestRunner t)
    {
        t.Add("agent_spawn taskFile: a file alone is the task, read against the caller's working directory", TaskFileAlone);
        t.Add("agent_spawn taskFile: with a task, the file is appended to it", TaskFileAppended);
        t.Add("agent_spawn taskFile: a subagents batch reads its file per entry", TaskFilePerEntry);
        t.Add("agent_spawn taskFile: a missing file refuses the call and starts nothing", TaskFileMissing);
        t.Add("agent_spawn taskFile: a batch with one missing file starts none", TaskFileBatchMissing);
    }

    private static bool IsChild(ModelRequest r) => r.SystemPrompt?.Contains("a subagent working for") == true;

    private static ToolContext ContextOf(TestHost h, AgentInfo parent) =>
        new() { SessionId = parent.SessionId, AgentId = parent.Id, CallId = "c~1", Cwd = h.Workspace, Services = h.Services, Events = h.Bus };

    private static async Task<AgentInfo> ParentAsync(TestHost h)
    {
        var s = h.NewSession();
        await h.SendAsync(s.Id, "hello");
        return await h.IdleAsync(s.Id);
    }

    /// <summary>The caller's tool call: the arguments straight to agent_spawn, with the caller's working directory as Cwd.</summary>
    private static Task<ToolResult> SpawnAsync(TestHost h, AgentInfo parent, object args) =>
        h.Tools.Get("agent_spawn")!.ExecuteAsync(ContextOf(h, parent), JsonSerializer.SerializeToElement(args), CancellationToken.None);

    private static void WriteBrief(TestHost h, string name, string text) => File.WriteAllText(Path.Combine(h.Workspace, name), text);

    private static AgentInfo ChildOf(TestHost h, AgentInfo parent, string name) =>
        h.Runtime.List(true).Single(a => a.ParentAgentId == parent.Id && a.Name == name);

    private static async Task TaskFileAlone()
    {
        await using var h = await TestHost.StartAsync();
        h.Catalog.Handler = (r, ct) => IsChild(r) ? Reply.Text("done") : Reply.Text("ok");
        var parent = await ParentAsync(h);
        WriteBrief(h, "brief.md", "Write the report on the flaky tests.");
        var result = await SpawnAsync(h, parent, new { taskFile = "brief.md" });
        Check.False(result.IsError, result.Content);
        var child = ChildOf(h, parent, "agent-1");
        Check.Equal("Write the report on the flaky tests.", child.Task, "the file's text is the task");
        await Wait.Until(() => h.Runtime.Get(child.Id)?.Status == AgentStatus.Completed, "the child finished");
    }

    private static async Task TaskFileAppended()
    {
        await using var h = await TestHost.StartAsync();
        h.Catalog.Handler = (r, ct) => IsChild(r) ? Reply.Text("done") : Reply.Text("ok");
        var parent = await ParentAsync(h);
        WriteBrief(h, "constraints.md", "Do not touch the storage layer.");
        var result = await SpawnAsync(h, parent, new { task = "Fix the failing build.", taskFile = "constraints.md" });
        Check.False(result.IsError, result.Content);
        var child = ChildOf(h, parent, "agent-1");
        Check.Equal("Fix the failing build.\n\nDo not touch the storage layer.", child.Task, "the task, then the file, on their own lines");
        await Wait.Until(() => h.Runtime.Get(child.Id)?.Status == AgentStatus.Completed, "the child finished");
    }

    private static async Task TaskFilePerEntry()
    {
        await using var h = await TestHost.StartAsync();
        h.Catalog.Handler = (r, ct) => IsChild(r) ? Reply.Text("done") : Reply.Text("ok");
        var parent = await ParentAsync(h);
        WriteBrief(h, "a.md", "First brief.");
        WriteBrief(h, "b.md", "Second brief.");
        var result = await SpawnAsync(h, parent, new
        {
            subagents = new object[]
            {
                new { taskFile = "a.md", name = "alpha" },
                new { task = "Second task.", taskFile = "b.md", name = "beta" },
            },
        });
        Check.False(result.IsError, result.Content);
        var alpha = ChildOf(h, parent, "alpha");
        var beta = ChildOf(h, parent, "beta");
        Check.Equal("First brief.", alpha.Task, "alpha's file alone");
        Check.Equal("Second task.\n\nSecond brief.", beta.Task, "beta's task with its file appended");
        await Wait.Until(() => h.Runtime.Get(alpha.Id)?.Status == AgentStatus.Completed
            && h.Runtime.Get(beta.Id)?.Status == AgentStatus.Completed, "both children finished");
    }

    private static async Task TaskFileMissing()
    {
        await using var h = await TestHost.StartAsync();
        h.Catalog.Handler = (r, ct) => Reply.Text("ok");
        var parent = await ParentAsync(h);
        var result = await SpawnAsync(h, parent, new { taskFile = "no-such-file.md" });
        Check.True(result.IsError);
        Check.Contains(result.Content, "taskFile no-such-file.md can't be read");
        Check.Equal(0, h.Runtime.List(true).Count(a => a.ParentAgentId == parent.Id), "nothing was started");
    }

    private static async Task TaskFileBatchMissing()
    {
        await using var h = await TestHost.StartAsync();
        h.Catalog.Handler = (r, ct) => Reply.Text("ok");
        var parent = await ParentAsync(h);
        WriteBrief(h, "a.md", "First brief.");
        var result = await SpawnAsync(h, parent, new
        {
            subagents = new object[]
            {
                new { taskFile = "a.md", name = "alpha" },
                new { taskFile = "no-such-file.md", name = "beta" },
            },
        });
        Check.True(result.IsError);
        Check.Contains(result.Content, "subagents[1] (beta)");
        Check.Contains(result.Content, "taskFile no-such-file.md can't be read");
        Check.Contains(result.Content, "None of them was started");
        Check.Equal(0, h.Runtime.List(true).Count(a => a.ParentAgentId == parent.Id), "nothing was started");
    }
}
