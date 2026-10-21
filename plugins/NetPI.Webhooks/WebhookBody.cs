using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace NetPI.Webhooks;

/// <summary>Reads a request body up to a limit; null when it is larger.</summary>
internal static class Body
{
    public static async Task<string?> ReadAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength is { } declared && declared > maxBytes) return null;
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The payload: a JSON document as it is, anything else wrapped as <c>{ "body": text }</c> so a plain-text sender still works.</summary>
    public static JsonNode Payload(string text, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(text) && (contentType is null || contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')))
        {
            try { if (JsonNode.Parse(text) is { } node) return node; } catch (System.Text.Json.JsonException) { /* plain text below */ }
        }
        return new JsonObject { ["body"] = text };
    }
}
