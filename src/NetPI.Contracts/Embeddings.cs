using System.Numerics;
using System.Runtime.InteropServices;

namespace NetPI;

/// <summary>What a text is to the embedding model: a stored document, or a search for one. Retrieval models
/// (bge, Qwen3-Embedding) want an instruction or prefix on queries only, so the client has to know.</summary>
public enum EmbeddingKind { Document, Query }

public sealed class EmbeddingRequest
{
    public required IReadOnlyList<string> Texts { get; init; }
    public EmbeddingKind Kind { get; init; } = EmbeddingKind.Document;
    /// <summary>Indexing work in the background: the longer batch timeout instead of the interactive one.</summary>
    public bool Background { get; init; }
}

/// <summary>One vector per text, in the request's order, each normalized to length 1 (a dot product is the cosine).</summary>
public sealed record EmbeddingResult(string Model, int Dimensions, IReadOnlyList<float[]> Vectors);

/// <summary>An embedding call that did not answer: <c>off</c> (no model configured), <c>unavailable</c> (backing off
/// after failures), <c>unreachable</c>, <c>timeout</c>, <c>bad_response</c> or the server's own code.</summary>
public sealed class EmbeddingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Text embeddings from an OpenAI-compatible <c>/v1/embeddings</c> server (the Embeddings plugin). A consumer resolves
/// it per use and treats every failure as "no embeddings": it is a way to shortlist candidates, never the only path.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>The configured model, or null when embeddings are switched off.</summary>
    string? Model { get; }
    /// <summary>A model is configured and the server is not in its back-off after failures.</summary>
    bool Available { get; }
    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct);
}

/// <summary>The arithmetic the consumers of <see cref="IEmbeddingService"/> share: cosine on normalized vectors and a
/// compact base64 form for storing them in JSON documents.</summary>
public static class VectorMath
{
    public static float[] Normalize(float[] v)
    {
        var len = MathF.Sqrt(Dot(v, v));
        if (len is 0 or float.NaN) return v;
        for (var i = 0; i < v.Length; i++) v[i] /= len;
        return v;
    }

    /// <summary>The dot product (the cosine for normalized vectors); 0 when the lengths differ.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) return 0;
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
            sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        var dot = Vector.Dot(sum, Vector<float>.One);
        for (; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    /// <summary>Little-endian float32 bytes as base64 (~5.4 KB for 1,024 dimensions, a third of a JSON number array).</summary>
    public static string Pack(float[] v) => Convert.ToBase64String(MemoryMarshal.AsBytes(v.AsSpan()));

    public static float[]? Unpack(string? packed)
    {
        if (string.IsNullOrEmpty(packed)) return null;
        try
        {
            var bytes = Convert.FromBase64String(packed);
            return bytes.Length % 4 == 0 ? MemoryMarshal.Cast<byte, float>(bytes).ToArray() : null;
        }
        catch (FormatException) { return null; }
    }
}
