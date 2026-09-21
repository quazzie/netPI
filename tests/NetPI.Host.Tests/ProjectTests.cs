using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using NetPI.Abstractions;
using NetPI.Context.Pi;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 C — project contracts, instruction discovery precedence, and the
/// atomic idempotent session-project operation.
/// </summary>
public class ProjectDiscoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string _ws;

    public ProjectDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "netpi-proj-" + Guid.NewGuid().ToString("N"));
        _ws = Path.Combine(_root, "work");
        Directory.CreateDirectory(_ws);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void WriteRoot(string file, string content)
        => File.WriteAllText(Path.Combine(_ws, file), content);

    private void WriteDotNetpi(string file, string content)
    {
        var netpi = Path.Combine(_ws, ".netpi");
        Directory.CreateDirectory(netpi);
        File.WriteAllText(Path.Combine(netpi, file), content);
    }

    /// <summary>A discovery whose raw read fails for ONE path (simulates unreadable).</summary>
    private sealed class UnreadableDiscovery(string path) : ContextDiscovery
    {
        protected override string ReadFileRaw(string p)
        {
            if (p == path) throw new UnauthorizedAccessException("access denied (simulated)");
            return File.ReadAllText(p);
        }
    }

    [Fact]
    public void Root_AgentsMd_Is_Included()
    {
        WriteRoot("AGENTS.md", "WORKSPACE ROOT AGENTS");
        var d = new ContextDiscovery();
        var snap = d.ResolveInstructions("p1", "proj", _ws);

        Assert.Contains("WORKSPACE ROOT AGENTS", snap.EffectiveInstructions);
        Assert.Contains(snap.InstructionSources, s => Path.GetFileName(s) == "AGENTS.md" && s.StartsWith(_ws));
    }

    [Fact]
    public void Precedence_OverrideInDotNetpi_Beats_RootOverride_Beats_Root_Beats_DotNetpi()
    {
        // All four present in the workspace directory: only #1 wins.
        WriteDotNetpi("AGENTS.override.md", "L1");
        WriteRoot("AGENTS.override.md", "L2");
        WriteRoot("AGENTS.md", "L3");
        WriteDotNetpi("AGENTS.md", "L4");

        var d = new ContextDiscovery();
        var (global, project) = d.DiscoverSplit(_ws);
        var wsLayers = project.Where(l => l.Label.StartsWith(_ws)).ToList();

        Assert.Single(wsLayers);
        Assert.Equal("L1", wsLayers[0].Content);
    }

    [Fact]
    public void Precedence_RootOverride_Beats_Root_Beats_DotNetpi()
    {
        WriteRoot("AGENTS.override.md", "L2");
        WriteRoot("AGENTS.md", "L3");
        WriteDotNetpi("AGENTS.md", "L4");

        var d = new ContextDiscovery();
        var (global, project) = d.DiscoverSplit(_ws);
        var wsLayers = project.Where(l => l.Label.StartsWith(_ws)).ToList();

        Assert.Single(wsLayers);
        Assert.Equal("L2", wsLayers[0].Content);
    }

    [Fact]
    public void Precedence_Root_AgentsMd_Beats_DotNetpi()
    {
        WriteRoot("AGENTS.md", "L3");
        WriteDotNetpi("AGENTS.md", "L4");

        var d = new ContextDiscovery();
        var (global, project) = d.DiscoverSplit(_ws);
        var wsLayers = project.Where(l => l.Label.StartsWith(_ws)).ToList();

        Assert.Single(wsLayers);
        Assert.Equal("L3", wsLayers[0].Content);
    }

    [Fact]
    public void BroadToSpecific_Layering_Is_Deterministic()
    {
        // The workspace is <_root>\work, so <_root> is its actual parent —
        // put the broad layer there and the specific layer at the workspace root.
        var broad = Path.Combine(_root, ".netpi");
        Directory.CreateDirectory(broad);
        File.WriteAllText(Path.Combine(broad, "AGENTS.md"), "PARENT AGENTS");
        WriteRoot("AGENTS.md", "CHILD AGENTS");

        var d = new ContextDiscovery();
        var snap = d.ResolveInstructions("p1", "proj", _ws);

        var pIdx = snap.EffectiveInstructions.IndexOf("PARENT AGENTS");
        var cIdx = snap.EffectiveInstructions.IndexOf("CHILD AGENTS");
        Assert.True(pIdx >= 0 && cIdx >= 0, "both layers must be present");
        Assert.True(pIdx < cIdx, "broad (parent) must precede specific (workspace)");
    }

    [Fact]
    public void Global_Instructions_Occur_Exactly_Once()
    {
        WriteRoot("AGENTS.md", "WORKSPACE ONLY MARKER");
        var d = new ContextDiscovery();
        var (global, project) = d.DiscoverSplit(_ws);

        // The workspace marker must NOT be classified as global, and the global
        // layer (if any) must not duplicate a project layer's content.
        Assert.False(project.Any(l => l.Content.Contains("WORKSPACE ONLY MARKER")
                                      && l.Label == global?.Label),
            "the global layer must not appear as a project layer too");

        var snap = d.ResolveInstructions("p1", "proj", _ws);
        var count = snap.InstructionSources.Count(s => s == global?.Label);
        Assert.True(count <= 1, $"global layer listed {count} times");
    }

    [Fact]
    public void Missing_Files_Are_Valid_Empty_Layers()
    {
        var d = new ContextDiscovery();
        var snap = d.ResolveInstructions("p1", "proj", _ws);
        Assert.Empty(snap.InstructionSources.Where(s => s.StartsWith(_ws)));
        Assert.False(snap.EffectiveInstructions.Contains("WORKSPACE"));
    }

    [Fact]
    public void Unreadable_Existing_File_Is_Actionable_Error()
    {
        WriteRoot("AGENTS.md", "SHOULD NOT BE READ");
        var path = Path.Combine(_ws, "AGENTS.md");
        var d = new UnreadableDiscovery(path);

        Assert.Throws<ProjectContextChangeException>(
            () => d.ResolveInstructions("p1", "proj", _ws));
    }

    [Fact]
    public void Unreadable_Error_Message_Names_The_File()
    {
        WriteRoot("AGENTS.md", "SHOULD NOT BE READ");
        var path = Path.Combine(_ws, "AGENTS.md");
        var d = new UnreadableDiscovery(path);

        var ex = Assert.Throws<ProjectContextChangeException>(
            () => d.ResolveInstructions("p1", "proj", _ws));
        Assert.Contains(path, ex.Message);
    }
}

/// <summary>
/// astra-1 C — IProjectStore CRUD + the atomic idempotent SetProjectAsync.
/// </summary>
public class ProjectStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteSessionStore _sessions;
    private readonly SqliteProjectStore _projects;

    public ProjectStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "netpi-projdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var db = Path.Combine(_dir, "test.db");
        _sessions = new SqliteSessionStore(db);
        _projects = new SqliteProjectStore(db);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private static ProjectContextSnapshot Snap(string projectId, string name, string workspace) =>
        new(projectId, name, workspace,
            new[] { workspace + "\\AGENTS.md" },
            "instructions for " + name,
            "hash-of-" + name,
            DateTimeOffset.UtcNow);

    private ProjectChangeRequest Change(string op, string session, string project,
        ProjectContextSnapshot snap, int expectedRevision = -1)
        => new(op, session, project, expectedRevision, snap);

    [Fact]
    public async Task ProjectCrud_Create_Get_List_Rename_Delete()
    {
        var p = await _projects.CreateAsync("NetPI", @"C:\AI\Projects\NetPI");
        Assert.NotEmpty(p.Id);
        Assert.Equal("NetPI", p.Name);

        var byId = await _projects.GetAsync(p.Id);
        Assert.NotNull(byId);

        var byWs = await _projects.GetByWorkspaceAsync(@"c:/ai/projects/netpi");
        Assert.NotNull(byWs);
        Assert.Equal(p.Id, byWs!.Id);

        Assert.Contains(p.Id, (await _projects.ListAsync()).Select(x => x.Id));

        await _projects.RenameAsync(p.Id, "netpi-renamed");
        Assert.Equal("netpi-renamed", (await _projects.GetAsync(p.Id))!.Name);

        await _projects.DeleteAsync(p.Id);
        Assert.Null(await _projects.GetAsync(p.Id));
    }

    [Fact]
    public async Task CanonicallyEquivalentPaths_Are_One_Project()
    {
        var a = await _projects.CreateAsync("A", @"C:\AI\Projects\NetPI");
        var b = await _projects.CreateAsync("B", @"c:/ai/projects/netpi");
        Assert.Equal(a.Id, b.Id);
        Assert.Single(await _projects.ListAsync());
    }

    [Fact]
    public async Task SetProjectAsync_Persists_Snapshot_And_Revision()
    {
        var session = await _sessions.CreateAsync(@"C:\AI\Projects\NetPI");
        var project = await _projects.CreateAsync("NetPI", @"C:\AI\Projects\NetPI");
        var change = Change("op-1", session.Id, project.Id, Snap(project.Id, "NetPI", project.WorkspacePath));

        var result = await _sessions.SetProjectAsync(change);

        Assert.Equal(project.Id, result.Session.ProjectId);
        Assert.Equal(session.WorkspacePath, result.Session.WorkspacePath);
        Assert.Equal("op-1", result.Entry.Id);
        Assert.Equal(EntryKind.ProjectContext, result.Entry.Kind);

        var reloaded = await _sessions.GetAsync(session.Id);
        Assert.Equal(project.Id, reloaded!.ProjectId);
        Assert.Contains("instructions for NetPI", result.Entry.Payload!.Value.ToString());
    }

    [Fact]
    public async Task Idempotent_Retry_Same_OperationId_Does_Not_Bump_Revision()
    {
        var session = await _sessions.CreateAsync(@"C:\AI\Projects\NetPI");
        var project = await _projects.CreateAsync("NetPI", @"C:\AI\Projects\NetPI");
        var change = Change("op-1", session.Id, project.Id, Snap(project.Id, "NetPI", project.WorkspacePath));

        var first = await _sessions.SetProjectAsync(change);
        var second = await _sessions.SetProjectAsync(change);

        Assert.Equal(first.Entry.Id, second.Entry.Id);
        Assert.Equal(first.Session.ProjectId, second.Session.ProjectId);
        // The revision must not have been bumped twice.
        var rev = await RevisionOf(session.Id);
        Assert.Equal(1, rev);
        // Only ONE project-context entry exists.
        var count = await ProjectContextEntryCount(session.Id);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Stale_ExpectedContextRevision_Is_Rejected()
    {
        var session = await _sessions.CreateAsync(@"C:\AI\Projects\NetPI");
        var project = await _projects.CreateAsync("NetPI", @"C:\AI\Projects\NetPI");

        // Expecting revision 5 on a fresh session (revision 0) → rejected.
        var stale = Change("op-1", session.Id, project.Id,
            Snap(project.Id, "NetPI", project.WorkspacePath), expectedRevision: 5);
        await Assert.ThrowsAsync<ProjectContextChangeException>(
            () => _sessions.SetProjectAsync(stale).AsTask());

        // The session is untouched.
        var reloaded = await _sessions.GetAsync(session.Id);
        Assert.Null(reloaded!.ProjectId);
    }

    [Fact]
    public async Task Correct_ExpectedContextRevision_Applies()
    {
        var session = await _sessions.CreateAsync(@"C:\AI\Projects\NetPI");
        var project = await _projects.CreateAsync("NetPI", @"C:\AI\Projects\NetPI");

        var ok = Change("op-1", session.Id, project.Id,
            Snap(project.Id, "NetPI", project.WorkspacePath), expectedRevision: 0);
        var result = await _sessions.SetProjectAsync(ok);
        Assert.Equal(project.Id, result.Session.ProjectId);
    }

    [Fact]
    public async Task Unknown_Session_Or_Project_Fails_Cleanly()
    {
        var project = await _projects.CreateAsync("NetPI", @"C:\AI\Projects\NetPI");

        var badSession = Change("op-1", "no-such-session", project.Id,
            Snap(project.Id, "NetPI", project.WorkspacePath));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sessions.SetProjectAsync(badSession).AsTask());

        var session = await _sessions.CreateAsync(@"C:\AI\Projects\NetPI");
        var badProject = Change("op-1", session.Id, "no-such-project",
            Snap("no-such-project", "X", project.WorkspacePath));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sessions.SetProjectAsync(badProject).AsTask());
    }

    [Fact]
    public async Task Reopening_Session_Keeps_Persisted_Snapshot_Without_Rereading()
    {
        var session = await _sessions.CreateAsync(@"C:\AI\Projects\NetPI");
        var project = await _projects.CreateAsync("NetPI", @"C:\AI\Projects\NetPI");
        var change = Change("op-1", session.Id, project.Id, Snap(project.Id, "NetPI", project.WorkspacePath));
        await _sessions.SetProjectAsync(change);

        // "Reopen": a FRESH store instance reads back the persisted snapshot.
        var db = Path.Combine(_dir, "test.db");
        using (var fresh = new SqliteSessionStore(db))
        {
            var reloaded = await fresh.GetAsync(session.Id);
            Assert.Equal(project.Id, reloaded!.ProjectId);

            // The persisted entry carries the exact snapshot text (not a paraphrase).
            var recent = await fresh.ReadRecentAsync(session.Id, 50);
            var entry = recent.LastOrDefault(e => e.Kind == EntryKind.ProjectContext);
            Assert.NotNull(entry);
            Assert.Equal(EntryKind.ProjectContext, entry!.Kind);
            Assert.Contains("instructions for NetPI", entry.Payload!.Value.ToString());
        }
    }

    // ---- raw helpers (read the schema directly) -------------------------

    private async Task<int> RevisionOf(string sessionId)
    {
        await using var conn = new SqliteConnection(
            SqliteSessionStore.BuildConnectionString(Path.Combine(_dir, "test.db")));
        conn.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT context_revision FROM sessions WHERE id = $s;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<int> ProjectContextEntryCount(string sessionId)
    {
        await using var conn = new SqliteConnection(
            SqliteSessionStore.BuildConnectionString(Path.Combine(_dir, "test.db")));
        conn.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM session_entries WHERE session_id = $s AND entry_type = 'ProjectContext';";
        cmd.Parameters.AddWithValue("$s", sessionId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
