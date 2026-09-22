using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Agent;
using NetPI.Orchestration;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §8/§15.C: workspace modes.
///   * shared-read enforces a READ-ONLY tool policy at the runtime: mutating
///     tools are withheld from the model's tool list AND rejected at preflight
///     (a generic shell is not read-only enforcement — it is withheld too).
///   * isolated-worktree provisions an independent git worktree of the parent
///     workspace's BASE revision (branch codex/…) whose path is recorded on the
///     child's session + assignment; the parent's dirty worktree is never touched
///     and no worktree is auto-removed (kept until reviewed).
///   * shared modes pass the parent's workspace path through (the run executes
///     in the parent's working tree, not the host CWD).
///   * continue keeps the agent's recorded workspace mode + path.
/// </summary>
public sealed class WorkspaceModeTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        // Clear pooled SQLite connections (they keep the temp db locked),
        // remove any provisioned worktrees (they live under the repo and hold a
        // .git pointer), then the temp dir.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in _dirs)
        {
            try
            {
                foreach (var wt in Directory.EnumerateDirectories(d, "codex-*",
                    SearchOption.AllDirectories))
                    Directory.Delete(wt, recursive: true);
                Directory.Delete(d, recursive: true);
            }
            catch (Exception) { /* best-effort: the OS temp path GCs leftovers */ }
        }
    }

    private string NewDir()
    {
        var d = Directory.CreateTempSubdirectory("netpi-ws-").FullName;
        _dirs.Add(d);
        return d;
    }

    // ---- runtime shared-read enforcement ------------------------------------

    [Fact]
    public async Task SharedRead_MutatingToolsWithheldFromModelAndRejectedAtPreflight()
    {
        // Register a real mutating tool ("bash") and a non-mutating one ("grep")
        // so the test asserts on the MODEL's tool list, not just preflight.
        var bash = new NamedTool("bash");
        var grep = new NamedTool("grep");
        var registry = new TestRegistry(bash, grep);

        // Turn 1: model calls "bash" → must be rejected at preflight.
        // Turn 2: model finishes.
        var provider = new FakeProvider(
            [new ModelStarted("model"), new ModelCompleted(Assistant(
                new ToolCallPart("t1", "bash",
                    JsonSerializer.SerializeToElement(new { command = "ls" }))))],
            [new ModelCompleted(Assistant(new TextPart("done")))]);

        var ctx = new NoopPluginContext();
        ctx.Add("provider", provider);
        ctx.Add("tools", registry);
        var rt = new AgentRuntime(ctx);

        var opts = new AgentRunOptions
        {
            SessionId = "s1",
            ModelId = "model",
            WorkspaceMode = "shared-read",   // the mode the runtime enforces
            Messages = [new AgentMessage("u1", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow)],
        };

        var result = await rt.RunAsync(opts, CancellationToken.None);

        Assert.True(result.Ok);
        // (a) the model was OFFERED only non-mutating tools — "bash" is withheld.
        var offered = provider.LastTools!;
        Assert.Contains(offered, t => t.Name == "grep");
        Assert.DoesNotContain(offered, t => t.Name == "bash");
        // (b) the withheld "bash" call was REJECTED at preflight, not executed.
        Assert.False(bash.Executed);
        Assert.Equal(2, result.Turns); // tool round-trip still completes as an error
    }

    [Fact]
    public async Task Unrestricted_MutatingToolsAvailableAndExecuted()
    {
        // No workspace mode (ad-hoc run): mutating tools are available and run.
        var bash = new NamedTool("bash");
        var registry = new TestRegistry(bash, new NamedTool("grep"));
        var provider = new FakeProvider(
            [new ModelStarted("model"), new ModelCompleted(Assistant(
                new ToolCallPart("t1", "bash",
                    JsonSerializer.SerializeToElement(new { command = "ls" }))))],
            [new ModelCompleted(Assistant(new TextPart("done")))]);

        var ctx = new NoopPluginContext();
        ctx.Add("provider", provider);
        ctx.Add("tools", registry);
        var rt = new AgentRuntime(ctx);

        var opts = new AgentRunOptions
        {
            SessionId = "s1",
            ModelId = "model",
            // WorkspaceMode left null → unrestricted.
            Messages = [new AgentMessage("u1", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow)],
        };

        var result = await rt.RunAsync(opts, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(bash.Executed);           // mutating tool ran
        Assert.Contains(provider.LastTools!, t => t.Name == "bash");
    }

    // ---- store round-trip: mode + path persist --------------------------------

    [Fact]
    public async Task Store_RoundTripsWorkspaceModeAndPath()
    {
        var dir = NewDir();
        var db = Path.Combine(dir, "test.db");
        var sessions = new SqliteSessionStore(db);
        var store = new SqliteOrchestrationStore(db);

        var sess = await sessions.CreateAsync("/tmp/my-ws");
        var root = await store.EnsureRootAgentAsync(sess.Id, null, "agent");

        await store.CreateAssignmentAsync("op-1", root.AgentId, sess.Id, null, null,
            "model", null, null, "assignment", null,
            workspaceMode: "shared-write", workspacePath: "/tmp/my-ws");

        var row = (await store.GetNonterminalAsync(sess.Id))!;
        Assert.Equal("shared-write", row.WorkspaceMode);
        Assert.Equal("/tmp/my-ws", row.WorkspacePath);
    }

    // ---- orchestrator: shared mode passes the parent's workspace through -----

    [Fact]
    public async Task Spawn_SharedRead_RunsInParentWorkspace_NotHostCwd()
    {
        var dir = NewDir();
        var db = Path.Combine(dir, "test.db");
        var sessions = new SqliteSessionStore(db);
        var store = new SqliteOrchestrationStore(db);

        // A real git repo as the parent workspace.
        var ws = InitGitRepo(Path.Combine(dir, "repo"));

        // Parent session lives in that repo.
        var parentSess = await sessions.CreateAsync(ws);

        // Wire the orchestrator against a context that has sessions + store,
        // but NO runner (so the spawn persists without starting a run).
        var ctx = new NoopPluginContext();
        ctx.Add("sessions", sessions);
        ctx.Add("orchestration-store", store);
        var orch = new AgentOrchestrator(ctx, store);

        var root = await store.EnsureRootAgentAsync(parentSess.Id, null, "parent");

        var result = await orch.SpawnChildAsync(root.AgentId,
            new AgentSpawnRequest { Brief = "investigate", OperationId = "op-shared",
                WorkspaceMode = "shared-read" },
            CancellationToken.None);

        Assert.Equal(AgentAssignmentLifecycle.Queued, result.Status);
        // The child runs in the PARENT's workspace (shared semantics), not the
        // host CWD — recorded on the spawn result.
        Assert.Equal(ws, result.WorkspacePath);

        // And it is durably recorded on the child's assignment.
        var childRow = (await store.GetNonterminalAsync(result.SessionId))!;
        Assert.Equal("shared-read", childRow.WorkspaceMode);
        Assert.Equal(ws, childRow.WorkspacePath);
    }

    // ---- orchestrator: isolated-worktree provisions a real git worktree ------

    [Fact]
    public async Task Spawn_IsolatedWorktree_ProvisionsIndependentWorktree()
    {
        var dir = NewDir();
        var db = Path.Combine(dir, "test.db");
        var sessions = new SqliteSessionStore(db);
        var store = new SqliteOrchestrationStore(db);

        var ws = InitGitRepo(Path.Combine(dir, "repo"));
        // A dirty (uncommitted) file that must NOT leak into the worktree.
        File.WriteAllText(Path.Combine(ws, "dirty.txt"), "uncommitted");
        var parentSess = await sessions.CreateAsync(ws);

        var ctx = new NoopPluginContext();
        ctx.Add("sessions", sessions);
        ctx.Add("orchestration-store", store);
        var orch = new AgentOrchestrator(ctx, store);

        var root = await store.EnsureRootAgentAsync(parentSess.Id, null, "parent");

        var opId = "op-iso-" + Guid.NewGuid().ToString("N")[..8];
        var result = await orch.SpawnChildAsync(root.AgentId,
            new AgentSpawnRequest { Brief = "change code", OperationId = opId,
                WorkspaceMode = "isolated-worktree" },
            CancellationToken.None);

        Assert.Equal(AgentAssignmentLifecycle.Queued, result.Status);
        var wt = result.WorkspacePath!;
        Assert.True(Directory.Exists(wt), $"worktree missing: {wt}");
        // It lives under <parent>/.netpi/worktrees/ and on a codex/ branch.
        Assert.StartsWith(Path.Combine(ws, ".netpi", "worktrees"), wt, StringComparison.Ordinal);
        // Independent: the parent's dirty file is NOT copied in, but the base is.
        Assert.True(File.Exists(Path.Combine(wt, "a.txt")));   // base revision committed
        Assert.False(File.Exists(Path.Combine(wt, "dirty.txt"))); // uncommitted change excluded
        // The parent's dirty worktree is untouched.
        Assert.True(File.Exists(Path.Combine(ws, "dirty.txt")));

        // Recorded on the assignment.
        var childRow = (await store.GetNonterminalAsync(result.SessionId))!;
        Assert.Equal("isolated-worktree", childRow.WorkspaceMode);
        Assert.Equal(wt, childRow.WorkspacePath);
    }

    [Fact]
    public async Task Spawn_IsolatedWorktree_NonGitParent_FailsGracefully()
    {
        var dir = NewDir();
        var db = Path.Combine(dir, "test.db");
        var sessions = new SqliteSessionStore(db);
        var store = new SqliteOrchestrationStore(db);

        // A non-Git workspace: §8 says do NOT assume Git exists.
        var plainWs = Path.Combine(dir, "plain");
        Directory.CreateDirectory(plainWs);
        var parentSess = await sessions.CreateAsync(plainWs);

        var ctx = new NoopPluginContext();
        ctx.Add("sessions", sessions);
        ctx.Add("orchestration-store", store);
        var orch = new AgentOrchestrator(ctx, store);

        var root = await store.EnsureRootAgentAsync(parentSess.Id, null, "parent");

        var result = await orch.SpawnChildAsync(root.AgentId,
            new AgentSpawnRequest { Brief = "change", OperationId = "op-nongit",
                WorkspaceMode = "isolated-worktree" },
            CancellationToken.None);

        // Fails with an actionable reason, not an exception, and no worktree path.
        Assert.Equal(AgentAssignmentLifecycle.Failed, result.Status);
        Assert.Null(result.WorkspacePath);
        Assert.Contains("could not be provisioned", result.Reason);
    }


    // ---- helpers -------------------------------------------------------------

    private static AgentMessage Assistant(params MessagePart[] parts)
        => new("a1", MessageRole.Assistant, parts, DateTimeOffset.UtcNow);

    private static string InitGitRepo(string path)
    {
        Directory.CreateDirectory(path);
        Run(path, "git", "init -q");
        Run(path, "git", "config user.email t@t.t");
        Run(path, "git", "config user.name T");
        File.WriteAllText(Path.Combine(path, "a.txt"), "base\n");
        Run(path, "git", "add a.txt");
        Run(path, "git", "commit -qm base");
        return path;
    }

    private static void Run(string cwd, string file, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEnd();
        _ = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{file} {args} exited {p.ExitCode}");
    }

    /// <summary>A minimal tool whose execution is recorded, by name.</summary>
    private sealed class NamedTool(string name) : IAgentTool
    {
        public bool Executed;
        public string Name => name;
        public string Description => name;
        public IReadOnlyList<string> Guidelines => [];
        public JsonElement Parameters => JsonSerializer.SerializeToElement(new { type = "object" });
        public ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct)
        {
            Executed = true;
            return ValueTask.FromResult(new ToolResult("t1", name, [new TextPart("ok")], false));
        }
    }
}
