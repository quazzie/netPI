using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>
/// The file form of a portable snapshot: the checksum that says which bytes an import accounted for, and the
/// document it is written as — versioned, LF endings, two-space indent.
/// </summary>
public static class SnapshotFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The hash of a source's bytes: the same bytes are the same import, a different one is a different import.</summary>
    public static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A portable snapshot of what was imported: a versioned document, LF endings, two-space indent.</summary>
    public static string Render(JsonObject root) => root.ToJsonString(WriteOptions) + "\n";
}
