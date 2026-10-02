using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>
/// The portable form of the backlog: a versioned snapshot (the <c>ideas.export</c> and <c>ideas.import</c> RPCs) that
/// mirrors the storage — stable ids, the user's order, every field the ideas carry, the cards waiting for an answer,
/// the answers already given, the check marks, the repository cursors and the commits recorded unread. A snapshot is
/// the format to hand to somebody else or to keep: it is not a live file the backlog is written to.
/// </summary>
public sealed class IdeaSnapshots(IdeasRepository repo)
{
    private readonly IdeasRepository _repo = repo;

    /// <summary>Where the preserved root-level fields of an imported file are kept.</summary>
    public const string RootMetaKey = "legacy-root";

    /// <summary>
    /// A portable snapshot: stable ids, the user's order, every field the ideas carry, the cards waiting for an
    /// answer, the answers already given, the check marks, the repository cursors and the commits recorded unread
    /// (the ones the sweep could not decide, so a new home sees them as skipped, not silently dropped).
    /// </summary>
    public JsonObject Export() => new()
    {
        ["version"] = 1,
        ["format"] = "netpi.ideas.export",
        ["exportedAt"] = IdeaOps.Now(),
        ["backend"] = _repo.Storage()["backend"]?.DeepClone(),
        ["scope"] = IdeasRepository.Scope,
        ["schemaVersion"] = IdeasRepository.SchemaVersion,
        ["ideas"] = new JsonArray(_repo.All().Select(i => (JsonNode?)i.DeepClone()).ToArray()),
        ["cards"] = new JsonArray(_repo.Cards().Select(c => (JsonNode?)c.DeepClone()).ToArray()),
        ["resolutions"] = new JsonArray(_repo.Resolutions().Select(r => (JsonNode?)r).ToArray()),
        ["checks"] = new JsonArray(_repo.Checks().Select(c => (JsonNode?)c).ToArray()),
        ["repos"] = new JsonArray(_repo.Repos().Select(r => (JsonNode?)r).ToArray()),
        ["unread"] = new JsonArray(_repo.Unread().Select(r => (JsonNode?)r).ToArray()),
        ["root"] = _repo.MetaObject(RootMetaKey)?.DeepClone(),
    };

    /// <summary>
    /// What an imported snapshot would do, without doing it: the counts, the ids that are already here (a conflict) and
    /// anything in the file this build does not understand. Nothing is replaced and nothing is duplicated silently.
    /// </summary>
    public JsonObject ValidateSnapshot(JsonObject snapshot)
    {
        var report = new JsonObject
        {
            ["version"] = snapshot["version"]?.DeepClone(),
            ["format"] = snapshot["format"]?.DeepClone(),
            ["ideas"] = (snapshot["ideas"] as JsonArray)?.Count ?? 0,
            ["cards"] = (snapshot["cards"] as JsonArray)?.Count ?? 0,
            ["conflicts"] = new JsonArray(),
            ["unknownFields"] = new JsonArray(),
        };
        foreach (var field in snapshot.Select(p => p.Key))
            if (field is not ("version" or "format" or "exportedAt" or "backend" or "scope" or "schemaVersion" or "ideas" or "cards" or "resolutions" or "checks" or "repos" or "root"))
                (report["unknownFields"]!.AsArray()).Add(field);
        foreach (var idea in (snapshot["ideas"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (IdeaOps.Str(idea["id"]) is not { Length: > 0 } id) { (report["unknownFields"]!.AsArray()).Add("an idea without an id"); continue; }
            if (_repo.Find(id) is not null) (report["conflicts"]!.AsArray()).Add(id);
        }
        return report;
    }

    /// <summary>
    /// Take a snapshot in. <paramref name="mode"/> is "merge" (default: ideas and cards that are not here yet are
    /// added, everything that is here stays) or "replace" (the backlog is emptied first — an explicit, deliberate
    /// act, which is why it says so in its answer).
    /// </summary>
    public JsonObject ImportSnapshot(JsonObject snapshot, string mode = "merge")
    {
        if (mode is not ("merge" or "replace")) throw new RpcException("bad_request", "mode must be \"merge\" or \"replace\"");
        var report = ValidateSnapshot(snapshot);
        var ideas = (snapshot["ideas"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var cards = (snapshot["cards"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        _repo.Transaction(r =>
        {
            if (mode == "replace")
            {
                foreach (var idea in r.All()) r.Delete(IdeaOps.Str(idea["id"]));
                foreach (var card in r.Cards()) r.RemoveCard(IdeaOps.Str(card["id"]) ?? "");
            }
            foreach (var idea in ideas)
            {
                if (IdeaOps.Str(idea["id"]) is not { Length: > 0 }) continue;
                if (r.Find(IdeaOps.Str(idea["id"])) is not null) continue; // a merge never overwrites
                r.Add(idea);
            }
            foreach (var card in cards) r.ImportCard(card);
            if (snapshot["root"] is JsonObject root) r.SetMeta(RootMetaKey, root.ToJsonString());
            r.RecordImport("snapshot:" + (IdeaOps.Str(snapshot["exportedAt"]) ?? Ids.Short(8)), "snapshot",
                IdeaOps.Str(snapshot["format"]) ?? "netpi.ideas.export", SnapshotFile.HashOf(Encoding.UTF8.GetBytes(snapshot.ToJsonString())),
                new JsonObject { ["ideas"] = ideas.Count, ["cards"] = cards.Count, ["mode"] = mode });
            return 0;
        });
        report["mode"] = mode;
        report["imported"] = new JsonObject { ["ideas"] = ideas.Count, ["cards"] = cards.Count };
        return report;
    }

    /// <summary>Write a snapshot to a file (the user asked for it; the app never writes one by itself).</summary>
    public async Task<string> ExportToFileAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, SnapshotFile.Render(Export()), ct).ConfigureAwait(false);
        return full;
    }
}
