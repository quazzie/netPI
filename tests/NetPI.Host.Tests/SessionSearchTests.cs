using NetPI.Storage.Sqlite;
using Xunit;

/// <summary>
/// astra-1 G1 — the global session picker searches ALL stored sessions through
/// the server. The store implements it with a case-insensitive title/workspace
/// LIKE query; the Web hub routes <c>session.list { query }</c> to it.
/// </summary>
public sealed class SessionSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netpi-search-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteSessionStore _store;

    public SessionSearchTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SqliteSessionStore(Path.Combine(_dir, "search.db"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static async Task<string> Seed(SqliteSessionStore store, string title, string? workspace)
    {
        var info = await store.CreateAsync(workspace);
        await store.RenameAsync(info.Id, title);
        return info.Id;
    }

    [Fact]
    public async Task Search_MatchesTitle_CaseInsensitively_AcrossAllStoredSessions()
    {
        await Seed(_store, "Astra-1 parity review", @"C:\AI\Projects\NetPI");
        await Seed(_store, "something else", @"C:\AI\Projects\Other");
        await Seed(_store, "astra notes", null);
        await Seed(_store, "Astra roadmap", @"D:\work");

        var hits = await _store.SearchAsync("astra", 100, 0);

        // Matches "Astra-1 parity review", "astra notes", "Astra roadmap" — the
        // "something else" row must NOT leak in even though it is stored.
        Assert.Equal(3, hits.Count);
        Assert.Equal(3, await _store.SearchCountAsync("astra"));
    }

    [Fact]
    public async Task Search_MatchesWorkspace_Too()
    {
        await Seed(_store, "untitled", @"C:\AI\Projects\NetPI");
        await Seed(_store, "untitled2", @"C:\AI\Projects\Unrelated");

        var hits = await _store.SearchAsync("netpi", 100, 0);

        Assert.Single(hits);
        Assert.Equal(@"C:\AI\Projects\NetPI", hits[0].WorkspacePath);
    }

    [Fact]
    public async Task Search_Paginates_WithOffsetAndCount()
    {
        for (var i = 1; i <= 7; i++)
            await Seed(_store, $"astra page {i}", null);

        var page1 = await _store.SearchAsync("astra", 4, 0);
        var page2 = await _store.SearchAsync("astra", 4, 4);

        Assert.Equal(4, page1.Count);
        Assert.Equal(3, page2.Count);
        var ids = page1.Concat(page2).Select(s => s.Id).ToList();
        Assert.Equal(7, ids.Distinct().Count());
    }

    [Fact]
    public async Task Search_EmptyQuery_FallsBack_ToFullListing()
    {
        await Seed(_store, "alpha", null);
        await Seed(_store, "beta", null);

        var hits = await _store.SearchAsync("   ");

        // Whitespace-only is treated as "no search": the plain listing.
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task Search_LikeWildcards_InTheQuery_DoNotMatchEverything()
    {
        await Seed(_store, "plain title", null);
        await Seed(_store, "100% done", null);

        // A '%' typed by the user must be a literal, not a LIKE wildcard.
        var hits = await _store.SearchAsync("100%");

        Assert.Single(hits);
        Assert.Equal("100% done", hits[0].Title);
    }
}
