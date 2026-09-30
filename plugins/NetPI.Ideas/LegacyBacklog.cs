using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>The ideas file exists but cannot be parsed. It is never overwritten in that state.</summary>
public sealed class IdeasFileException(string message) : Exception(message);

/// <summary>A legacy ideas file as it is on disk: the root document with every field it had, and the ideas array.</summary>
public sealed class LegacyFile
{
    public required string Path { get; init; }
    public required JsonObject Root { get; init; }
    public required JsonArray Ideas { get; init; }
    public bool Exists { get; init; }
}

/// <summary>
/// The JSON backlog of the versions before the SQLite one, read only. Storage is the app's database now; what is left
/// of the file format is the import (and the export that mirrors it), so this reads a file and never writes one: the
/// bytes it parsed are the bytes a migration archive keeps.
/// </summary>
public static class LegacyBacklog
{
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The hash of a source's bytes: the same bytes are the same import, a different one is a different import.</summary>
    public static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Read a legacy file under the OS lock that every reader of it takes (so the import never sees half of a write by
    /// the old plugin that is still running), or report why it cannot.
    /// </summary>
    public static async Task<(LegacyFile? File, byte[]? Bytes, string? Error)> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return (null, null, null);
            var gate = new FileGate();
            return await gate.WithFileAsync(path, async token =>
            {
                var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                return ((LegacyFile?)Parse(path, bytes), bytes, (string?)null);
            }, ct).ConfigureAwait(false);
        }
        catch (IdeasFileException ex) { return (null, null, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, null, ex.Message); }
    }

    /// <summary>
    /// Parse a legacy backlog. An empty file and a bare array are both accepted (older versions wrote them), a
    /// document that is not JSON is refused with a precise message: an invalid source fails the import instead of
    /// quietly importing an empty backlog.
    /// </summary>
    public static LegacyFile Parse(string path, byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || (bytes.Length == 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF))
            return new LegacyFile { Path = path, Root = [], Ideas = [], Exists = bytes is not null };

        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        if (text.Trim().Length == 0) return new LegacyFile { Path = path, Root = [], Ideas = [], Exists = true };

        JsonNode? node;
        try { node = JsonNode.Parse(text, null, ReadOptions); }
        catch (JsonException ex) { throw new IdeasFileException($"{path} is not valid JSON ({ex.Message}). Fix it or move it aside; it was not imported."); }

        JsonObject root;
        switch (node)
        {
            case JsonObject o: root = o; break;
            case JsonArray a: root = new JsonObject { ["version"] = 1, ["ideas"] = a }; break; // a bare array: wrap it
            default: throw new IdeasFileException($"{path} must contain a JSON object {{ \"version\": 1, \"ideas\": [...] }}.");
        }
        if (root["ideas"] is not JsonArray ideas)
        {
            if (root["ideas"] is not null)
                throw new IdeasFileException($"{path}: \"ideas\" must be an array. Fix the file; it was not imported.");
            ideas = [];
            root["ideas"] = ideas;
        }
        return new LegacyFile { Path = path, Root = root, Ideas = ideas, Exists = true };
    }

    /// <summary>A portable snapshot of what was imported: a versioned document, LF endings, two-space indent.</summary>
    public static string Render(JsonObject root) => root.ToJsonString(WriteOptions) + "\n";

    /// <summary>The file's own creation time, for the report (never for ordering: the ideas keep their array order).</summary>
    public static DateTimeOffset? WrittenAt(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return null; }
    }

    internal static string Stamp(DateTimeOffset when) => when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
