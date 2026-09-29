using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>
/// What the one-time import of the per-project files of earlier versions already did: one entry per source file, with
/// the hash of its content and the idea ids the import produced. Written <b>before</b> a source is deleted, so an
/// interrupted migration (a crash, a delete that fails because the file is open) re-imports nothing at the next start —
/// it only finishes the delete. A file we cannot parse is treated as an empty receipt (the migration re-runs and the
/// collisions get new ids), never as a reason to delete a source.
/// </summary>
internal sealed class MigrationReceipt
{
    public const string FileName = "ideas-migration.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly Dictionary<string, (string Hash, List<string> Ids)> _imports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The hash of a source file's content: the same bytes are the same import.</summary>
    public static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public bool Has(string source, string hash) => _imports.TryGetValue(source, out var e) && e.Hash == hash;

    public void Remember(string source, string hash, IEnumerable<string> ids) => _imports[source] = (hash, [.. ids]);

    public static MigrationReceipt Read(string file)
    {
        var receipt = new MigrationReceipt();
        try
        {
            if (!File.Exists(file)) return receipt;
            var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject;
            foreach (var node in (root?["imports"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (IdeaOps.Str(node["source"]) is not { Length: > 0 } source || IdeaOps.Str(node["sha256"]) is not { Length: > 0 } hash) continue;
                var ids = (node["imported"] as JsonArray ?? []).Select(i => IdeaOps.Str(i)).Where(i => i is { Length: > 0 }).Select(i => i!).ToList();
                receipt._imports[source] = (hash, ids);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* an unreadable receipt is an empty one */ }
        return receipt;
    }

    public async Task SaveAsync(string file, CancellationToken ct)
    {
        var root = new JsonObject
        {
            ["version"] = 1,
            ["imports"] = new JsonArray(_imports.Select(p => (JsonNode)new JsonObject
            {
                ["source"] = p.Key,
                ["sha256"] = p.Value.Hash,
                ["imported"] = new JsonArray(p.Value.Ids.Select(i => (JsonNode)i!).ToArray()),
                ["at"] = IdeaOps.Now(),
            }).ToArray()),
        };
        await FileGate.WriteAtomicAsync(file, System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(Options)), ct).ConfigureAwait(false);
    }
}
