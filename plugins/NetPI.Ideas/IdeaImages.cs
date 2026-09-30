using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>
/// An image attached to an idea. The bytes live in a file under the home; the idea keeps a home-relative reference
/// ({ path, name, mediaType, bytes }), so a moved or backed-up home still finds them and <c>ideas.list</c> stays small
/// — an idea document is read on every list, poll and prompt.
/// </summary>
public static class IdeaImages
{
    /// <summary>Where the files go, relative to the home.</summary>
    public const string Dir = "idea-images";
    /// <summary>Largest single image. The UI shrinks anything bigger before it gets here (the composer's budget).</summary>
    public const int MaxBytes = 4 * 1024 * 1024;
    /// <summary>How many images one idea may carry — enough for a bug report, few enough to keep a prompt readable.</summary>
    public const int MaxPerIdea = 6;

    private static readonly string[] MediaTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public static bool IsImage(string? mediaType) => mediaType is not null && Array.IndexOf(MediaTypes, mediaType) >= 0;

    public static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => throw new IdeaInputException($"An idea image must be one of {string.Join(", ", MediaTypes)} (got \"{mediaType}\")."),
    };

    /// <summary>Store the image and return the reference the idea keeps. The data is base64, with or without a
    /// <c>data:</c> prefix (the browser sends both shapes depending on who produced it).</summary>
    public static JsonObject Attach(string home, string data, string mediaType, string? name)
    {
        if (!IsImage(mediaType)) throw new IdeaInputException($"An idea image must be one of {string.Join(", ", MediaTypes)} (got \"{mediaType}\").");
        var payload = data.AsSpan();
        var comma = payload.IndexOf(',');
        if (payload.StartsWith("data:", StringComparison.Ordinal) && comma > 0) payload = payload[(comma + 1)..];
        // Check the length before decoding: base64 inflates by a third, and this is the boundary that protects the host.
        if (payload.Length == 0) throw new IdeaInputException("The image was empty.");
        if (payload.Length / 4 * 3 > MaxBytes)
            throw new IdeaInputException($"The image is larger than {MaxBytes / (1024 * 1024)} MB — shrink it before attaching.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload.ToString()); }
        catch (FormatException) { throw new IdeaInputException("The image was not valid base64."); }
        if (bytes.Length == 0) throw new IdeaInputException("The image was empty.");

        var dir = Path.Combine(home, Dir);
        Directory.CreateDirectory(dir);
        var file = $"img-{Ids.Short(8)}.{ExtensionFor(mediaType)}";
        File.WriteAllBytes(Path.Combine(dir, file), bytes);
        return new JsonObject
        {
            ["path"] = $"{Dir}/{file}",
            ["name"] = name is { Length: > 0 } n ? n : file,
            ["mediaType"] = mediaType,
            ["bytes"] = bytes.Length,
        };
    }

    /// <summary>The idea's images, as a clean array: only references inside the images directory, at most
    /// <see cref="MaxPerIdea"/>. A hand-written or migrated document cannot make the host touch a file elsewhere.</summary>
    public static JsonArray Sanitize(JsonNode? images)
    {
        var kept = new JsonArray();
        foreach (var node in images as JsonArray ?? [])
        {
            if (kept.Count >= MaxPerIdea) break;
            if (node is not JsonObject o || !IsInside(IdeaOps.Str(o["path"]))) continue;
            kept.Add(new JsonObject
            {
                ["path"] = IdeaOps.Str(o["path"])!,
                ["name"] = IdeaOps.Str(o["name"]) ?? Path.GetFileName(IdeaOps.Str(o["path"])!),
                ["mediaType"] = IsImage(IdeaOps.Str(o["mediaType"])) ? IdeaOps.Str(o["mediaType"])! : "image/png",
                ["bytes"] = o["bytes"] is { } b && b.GetValueKind() == System.Text.Json.JsonValueKind.Number ? b.GetValue<int>() : 0,
            });
        }
        return kept;
    }

    /// <summary>The media type a stored file's extension implies, for reading an image back.</summary>
    public static string TypeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    /// <summary>Home-relative paths, with the separators Windows and the rest of the world both understand.</summary>
    private static bool IsInside([NotNullWhen(true)] string? rel) =>
        rel is { Length: > 0 } && rel.Replace('\\', '/').StartsWith(Dir + "/", StringComparison.Ordinal);

    /// <summary>Absolute path of a stored image, or null when the reference is not one of ours.</summary>
    public static string? Absolute(string home, string? rel) =>
        IsInside(rel) ? Path.Combine(home, rel.Replace('/', Path.DirectorySeparatorChar)) : null;

    public static void Delete(string home, string? rel)
    {
        if (Absolute(home, rel) is not { } path) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Delete every image an idea carried. Called when the idea itself goes, so nothing is left behind.</summary>
    public static void DeleteAll(string home, JsonObject idea)
    {
        foreach (var image in idea["images"] as JsonArray ?? []) if (image is JsonObject o) Delete(home, IdeaOps.Str(o["path"]));
    }

    /// <summary>The idea's images, as absolute paths — what the agent needs to read them.</summary>
    public static List<string> Paths(string home, JsonObject idea)
    {
        var paths = new List<string>();
        foreach (var image in idea["images"] as JsonArray ?? [])
            if (image is JsonObject o && Absolute(home, IdeaOps.Str(o["path"])) is { } p) paths.Add(p);
        return paths;
    }

    /// <summary>
    /// The prompt's image section: the paths, so whoever picks the idea up can read the screenshots. The read tool
    /// hands an image file to the model, so a path is enough — no pixels in the prompt text.
    /// </summary>
    public static void AppendTo(StringBuilder sb, string home, JsonObject idea)
    {
        var paths = Paths(home, idea);
        if (paths.Count == 0) return;
        sb.Append("\n## Images\n");
        foreach (var image in idea["images"] as JsonArray ?? [])
            if (image is JsonObject o) sb.Append("- ").Append(IdeaOps.Str(o["name"]) ?? "image").Append(" — read it: ")
              .Append(Absolute(home, IdeaOps.Str(o["path"])) ?? "").Append('\n');
        sb.Append("Read the ones that matter before you start; they are the report.\n");
    }

    internal static string Format(int bytes) => bytes.ToString("0.##", CultureInfo.InvariantCulture);
}
