using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPI.Ideas;

/// <summary>
/// One idea in a project's memory bank. Kept as a POCO (System.Text.Json
/// round-trips it both ways); the on-disk shape is versioned by
/// <see cref="IdeasFile.Version"/>.
/// </summary>
public sealed class IdeaRecord
{
    /// <summary>Stable short id (8 hex). The id an agent/user refers to in chat.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>The idea itself (what + why).</summary>
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    /// <summary>Discussion notes accumulated while talking it over with an agent.</summary>
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    /// <summary>A concrete plan/steps once the idea has been worked out.</summary>
    [JsonPropertyName("plan")] public string Plan { get; set; } = "";
    /// <summary>Conventional values: idea | planned | in-progress | done | dropped (any string accepted).</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = IdeasStore.DefaultStatus;
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Thrown when ideas.json exists but cannot be parsed (never silently rewritten).</summary>
public sealed class IdeasStoreException(string message) : Exception(message);

/// <summary>
/// The per-project ideas bank: one <c>ideas.json</c> in the project (session)
/// root. Synchronous, guarded by a lock, atomic saves (tmp + move). One
/// instance per workspace; tools and the panel surface each build one per
/// operation.
/// </summary>
public sealed class IdeasStore
{
    public const int Version = 1;
    public const string FileName = "ideas.json";
    public const string DefaultStatus = "idea";
    public static readonly string[] KnownStatuses = { "idea", "planned", "in-progress", "done", "dropped" };

    private sealed class FileDoc
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("ideas")] public List<IdeaRecord> Ideas { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _gate = new();

    public string Workspace { get; }
    public string FilePath => Path.Combine(Workspace, FileName);

    public IdeasStore(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
            throw new IdeasStoreException("a workspace path is required");
        Workspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(Workspace))
            throw new IdeasStoreException($"workspace does not exist: {Workspace}");
    }

    public List<IdeaRecord> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath)) return new List<IdeaRecord>();
            string raw;
            try { raw = File.ReadAllText(FilePath); }
            catch (Exception ex) { throw new IdeasStoreException($"could not read {FilePath}: {ex.Message}"); }
            if (string.IsNullOrWhiteSpace(raw)) return new List<IdeaRecord>();
            FileDoc? doc;
            try { doc = JsonSerializer.Deserialize<FileDoc>(raw, JsonOpts); }
            catch (Exception ex) { throw new IdeasStoreException($"corrupt {FilePath}: {ex.Message}"); }
            return doc?.Ideas ?? new List<IdeaRecord>();
        }
    }

    public IdeaRecord Add(string title, string? body = null, string? notes = null, string? plan = null,
        string? status = null, string[]? tags = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new IdeasStoreException("title is required");
        lock (_gate)
        {
            var ideas = LoadLocked();
            var now = DateTimeOffset.UtcNow;
            var record = new IdeaRecord
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Title = title.Trim(),
                Body = body ?? "",
                Notes = notes ?? "",
                Plan = plan ?? "",
                Status = NormalizeStatus(status),
                Tags = (tags ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            ideas.Add(record);
            SaveLocked(ideas);
            return record;
        }
    }

    /// <summary>
    /// Find by id (case-insensitive exact) or by a case-insensitive substring
    /// of the title (the way a user says it in chat: "the auto-bind pool idea").
    /// Returns null when nothing matches; a title fragment matching several
    /// ideas returns the most recently updated.
    /// </summary>
    public IdeaRecord? Find(string idOrTitle)
    {
        if (string.IsNullOrWhiteSpace(idOrTitle)) return null;
        var key = idOrTitle.Trim();
        lock (_gate)
        {
            var ideas = LoadLocked();
            var byId = ideas.FirstOrDefault(i => string.Equals(i.Id, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.Title, key, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;
            var byTitle = ideas.Where(i => i.Title.Contains(key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.UpdatedAt).FirstOrDefault();
            return byTitle;
        }
    }

    /// <summary>
    /// Patch one idea: only non-null arguments change (tags: null = keep,
    /// explicit array = replace). Returns the updated record, or null when the
    /// id is unknown.
    /// </summary>
    public IdeaRecord? Update(string idOrTitle, string? title = null, string? body = null, string? notes = null,
        string? plan = null, string? status = null, string[]? tags = null)
    {
        if (string.IsNullOrWhiteSpace(idOrTitle))
            throw new IdeasStoreException("id is required");
        lock (_gate)
        {
            var ideas = LoadLocked();
            var record = FindLocked(ideas, idOrTitle.Trim());
            if (record is null) return null;
            if (!string.IsNullOrWhiteSpace(title)) record.Title = title.Trim();
            if (body is not null) record.Body = body;
            if (notes is not null) record.Notes = notes;
            if (plan is not null) record.Plan = plan;
            if (status is not null) record.Status = NormalizeStatus(status);
            if (tags is not null)
                record.Tags = tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            record.UpdatedAt = DateTimeOffset.UtcNow;
            SaveLocked(ideas);
            return record;
        }
    }

    public bool Remove(string idOrTitle)
    {
        if (string.IsNullOrWhiteSpace(idOrTitle)) return false;
        lock (_gate)
        {
            var ideas = LoadLocked();
            var key = idOrTitle.Trim();
            var idx = ideas.FindIndex(i => string.Equals(i.Id, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.Title, key, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            ideas.RemoveAt(idx);
            SaveLocked(ideas);
            return true;
        }
    }

    // ---- internals (lock held) ---------------------------------------------

    private static string NormalizeStatus(string? status)
        => string.IsNullOrWhiteSpace(status) ? DefaultStatus : status.Trim();

    private List<IdeaRecord> LoadLocked()
    {
        if (!File.Exists(FilePath)) return new List<IdeaRecord>();
        string raw = File.ReadAllText(FilePath);
        if (string.IsNullOrWhiteSpace(raw)) return new List<IdeaRecord>();
        var doc = JsonSerializer.Deserialize<FileDoc>(raw, JsonOpts)
            ?? throw new IdeasStoreException($"corrupt {FilePath}: null document");
        return doc.Ideas ?? new List<IdeaRecord>();
    }

    private void SaveLocked(List<IdeaRecord> ideas)
    {
        var doc = new FileDoc { Version = Version, Ideas = ideas };
        var raw = JsonSerializer.Serialize(doc, JsonOpts);
        var tmp = Path.Combine(Workspace, $".{FileName}.tmp-{Guid.NewGuid():N}");
        File.WriteAllText(tmp, raw);
        try
        {
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    private static IdeaRecord? FindLocked(List<IdeaRecord> ideas, string key)
    {
        var byId = ideas.FirstOrDefault(i => string.Equals(i.Id, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(i.Title, key, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        return ideas.Where(i => i.Title.Contains(key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.UpdatedAt).FirstOrDefault();
    }
}
