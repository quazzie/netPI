using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Media;

/// <summary>
/// <c>show_image</c>: show the user an image in the chat (a file, an http(s) URL or a <c>data:</c> URL) with an optional
/// caption. The image goes to the UI in the result's details only; it is not sent back to the model.
/// </summary>
[NetPiPlugin("netpi.tools.media", Name = "Media tools", Description = "show_image: the agent shows the user an image in the chat", Order = 26)]
public sealed class MediaPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "media", Title = "Images", Group = "Tools", Order = 40,
            Settings =
            [
                SettingInfo.Int("media.maxBytes", "Largest image show_image shows", 10000000, null, 1000, null, "bytes"),
            ],
        });
        var http = context.Track(new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        }) { Timeout = TimeSpan.FromSeconds(30) });
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; NetPI/0.1)");
        context.Tools.Register(new ShowImageTool(context, http));
        return Task.CompletedTask;
    }
}

internal sealed class ShowImageTool(IPluginContext ctx, HttpClient http) : IAgentTool
{
    private const long DefaultMaxBytes = 10_000_000;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".bmp"] = "image/bmp", [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon", [".avif"] = "image/avif",
    };

    public ToolDefinition Definition { get; } = new()
    {
        Name = "show_image",
        Label = "Image",
        Category = "media",
        ReadOnly = true,
        SummaryArg = "source",
        Description =
            "Show the user an image in the chat: a file (png, jpg, gif, webp, bmp, svg, ico, avif), an http(s) URL or a data: " +
            "URL, with an optional caption. Use it to present a chart, diagram, screenshot or picture, for example one you " +
            "generated. The image is shown to the user only; to look at an image yourself, use read (files) or screenshot.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["source"] = new JsonObject { ["type"] = "string", ["description"] = "File path, http(s) URL or data: URL" },
                ["caption"] = new JsonObject { ["type"] = "string", ["description"] = "A short caption shown under the image" },
            },
            ["required"] = new JsonArray("source"),
        },
        PromptGuidelines = ["Use show_image to show the user an image, chart or diagram, for example one you generated."],
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        args = Unwrap(args);
        var source = Str(args, "source", "path", "url", "file", "src", "image")?.Trim().Trim('"');
        if (string.IsNullOrEmpty(source)) return ToolResult.Error("show_image needs a source: a file path, an http(s) URL or a data: URL.");
        var caption = Str(args, "caption", "title", "alt", "description")?.Trim();
        if (caption is { Length: > 300 }) caption = caption[..300] + "…";
        var max = Math.Clamp(ctx.Settings.Get("media.maxBytes", DefaultMaxBytes), 100_000, 100_000_000);

        byte[] bytes;
        string? mediaType;
        string kind, name;
        string? path = null, url = null;
        try
        {
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                (bytes, mediaType) = ParseDataUrl(source);
                kind = "data";
                name = "image";
            }
            else if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = source;
                using var res = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) return ToolResult.Error($"HTTP {(int)res.StatusCode} for {source}");
                if (res.Content.Headers.ContentLength > max) return TooLarge(res.Content.Headers.ContentLength.Value, max);
                bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                mediaType = res.Content.Headers.ContentType?.MediaType;
                kind = "url";
                name = Uri.TryCreate(source, UriKind.Absolute, out var u) && Path.GetFileName(u.LocalPath) is { Length: > 0 } f ? f : "image";
            }
            else
            {
                path = context.ResolvePath(source);
                if (!File.Exists(path)) return ToolResult.Error($"No such file: {path}");
                var size = new FileInfo(path).Length;
                if (size > max) return TooLarge(size, max);
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                mediaType = Types.GetValueOrDefault(Path.GetExtension(path));
                kind = "file";
                name = Path.GetFileName(path);
            }
        }
        catch (HttpRequestException ex) { return ToolResult.Error($"Fetching {source} failed: {ex.Message}"); }
        catch (FormatException ex) { return ToolResult.Error($"Not a valid data: URL: {ex.Message}"); }
        catch (IOException ex) { return ToolResult.Error($"Reading {source} failed: {ex.Message}"); }

        if (bytes.Length > max) return TooLarge(bytes.Length, max);
        var sniffed = Sniff(bytes);
        mediaType = sniffed ?? (mediaType is { } t && t.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? t.ToLowerInvariant() : null);
        if (mediaType is null) return ToolResult.Error($"{name} is not an image the chat can show (png, jpg, gif, webp, bmp, svg, ico, avif).");

        var kb = Math.Max(1, (bytes.Length + 512) / 1024);
        return ToolResult.Ok($"Showed {name} ({mediaType}, {kb} KB) to the user{(string.IsNullOrEmpty(caption) ? "" : $" with the caption \"{caption}\"")}.", new
        {
            source = kind,
            path,
            url,
            name,
            mediaType,
            bytes = bytes.Length,
            caption,
            data = Convert.ToBase64String(bytes),
        });
    }

    private static ToolResult TooLarge(long size, long max) =>
        ToolResult.Error($"The image is too large to show ({size / 1_000_000.0:0.#} MB; the limit is {max / 1_000_000.0:0.#} MB, setting media.maxBytes).");

    /// <summary>The image type from the first bytes (null when it is not a known image format).</summary>
    internal static string? Sniff(byte[] b)
    {
        bool At(int offset, params byte[] sig) => b.Length >= offset + sig.Length && b.AsSpan(offset, sig.Length).SequenceEqual(sig);
        if (At(0, 0x89, 0x50, 0x4E, 0x47)) return "image/png";
        if (At(0, 0xFF, 0xD8, 0xFF)) return "image/jpeg";
        if (At(0, 0x47, 0x49, 0x46, 0x38)) return "image/gif";
        if (At(0, 0x52, 0x49, 0x46, 0x46) && At(8, 0x57, 0x45, 0x42, 0x50)) return "image/webp";
        if (At(0, 0x42, 0x4D)) return "image/bmp";
        if (At(0, 0x00, 0x00, 0x01, 0x00)) return "image/x-icon";
        if (At(4, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66)) return "image/avif";
        var head = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 1024)).TrimStart('﻿').TrimStart();
        if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
            (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
            return "image/svg+xml";
        return null;
    }

    internal static (byte[] Bytes, string? MediaType) ParseDataUrl(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0) throw new FormatException("no comma");
        var meta = url[5..comma];
        var payload = url[(comma + 1)..];
        var parts = meta.Split(';');
        var type = parts[0].Length > 0 ? parts[0] : null;
        var base64 = parts.Skip(1).Any(p => p.Equals("base64", StringComparison.OrdinalIgnoreCase));
        return (base64 ? Convert.FromBase64String(payload) : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload)), type);
    }

    private static JsonElement Unwrap(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) return e;
        try
        {
            using var doc = JsonDocument.Parse(e.GetString() ?? "{}");
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return e; }
    }

    private static string? Str(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        static string Norm(string s) => s.Replace("_", "").Replace("-", "").ToLowerInvariant();
        foreach (var name in names)
            foreach (var p in e.EnumerateObject())
                if (Norm(p.Name) == Norm(name) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
        return null;
    }
}
