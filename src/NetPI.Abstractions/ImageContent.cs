namespace NetPI.Abstractions;

/// <summary>Bounded raster attachments shared by file tools and the Web surface.</summary>
public static class ImageContent
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxPromptBytes = 600 * 1024;

    public static string? DetectMimeType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
