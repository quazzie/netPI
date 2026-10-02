using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>A legacy file the cutover may read: what it is, where it is, and which project it belongs to.</summary>
/// <param name="Kind">backlog | pending | receipt | project</param>
public sealed record LegacySource(string Kind, string Path, string? ProjectId = null, string? ProjectName = null);

/// <summary>What the migration did (or would do): counts, sources, and anything it refused to guess at.</summary>
public sealed record MigrationReport(
    JsonObject Details,
    IReadOnlyList<(LegacySource Source, byte[] Bytes)> Sources,
    IReadOnlyList<JsonObject> Diagnostics);

/// <summary>
/// The one-time move of the JSON backlog into SQLite, and the explicit import/export that mirrors it.
/// <para>
/// The cutover is deliberate and repeatable. It reads every legacy source, refuses the whole thing if one of them is
/// malformed (an invalid source fails with a diagnostic; an empty backlog is never a substitute), stages what it read
/// outside the database and writes it inside <b>one</b> transaction that ends with the receipt and the durable backend
/// marker. A failure before that commit leaves the old files exactly as they were and SQLite empty; a failure after it
/// leaves SQLite authoritative. A restart that finds the marker does not import again.
/// </para>
/// <para>
/// After the commit the sources are archived byte for byte (with their checksums) and moved out of the place the old
/// build read them from: an older NetPI that is started afterwards therefore starts with an empty backlog instead of
/// quietly writing JSON that nothing reads. That is the whole of the downgrade protection — the marker cannot stop a
/// binary that has already shipped, and nothing here claims it can (docs/PLUGIN-IDEAS.md, "Upgrading and going back").
/// </para>
/// </summary>
public sealed class IdeasMigration(IdeasRepository repo, IdeasLocator locator, Func<IReadOnlyList<ProjectInfo>> projects, ILogger? log = null)
{
    /// <summary>The pending file of the JSON versions: the cards, the answers in flight, the check marks, the cursors.</summary>
    public const string PendingFileName = "ideas-pending.json";

    /// <summary>The receipt file of the per-project import the JSON versions did at every start.</summary>
    public const string ReceiptFileName = "ideas-migration.json";

    /// <summary>Where the cutover keeps the originals it read.</summary>
    public const string ArchiveFolder = "ideas-archive";

    /// <summary>The metadata key the durable backend marker lives under.</summary>
    public const string BackendKey = "backend";

    /// <summary>Where the preserved root-level fields of the JSON file are kept.</summary>
    public const string RootMetaKey = "legacy-root";

    private readonly IdeasRepository _repo = repo;
    private readonly IdeasLocator _locator = locator;
    private readonly Func<IReadOnlyList<ProjectInfo>> _projects = projects;
    private readonly ILogger? _log = log;

    // ------------------------------------------------------------------ sources

    /// <summary>
    /// Every file the JSON versions could have left behind: the global backlog (under whatever name the setting says),
    /// its pending file, its per-project receipt, and the per-project files themselves for every known project.
    /// </summary>
    public List<LegacySource> Sources()
    {
        var list = new List<LegacySource>();
        var home = _locator.Home;
        var name = _locator.FileName();
        list.Add(new LegacySource("backlog", Path.Combine(home, name)));
        list.Add(new LegacySource("pending", Path.Combine(home, PendingFileName)));
        list.Add(new LegacySource("receipt", Path.Combine(home, ReceiptFileName)));
        foreach (var p in Projects())
        {
            if (string.IsNullOrWhiteSpace(p.Path)) continue;
            list.Add(new LegacySource("project", Path.Combine(p.Path, ".netpi", name), p.Id, p.Name));
            list.Add(new LegacySource("project", Path.Combine(p.Path, name), p.Id, p.Name)); // the legacy place
        }
        return list;
    }

    private IReadOnlyList<ProjectInfo> Projects()
    {
        try { return _projects() ?? []; } catch { return []; }
    }

    // ------------------------------------------------------------------ preflight

    /// <summary>
    /// What the cutover would read, without writing anything: the resolved paths with their checksums, the counts,
    /// what is malformed, what is already imported, and whether anything is still pending in the old files. A source
    /// that does not exist is reported as missing (which is fine) and is distinct from one that is malformed.
    /// </summary>
    public async Task<JsonObject> PreflightAsync(CancellationToken ct = default)
    {
        var details = new JsonObject
        {
            ["backend"] = "sqlite",
            ["storage"] = _repo.Storage().DeepClone(),
            ["cutover"] = Cutover()?.DeepClone(),
            ["imports"] = new JsonArray(_repo.Imports().Select(i => (JsonNode?)i).ToArray()),
            ["sources"] = new JsonArray(),
        };
        foreach (var source in Sources())
        {
            var (file, bytes, error) = await LegacyBacklog.ReadAsync(source.Path, ct).ConfigureAwait(false);
            if (error is null && (file is null || bytes is null)) continue; // nothing there: not a source
            var entry = new JsonObject
            {
                ["kind"] = source.Kind,
                ["path"] = source.Path,
                ["project"] = source.ProjectId is { Length: > 0 } id ? new JsonObject { ["id"] = id, ["name"] = source.ProjectName } : null,
                ["sha256"] = bytes is null ? null : LegacyBacklog.HashOf(bytes),
                ["bytes"] = bytes?.Length ?? 0,
                ["imported"] = _repo.HasImport(ImportId(source), bytes is null ? null : LegacyBacklog.HashOf(bytes)),
            };
            if (error is not null) entry["error"] = error;
            else if (source.Kind is "backlog" or "project")
            {
                var ideas = file!.Ideas.OfType<JsonObject>().ToList();
                entry["ideas"] = ideas.Count;
                var ids = ideas.Select(i => IdeaOps.Str(i["id"])).Where(i => i is { Length: > 0 }).ToList();
                var duplicate = ids.GroupBy(i => i!, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (duplicate is not null) entry["duplicateIds"] = new JsonArray(duplicate.Select(i => (JsonNode?)i!).ToArray());
                if (ideas.Any(i => IdeaOps.Str(i["id"]) is not { Length: > 0 })) entry["unnamedIdeas"] = ideas.Count(i => IdeaOps.Str(i["id"]) is not { Length: > 0 });
            }
            else if (source.Kind == "pending")
            {
                entry["cards"] = (file!.Root["suggestions"] as JsonArray)?.Count ?? 0;
                entry["answersInFlight"] = (file.Root["ops"] as JsonArray)?.Count ?? 0;
                entry["checks"] = (file.Root["checked"] as JsonObject)?.Count ?? 0;
                entry["repos"] = (file.Root["repos"] as JsonObject)?.Count ?? 0;
            }
            else if (source.Kind == "receipt") entry["receipts"] = (file!.Root["imports"] as JsonArray)?.Count ?? 0;
            (details["sources"]!.AsArray()).Add(entry);
        }
        // An ideas file that appeared after the cutover is not a second backend to merge silently: it is a warning, and
        // an explicit import (ideas.migrate) is what takes it in.
        var after = Orphaned();
        if (after.Count > 0) details["orphanedSources"] = new JsonArray(after.Select(p => (JsonNode?)p).ToArray());
        return details;
    }

    /// <summary>Legacy files that exist although the cutover is done (an older build wrote them again).</summary>
    public List<string> Orphaned()
    {
        if (Cutover() is not { } done) return [];
        var at = IdeaOps.Str(done["at"]) is { Length: > 0 } s
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when) ? when : DateTimeOffset.MinValue;
        return Sources()
            .Where(s => File.Exists(s.Path) && (_repo.HasImport(ImportId(s)) || WrittenAfter(s.Path, at)))
            .Select(s => s.Path)
            .ToList();
    }

    private static bool WrittenAfter(string path, DateTimeOffset when)
    {
        try { return File.GetLastWriteTimeUtc(path) > when.UtcDateTime.AddSeconds(1); } catch { return false; }
    }

    /// <summary>The durable marker of the cutover (null: this database has never had one).</summary>
    public JsonObject? Cutover() => _repo.MetaObject(BackendKey);

    /// <summary>
    /// Whether SQLite is the authoritative store. Without the marker the plugin keeps the JSON file authoritative and
    /// says so, instead of two backends both taking writes.
    /// </summary>
    public bool IsCutOver => Cutover() is { } done && IdeaOps.Str(done["backend"]) == "sqlite";

    /// <summary>
    /// Record that the cutover happened with nothing to read (a fresh home): the marker is what says where the truth
    /// lives, and a home that never had an ideas file needs it as much as an imported one.
    /// </summary>
    public void MarkCutOver() => _repo.SetMeta(BackendKey, new JsonObject
    {
        ["backend"] = "sqlite",
        ["scope"] = IdeasRepository.Scope,
        ["schemaVersion"] = IdeasRepository.SchemaVersion,
        ["at"] = IdeaOps.Now(),
        ["sources"] = 0,
    }.ToJsonString());

    // ------------------------------------------------------------------ the cutover

    /// <summary>
    /// Do the cutover: read every source, then write everything in one transaction that also records the receipts and
    /// the marker. Refuses when the marker says it has already happened (use <see cref="ImportOrphansAsync"/> for a file
    /// an older build wrote afterwards), and refuses when a source is malformed, with a diagnostic naming it.
    /// </summary>
    public async Task<MigrationReport> RunAsync(bool force = false, CancellationToken ct = default)
    {
        if (IsCutOver && !force) throw new RpcException("conflict", "This backlog is already in the database (ideas.migration says so). " +
            "An ideas file that appeared afterwards is imported on purpose with { force: true }.");
        if (force && !IsCutOver) throw new RpcException("bad_request", "There is no cutover to repeat: this backlog has never been imported.");

        var diagnostics = new List<JsonObject>();
        var read = new List<(LegacySource Source, byte[] Bytes)>();
        // What the JSON version's own receipt says it merged already: those files are accounted for, not merged again.
        var merged = ReceiptEntries();
        foreach (var source in Sources())
        {
            ct.ThrowIfCancellationRequested();
            var (file, bytes, error) = await LegacyBacklog.ReadAsync(source.Path, ct).ConfigureAwait(false);
            // Missing is fine (there was nothing there); malformed is not, and nothing is written when a source cannot
            // be read — a broken file must not become a half-imported backlog.
            if (error is not null)
            {
                diagnostics.Add(new JsonObject { ["path"] = source.Path, ["error"] = error, ["fatal"] = true });
                continue;
            }
            if (file is null || bytes is null) continue;
            var hash = LegacyBacklog.HashOf(bytes!);
            if (!force && (_repo.HasImport(ImportId(source), hash) || merged.ContainsKey(source.Path)))
            {
                diagnostics.Add(new JsonObject
                {
                    ["path"] = source.Path,
                    ["skipped"] = "the JSON version had already merged this file",
                    ["ids"] = new JsonArray((merged.TryGetValue(source.Path, out var known) ? known : []).Select(i => (JsonNode?)i).ToArray()),
                });
                continue;
            }
            if (source.Kind is "backlog" or "project")
            {
                var unnamed = file!.Ideas.OfType<JsonObject>().Count(i => IdeaOps.Str(i["id"]) is not { Length: > 0 });
                if (unnamed > 0) diagnostics.Add(new JsonObject { ["path"] = source.Path, ["unnamedIdeas"] = unnamed, ["note"] = "given an id on import" });
            }
            read.Add((source, bytes!));
            _ = file;
        }
        if (diagnostics.Any(d => d["fatal"] is not null))
            throw new RpcException("invalid_file", "The ideas backlog was not imported: " + string.Join("; ", diagnostics
                .Where(d => d["fatal"] is not null).Select(d => $"{d["path"]}: {d["error"]}")));

        var counts = new JsonObject { ["ideas"] = 0, ["cards"] = 0, ["checks"] = 0, ["repos"] = 0, ["answers"] = 0, ["sources"] = read.Count };
        var marker = Cutover() is { } was
            ? (JsonObject)was.DeepClone()
            : new JsonObject { ["backend"] = "sqlite", ["scope"] = IdeasRepository.Scope, ["schemaVersion"] = IdeasRepository.SchemaVersion, ["at"] = IdeaOps.Now() };
        marker["at"] = IdeaOps.Now();

        // One transaction: the data, the receipts that account for it and the marker that makes SQLite authoritative
        // commit together, or none of them does.
        _repo.Transaction(r =>
        {
            foreach (var (source, bytes) in read)
            {
                var file = source.Kind is "backlog" or "project"
                    ? LegacyBacklog.Parse(source.Path, bytes)
                    : ParsePending(source.Path, bytes);
                switch (source.Kind)
                {
                    case "backlog":
                        counts["ideas"] = ImportBacklog(r, file.Ideas);
                        r.SetMeta(RootMetaKey, RootExtension(file.Root).ToJsonString());
                        break;
                    case "project":
                        counts["ideas"] = (int)(counts["ideas"]!.GetValue<int>() + ImportBacklog(r, file.Ideas, source.ProjectId, source.ProjectName));
                        break;
                    case "pending":
                        ImportPending(r, file.Root, counts, diagnostics);
                        break;
                    case "receipt":
                        ImportReceipts(r, file.Root, diagnostics);
                        break;
                }
                r.RecordImport(ImportId(source), "legacy", source.Path, LegacyBacklog.HashOf(bytes),
                    new JsonObject { ["kind"] = source.Kind });
            }
            r.SetMeta(BackendKey, marker.ToJsonString());
            return 0;
        });

        _log?.LogInformation("Ideas: imported {Ideas} idea(s), {Cards} card(s) and {Answers} interrupted answer(s) into the database",
            counts["ideas"], counts["cards"], counts["answers"]);
        var details = new JsonObject
        {
            ["backend"] = "sqlite",
            ["cutover"] = marker,
            ["counts"] = counts.DeepClone(),
            ["diagnostics"] = new JsonArray(diagnostics.Select(d => (JsonNode?)d).ToArray()),
            ["archive"] = Archive(read, counts),
        };
        return new MigrationReport(details, read, diagnostics);
    }

    /// <summary>Import the ideas of one file, keeping their ids, their order and every field they carry.</summary>
    private int ImportBacklog(IdeasRepository r, JsonArray ideas, string? projectId = null, string? projectName = null)
    {
        var n = 0;
        foreach (var node in ideas.OfType<JsonObject>())
        {
            var idea = (JsonObject)node.DeepClone();
            var id = IdeaOps.Str(idea["id"]);
            var taken = new List<string>(r.TakenIds());
            if (id is not { Length: > 0 } || taken.Any(t => string.Equals(t, id, StringComparison.OrdinalIgnoreCase)))
                idea["id"] = IdeaOps.NewId("idea-", taken, 6); // a collision is a new id, never a merged idea
            if (projectId is { Length: > 0 } && IdeaOps.ProjectOf(idea) is null) IdeaOps.SetProject(idea, projectId, projectName);
            r.Add(idea, prepend: false);
            n++;
        }
        return n;
    }

    /// <summary>
    /// The fields of the JSON file that are not the backlog itself (a tool of the user's own, a note, a plugin's data).
    /// They are kept whole at the root of the document: the import does not have to understand them to preserve them.
    /// </summary>
    private static JsonObject RootExtension(JsonObject root)
    {
        var extra = new JsonObject();
        foreach (var (key, value) in root)
            if (key is not ("version" or "ideas")) extra[key] = value?.DeepClone();
        return extra;
    }

    private static LegacyFile ParsePending(string path, byte[] bytes)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject
                   ?? throw new IdeasFileException($"{path} must contain a JSON object.");
        return new LegacyFile { Path = path, Root = root, Ideas = [], Exists = true };
    }

    /// <summary>
    /// The pending file: the cards the user is answering, the check marks, the repository cursors — and the answers
    /// that were in flight when NetPI stopped, reconciled by their own identities (see <see cref="Reconcile"/>).
    /// </summary>
    private void ImportPending(IdeasRepository r, JsonObject root, JsonObject counts, List<JsonObject> diagnostics)
    {
        foreach (var card in (root["suggestions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (IdeaOps.Str(card["id"]) is not { Length: > 0 }) { diagnostics.Add(new JsonObject { ["card"] = "without an id", ["skipped"] = true }); continue; }
            r.ImportCard(card);
            counts["cards"] = (int)(counts["cards"]!.GetValue<int>() + 1);
        }
        foreach (var (sessionId, mark) in root["checked"] as JsonObject ?? [])
        {
            if (mark is not JsonObject m) continue;
            r.ImportCheck(sessionId, m);
            counts["checks"] = (int)(counts["checks"]!.GetValue<int>() + 1);
        }
        foreach (var (repo, value) in root["repos"] as JsonObject ?? [])
        {
            if (value is not JsonObject entry) continue;
            r.ImportRepo(new JsonObject { ["repo"] = repo, ["hash"] = entry["hash"]?.DeepClone(), ["at"] = entry["at"]?.DeepClone() });
            counts["repos"] = (int)(counts["repos"]!.GetValue<int>() + 1);
        }
        Reconcile(r, root["ops"] as JsonArray ?? [], counts, diagnostics);
    }

    /// <summary>
    /// The answers that were in flight. Each entry names its card, its action and the idea it wrote, so an interrupted
    /// answer is finished exactly once: an idea that is already in the backlog (the write happened) is recorded as the
    /// answer it was and nothing is written again; an entry that claims to be applied while its idea is missing is
    /// <b>not</b> guessed at — it is reported, because a duplicate idea is worse than a diagnostic.
    /// </summary>
    private void Reconcile(IdeasRepository r, JsonArray ops, JsonObject counts, List<JsonObject> diagnostics)
    {
        foreach (var node in ops.OfType<JsonObject>())
        {
            var opId = IdeaOps.Str(node["id"]) ?? "";
            var cardId = IdeaOps.Str(node["cardId"]) ?? "";
            var action = IdeaOps.Str(node["action"]) ?? "";
            var applied = node["applied"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            if (cardId.Length == 0 || action is not ("save" or "done" or "discard"))
            {
                diagnostics.Add(new JsonObject { ["op"] = opId, ["error"] = "an answer without a card or with an unknown action", ["fatal"] = false });
                continue;
            }
            if (r.Resolution(cardId) is { } known) // already answered in this backlog
            {
                r.RemoveCard(cardId);
                diagnostics.Add(new JsonObject { ["op"] = opId, ["card"] = cardId, ["action"] = known.Action, ["note"] = "already answered" });
                continue;
            }
            var wanted = action == "save" ? node["idea"] as JsonObject : null;
            var ideaId = action == "save" ? IdeaOps.Str(wanted?["id"]) : IdeaOps.Str(node["ideaId"]);
            if (action == "save" && ideaId is { Length: > 0 } && r.Find(ideaId) is { } stored && SameIdea(stored.Doc, wanted!))
            {
                // The idea the answer wrote is there: the answer is finished, not repeated.
                r.RemoveCard(cardId);
                diagnostics.Add(new JsonObject { ["op"] = opId, ["card"] = cardId, ["action"] = action, ["note"] = "the idea was already in the backlog" });
                continue;
            }
            if (applied)
            {
                // The journal says it was written and it is not there. Guessing would either duplicate an idea or lose
                // the answer; the user is told instead.
                diagnostics.Add(new JsonObject
                {
                    ["op"] = opId, ["card"] = cardId, ["action"] = action, ["ideaId"] = ideaId,
                    ["error"] = "the answer is marked as applied but its idea is not in the backlog; it was not replayed",
                });
                continue;
            }
            try
            {
                var resolution = r.FinishLegacyAnswer(cardId, action, wanted, IdeaOps.Str(node["ideaId"]));
                counts["answers"] = (int)(counts["answers"]!.GetValue<int>() + 1);
                diagnostics.Add(new JsonObject { ["op"] = opId, ["card"] = cardId, ["action"] = action, ["ideaId"] = IdeaOps.Str(resolution.Idea?["id"]), ["note"] = "finished" });
            }
            catch (Exception ex) when (ex is RpcException or IdeaInputException)
            {
                diagnostics.Add(new JsonObject { ["op"] = opId, ["card"] = cardId, ["error"] = ex.Message, ["fatal"] = false });
            }
        }
    }

    /// <summary>The idea already in the backlog is the one this answer wrote (same id, title and creation time).</summary>
    private static bool SameIdea(JsonObject stored, JsonObject wanted) =>
        IdeaOps.Str(stored["title"]) == IdeaOps.Str(wanted["title"]) && IdeaOps.Str(stored["createdAt"]) == IdeaOps.Str(wanted["createdAt"]);

    /// <summary>
    /// The per-project receipts the JSON version kept: the sources it already merged (so a later run does not merge
    /// them again) and the ids it gave their ideas (for the report).
    /// </summary>
    private void ImportReceipts(IdeasRepository r, JsonObject root, List<JsonObject> diagnostics)
    {
        foreach (var node in (root["imports"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (IdeaOps.Str(node["source"]) is not { Length: > 0 } source) continue;
            var hash = IdeaOps.Str(node["sha256"]) ?? "";
            var ids = (node["imported"] as JsonArray ?? []).Select(i => IdeaOps.Str(i)).Where(i => i is { Length: > 0 }).Select(i => i!).ToList();
            r.RecordImport(ImportId(new LegacySource("project", source)), "legacy-project", source, hash, new JsonObject { ["ids"] = new JsonArray(ids.Select(i => (JsonNode?)i).ToArray()) });
            diagnostics.Add(new JsonObject { ["source"] = source, ["note"] = "the JSON version had already merged this file", ["ids"] = ids.Count });
        }
    }

    /// <summary>The per-project files the JSON version's own receipt says it merged, and the ids it gave them.</summary>
    private Dictionary<string, List<string>> ReceiptEntries()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(_locator.Home, ReceiptFileName);
        try
        {
            if (!File.Exists(file)) return map;
            var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject;
            foreach (var node in (root?["imports"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (IdeaOps.Str(node["source"]) is not { Length: > 0 } source) continue;
                map[source] = (node["imported"] as JsonArray ?? []).Select(IdeaOps.Str).Where(i => i is { Length: > 0 }).Select(i => i!).ToList();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* an unreadable receipt is an empty one */ }
        return map;
    }

    /// <summary>Import an ideas file an older build wrote after the cutover, keeping what is already there.</summary>
    public async Task<MigrationReport> ImportOrphansAsync(CancellationToken ct = default) => await RunAsync(force: true, ct).ConfigureAwait(false);

    // ------------------------------------------------------------------ archive

    /// <summary>
    /// Keep the originals: a copy of every source with its checksum, then the file itself, moved out of the place the
    /// old build read it from. The originals are never deleted — a cutover that is regretted is undone by moving them
    /// back (docs/PLUGIN-IDEAS.md, "Going back").
    /// </summary>
    private JsonObject? Archive(IReadOnlyList<(LegacySource Source, byte[] Bytes)> read, JsonObject counts)
    {
        if (read.Count == 0) return null;
        var home = _locator.Home;
        var folder = Path.Combine(home, ArchiveFolder, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Ids.Short(6));
        try
        {
            Directory.CreateDirectory(folder);
            var manifest = new JsonObject
            {
                ["version"] = 1,
                ["at"] = IdeaOps.Now(),
                ["database"] = _repo.Storage()["database"]?.DeepClone(),
                ["counts"] = counts.DeepClone(),
                ["sources"] = new JsonArray(),
            };
            for (var k = 0; k < read.Count; k++)
            {
                var (source, bytes) = read[k];
                // Two sources can be called ideas.json (the global one and a project's), so the name carries its place.
                var name = $"{source.Kind}-{k}-{Path.GetFileName(source.Path)}";
                File.WriteAllBytes(Path.Combine(folder, name), bytes); // byte for byte, as it was
                (manifest["sources"]!.AsArray()).Add(new JsonObject
                {
                    ["kind"] = source.Kind, ["path"] = source.Path, ["file"] = name, ["bytes"] = bytes.Length,
                    ["sha256"] = LegacyBacklog.HashOf(bytes),
                    ["project"] = source.ProjectId is { Length: > 0 } id ? new JsonObject { ["id"] = id, ["name"] = source.ProjectName } : null,
                });
                var kept = Path.Combine(folder, "originals", name);
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                if (File.Exists(source.Path)) File.Move(source.Path, kept, overwrite: true);
            }
            File.WriteAllText(Path.Combine(folder, "manifest.json"), LegacyBacklog.Render(manifest));
            _log?.LogInformation("Ideas: {Count} legacy file(s) kept in {Folder} and moved out of the way", read.Count, folder);
            return new JsonObject { ["folder"] = folder, ["sources"] = manifest["sources"]?.DeepClone() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The marker is already committed: SQLite is authoritative whatever housekeeping failed.
            _log?.LogWarning("Ideas: the imported files could not be archived ({Message}); SQLite is authoritative and the originals are still where they were", ex.Message);
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    private static string ImportId(LegacySource source) => "legacy:" + source.Kind + ":" + Path.GetFullPath(source.Path).ToLowerInvariant();

    // ------------------------------------------------------------------ explicit export and import

    /// <summary>
    /// A portable snapshot: stable ids, the user's order, every field the ideas carry, the cards waiting for an
    /// answer, the answers already given, the check marks, the repository cursors and the commits recorded unread
    /// (the ones the sweep could not decide, so a new home sees them as skipped, not silently dropped).
    /// This is the format to hand to somebody else or to keep — it is not a live file the backlog is written to.
    /// </summary>
    public JsonObject Export() => new()
    {
        ["version"] = 1,
        ["format"] = "netpi.ideas.export",
        ["exportedAt"] = IdeaOps.Now(),
        ["backend"] = "sqlite",
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
                IdeaOps.Str(snapshot["format"]) ?? "netpi.ideas.export", LegacyBacklog.HashOf(Encoding.UTF8.GetBytes(snapshot.ToJsonString())),
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
        await File.WriteAllTextAsync(full, LegacyBacklog.Render(Export()), ct).ConfigureAwait(false);
        return full;
    }
}
