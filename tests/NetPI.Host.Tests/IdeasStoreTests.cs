using System.Text.Json;
using NetPI.Ideas;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// The per-project ideas bank: CRUD on ideas.json, find-by-id-or-title,
/// corrupt-file safety, and the pure text builders behind the panel actions.
/// </summary>
public sealed class IdeasStoreTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netpi-ideas-test-" + Guid.NewGuid().ToString("N"));

    public IdeasStoreTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private IdeasStore Store() => new(_dir);

    [Fact]
    public void Add_CreatesFile_Version1_WithRecord()
    {
        var rec = Store().Add("Auto-bind pools", body: "new models escape lanes", tags: ["lanes", "pools"]);
        Assert.False(string.IsNullOrEmpty(rec.Id));
        Assert.Equal("idea", rec.Status);
        Assert.Equal(new[] { "lanes", "pools" }, rec.Tags);

        var raw = File.ReadAllText(System.IO.Path.Combine(_dir, "ideas.json"));
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("ideas").GetArrayLength());
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty_NotAnError()
    {
        Assert.Empty(Store().Load());
    }

    [Fact]
    public void Find_ByTitleFragment_ReturnsMostRecentlyUpdated()
    {
        Store().Add("Auto-bind pools");
        var newer = Store().Add("Auto-bind pools for cloud models");
        var found = Store().Find("auto-bind");
        Assert.NotNull(found);
        Assert.Equal(newer.Id, found!.Id);
        Assert.Equal(newer.Id, Store().Find(newer.Id)!.Id);
    }

    [Fact]
    public void Find_Unknown_ReturnsNull()
    {
        Store().Add("something");
        Assert.Null(Store().Find("nothing at all"));
    }

    [Fact]
    public void Update_PatchesOnlyGivenFields_AndBumpsUpdatedAt()
    {
        var rec = Store().Add("t", body: "b");
        Thread.Sleep(15); // distinct tick
        var updated = Store().Update(rec.Id, notes: "discussed", status: "planned");
        Assert.NotNull(updated);
        Assert.Equal("discussed", updated!.Notes);
        Assert.Equal("planned", updated.Status);
        Assert.Equal("b", updated.Body);            // untouched
        Assert.True(updated.UpdatedAt > rec.UpdatedAt);
        Assert.Null(Store().Update("missing idea"));
    }

    [Fact]
    public void Update_Tags_ExplicitArrayReplaces()
    {
        var rec = Store().Add("t", tags: ["a", "b"]);
        var updated = Store().Update(rec.Id, tags: ["c"]);
        Assert.Equal(new[] { "c" }, updated!.Tags);
    }

    [Fact]
    public void Remove_DeletesAndReports()
    {
        var rec = Store().Add("t");
        Assert.True(Store().Remove(rec.Id));
        Assert.False(Store().Remove(rec.Id));
        Assert.Empty(Store().Load());
    }

    [Fact]
    public void CorruptFile_Throws_NeverSilentlyRewritten()
    {
        File.WriteAllText(System.IO.Path.Combine(_dir, "ideas.json"), "{ not json ");
        Assert.Throws<IdeasStoreException>(() => Store().Load());
        // the corrupt file must survive untouched (fail-closed)
        Assert.Equal("{ not json ", File.ReadAllText(System.IO.Path.Combine(_dir, "ideas.json")));
    }

    [Fact]
    public void NonExistentWorkspace_Throws()
    {
        var bad = System.IO.Path.Combine(_dir, "nope");
        Assert.Throws<IdeasStoreException>(() => new IdeasStore(bad));
    }

    [Fact]
    public void Add_RequiresTitle()
    {
        Assert.Throws<IdeasStoreException>(() => Store().Add("   "));
    }

    // ---- panel text builders -------------------------------------------------

    [Fact]
    public void StartMessage_ContainsIdeaAndInstructions()
    {
        var rec = new IdeaRecord { Id = "abcd1234", Title = "Auto-bind pools", Body = "b", Notes = "n", Plan = "p" };
        var msg = IdeasWebApp.BuildStartMessage(rec);
        Assert.Contains("Auto-bind pools", msg);
        Assert.Contains("abcd1234", msg);
        Assert.Contains("ideas.json", msg);
        Assert.Contains("in-progress", msg);
        Assert.Contains("Let's work on the idea", msg);
    }

    [Fact]
    public void StartMessage_OmitsEmptySections()
    {
        var rec = new IdeaRecord { Id = "x", Title = "T" };
        var msg = IdeasWebApp.BuildStartMessage(rec);
        Assert.DoesNotContain("Notes:", msg);
        Assert.DoesNotContain("Plan:", msg);
    }

    [Fact]
    public void PrefillText_TitleFirst_ThenExistingSections()
    {
        var rec = new IdeaRecord { Id = "i1", Title = "T", Status = "idea", Body = "b", Notes = "n", Plan = "p" };
        var text = IdeasWebApp.BuildPrefillText(rec);
        Assert.StartsWith("Idea: T (id i1 · idea)", text);
        Assert.Contains("b", text);
        Assert.Contains("Notes: n", text);
        Assert.Contains("Plan: p", text);
    }
}
