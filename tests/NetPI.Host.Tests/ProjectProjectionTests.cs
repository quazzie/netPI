using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using NetPI.Abstractions;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 D (slice 1): the project-context PROJECTION — one persisted
/// ProjectContext entry projects into exactly one model-facing user message
/// with a deterministic, entry-id-derived identity, and the active snapshot
/// is reconstructed EXACTLY (survives compaction / reconnect / restart).
/// </summary>
public class ProjectProjectionTests
{
    private static ProjectContextSnapshot Snap(string project, string name, string ws, string instructions) =>
        new(project, name, ws,
            new[] { ws + "\\AGENTS.md" },
            instructions,
            "hash-" + instructions,
            DateTimeOffset.UtcNow);

    private static SessionEntry ProjectEntry(string id, string sessionId, ProjectContextSnapshot snap, int seq)
    {
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(snap)).RootElement.Clone();
        return new SessionEntry(id, sessionId, EntryKind.ProjectContext, null, payload,
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + seq), Sequence: seq);
    }

    [Fact]
    public void Project_Produces_One_ModelFacing_UserMessage()
    {
        var entry = ProjectEntry("op-1", "s1", Snap("p1", "NetPI", @"C:\AI\Projects\NetPI", "EXACT AGENTS CONTENT"), 1);
        var msg = ProjectContextProjection.Project(entry);

        Assert.NotNull(msg);
        Assert.Equal(MessageRole.User, msg!.Role);
        Assert.Single(msg.Parts);
        var text = ((TextPart)msg.Parts[0]).Text;
        Assert.Contains("Project changed to NetPI.", text);
        Assert.Contains(@"Workspace: C:\AI\Projects\NetPI", text);
        Assert.Contains("EXACT AGENTS CONTENT", text);
        Assert.Contains("<project_instructions>", text);
        Assert.Contains("supersede", text);
    }

    [Fact]
    public void Projection_Id_Is_Derived_From_EntryId_Not_Content()
    {
        // Same entry id, same content → same message id.
        var a = ProjectContextProjection.Project(ProjectEntry("op-1", "s1", Snap("p", "N", "w", "X"), 1))!;
        var b = ProjectContextProjection.Project(ProjectEntry("op-1", "s2", Snap("p", "N", "w", "X"), 1))!;
        Assert.Equal(a.Id, b.Id);

        // Different entry id, same content → different message id.
        var c = ProjectContextProjection.Project(ProjectEntry("op-2", "s1", Snap("p", "N", "w", "X"), 2))!;
        Assert.NotEqual(a.Id, c.Id);

        // Same entry id, different content → same message id (id is entry-derived).
        var d = ProjectContextProjection.Project(ProjectEntry("op-1", "s1", Snap("p", "N", "w", "DIFFERENT"), 1))!;
        Assert.Equal(a.Id, d.Id);
    }

    [Fact]
    public void Projection_Is_Idempotent_Exact_Text_Not_Paraphrase()
    {
        var entry = ProjectEntry("op-1", "s1", Snap("p1", "NetPI", @"C:\X", "LINE ONE\nLINE TWO"), 1);
        var first = ProjectContextProjection.Project(entry)!;
        var second = ProjectContextProjection.Project(entry)!;

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(
            ((TextPart)first.Parts[0]).Text,
            ((TextPart)second.Parts[0]).Text);
        var text = ((TextPart)first.Parts[0]).Text;
        Assert.Contains("LINE ONE", text);
        Assert.Contains("LINE TWO", text);
    }

    [Fact]
    public void Snapshot_RoundTrips_Through_Payload()
    {
        var snap = Snap("p1", "NetPI", @"C:\X", "INSTRUCTIONS");
        var entry = ProjectEntry("op-1", "s1", snap, 1);
        var back = ProjectContextProjection.Snapshot(entry);

        Assert.NotNull(back);
        Assert.Equal("p1", back!.ProjectId);
        Assert.Equal("NetPI", back.ProjectName);
        Assert.Equal(@"C:\X", back.WorkspacePath);
        Assert.Equal("INSTRUCTIONS", back.EffectiveInstructions);
        Assert.Equal("hash-INSTRUCTIONS", back.ContentHash);
    }

    [Fact]
    public void ProjectOf_NonProjectEntry_Is_Null()
    {
        var entry = new SessionEntry("m-1", "s1", EntryKind.Message,
            new AgentMessage("id", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow),
            null, DateTimeOffset.UtcNow, 1);
        Assert.Null(ProjectContextProjection.Project(entry));
    }

    [Fact]
    public void ActiveProjectContext_Is_MostRecent_BySequence()
    {
        // Fresh session: A active, then B active — the newest (by sequence) wins.
        var db = Path.Combine(Path.GetTempPath(), "netpi-projD-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(db);
        try
        {
            using var sessions = new SqliteSessionStore(Path.Combine(db, "x.db"));
            var projects = new SqliteProjectStore(Path.Combine(db, "x.db"));
            var session = sessions.CreateAsync(null).GetAwaiter().GetResult();
            var a = projects.CreateAsync("A", @"C:\A").GetAwaiter().GetResult();
            var b = projects.CreateAsync("B", @"C:\B").GetAwaiter().GetResult();

            var snapA = new ProjectContextSnapshot(a.Id, "A", a.WorkspacePath,
                new[] { a.WorkspacePath + "\\AGENTS.md" }, "INSTRUCTIONS A", "hashA", DateTimeOffset.UtcNow);
            var snapB = new ProjectContextSnapshot(b.Id, "B", b.WorkspacePath,
                new[] { b.WorkspacePath + "\\AGENTS.md" }, "INSTRUCTIONS B", "hashB", DateTimeOffset.UtcNow);

            sessions.SetProjectAsync(new ProjectChangeRequest("op-A", session.Id, a.Id, snapA)).GetAwaiter().GetResult();
            sessions.SetProjectAsync(new ProjectChangeRequest("op-B", session.Id, b.Id, snapB)).GetAwaiter().GetResult();

            var active = sessions.ActiveProjectContextAsync(session.Id).GetAwaiter().GetResult();
            Assert.NotNull(active);
            Assert.Equal(b.Id, ProjectContextProjection.Snapshot(active!)!.ProjectId);
            Assert.Equal("INSTRUCTIONS B", ProjectContextProjection.Snapshot(active)!.EffectiveInstructions);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(db, true); }
    }

    [Fact]
    public void ActiveProjectContext_Is_Null_When_NeverAttached()
    {
        var db = Path.Combine(Path.GetTempPath(), "netpi-projD-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(db);
        try
        {
            using var sessions = new SqliteSessionStore(Path.Combine(db, "x.db"));
            var session = sessions.CreateAsync(null).GetAwaiter().GetResult();
            Assert.Null(sessions.ActiveProjectContextAsync(session.Id).GetAwaiter().GetResult());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(db, true); }
    }

    [Fact]
    public void ActiveSnapshot_Survives_A_Compaction_Entry()
    {
        // The active project-context entry is NOT a Message, so a compaction
        // tail (which only carries messages) cannot carry it — it must be
        // reconstructed EXACTLY from the store.
        var db = Path.Combine(Path.GetTempPath(), "netpi-projD-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(db);
        try
        {
            using var sessions = new SqliteSessionStore(Path.Combine(db, "x.db"));
            var projects = new SqliteProjectStore(Path.Combine(db, "x.db"));
            var session = sessions.CreateAsync(null).GetAwaiter().GetResult();
            var proj = projects.CreateAsync("NetPI", @"C:\AI\Projects\NetPI").GetAwaiter().GetResult();

            var snap = new ProjectContextSnapshot(proj.Id, "NetPI", proj.WorkspacePath,
                new[] { proj.WorkspacePath + "\\AGENTS.md" }, "EXACT INSTRUCTIONS", "hashEXACT", DateTimeOffset.UtcNow);
            sessions.SetProjectAsync(new ProjectChangeRequest("op-1", session.Id, proj.Id, snap)).GetAwaiter().GetResult();

            // A compaction entry lands after it (the tail boundary moves past it).
            var compaction = new SessionEntry(
                "comp-1", session.Id, EntryKind.Compaction, null,
                JsonDocument.Parse(
                    $"{{\"summary\":\"old stuff\",\"summarizedThroughSequence\":2,\"retainedFromSequence\":3,\"estimatedTokensBefore\":9,\"estimatedTokensAfter\":1}}").RootElement.Clone(),
                DateTimeOffset.UtcNow,
                Sequence: 2);
            sessions.AppendAsync(compaction).GetAwaiter().GetResult();

            // Reconstructed active snapshot is byte-identical (exact, not paraphrased).
            var active = sessions.ActiveProjectContextAsync(session.Id).GetAwaiter().GetResult();
            Assert.NotNull(active);
            var back = ProjectContextProjection.Snapshot(active!)!;
            Assert.Equal("EXACT INSTRUCTIONS", back.EffectiveInstructions);
            Assert.Equal("hashEXACT", back.ContentHash);
            var projected = ProjectContextProjection.Project(active)!;
            Assert.Contains("EXACT INSTRUCTIONS", ((TextPart)projected.Parts[0]).Text);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(db, true); }
    }
}
