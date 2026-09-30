using System.Text;

namespace NetPI.Tools.Files;

public enum EolStyle { Lf, CrLf }

/// <summary>Line-break statistics of a text.</summary>
public readonly record struct EolStats(int CrLf, int Lf, int Cr)
{
    public int Total => CrLf + Lf + Cr;
    public bool Mixed => (CrLf > 0 ? 1 : 0) + (Lf > 0 ? 1 : 0) + (Cr > 0 ? 1 : 0) > 1;
    /// <summary>CRLF when the (strict) majority of line breaks are CRLF, LF otherwise; null when there are no line breaks.</summary>
    public EolStyle? Style => Total == 0 ? null : CrLf > Lf + Cr ? EolStyle.CrLf : EolStyle.Lf;
}

/// <summary>A decoded text file: <see cref="Text"/> is always LF-normalized.</summary>
public sealed class TextDocument
{
    /// <summary>Content with every line break normalized to \n.</summary>
    public required string Text { get; set; }
    /// <summary>Detected line-break style (null when the file has no line breaks).</summary>
    public EolStyle? DetectedEol { get; init; }
    public EolStats Stats { get; init; }
    public bool Bom { get; init; }
    public required Encoding Encoding { get; init; }
    /// <summary>The file was not valid UTF-8 and was decoded as Latin-1 (bytes round-trip unchanged).</summary>
    public bool Legacy { get; init; }
    public long ByteLength { get; init; }
}

/// <summary>EOL / BOM / encoding helpers. All text tools work on LF-normalized text and write back with the original style.</summary>
public static class TextCodec
{
    public const int BinarySniffBytes = 8192;

    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    public static bool IsImagePath(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static string ImageMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>Byte-order mark length and encoding (UTF-8, UTF-16 LE/BE), or (0, null).</summary>
    public static (int Length, Encoding? Encoding) DetectBom(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return (3, Utf8NoBom);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return (2, new UnicodeEncoding(bigEndian: false, byteOrderMark: false));
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return (2, new UnicodeEncoding(bigEndian: true, byteOrderMark: false));
        return (0, null);
    }

    /// <summary>Binary = a NUL byte within the first 8KB (UTF-16 files with a BOM are text).</summary>
    public static bool IsBinary(ReadOnlySpan<byte> data)
    {
        var (bomLen, enc) = DetectBom(data);
        if (bomLen > 0 && enc is UnicodeEncoding) return false;
        var sniff = data[..Math.Min(data.Length, BinarySniffBytes)];
        return sniff.IndexOf((byte)0) >= 0;
    }

    /// <summary>Check a file on disk for binary content (reads the first 8KB only).</summary>
    public static bool IsBinaryFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        Span<byte> buf = stackalloc byte[BinarySniffBytes];
        var total = 0;
        while (total < buf.Length)
        {
            var n = fs.Read(buf[total..]);
            if (n == 0) break;
            total += n;
        }
        return IsBinary(buf[..total]);
    }

    public static EolStats CountEol(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        var span = text.AsSpan();
        var i = span.IndexOfAny('\r', '\n');
        while (i >= 0)
        {
            if (span[i] == '\r')
            {
                if (i + 1 < span.Length && span[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else lf++;
            var next = span[(i + 1)..].IndexOfAny('\r', '\n');
            i = next < 0 ? -1 : i + 1 + next;
        }
        return new EolStats(crlf, lf, cr);
    }

    public static EolStyle? DetectEol(string text) => CountEol(text).Style;

    /// <summary>Convert \r\n and lone \r to \n.</summary>
    public static string NormalizeToLf(string text)
    {
        if (text.IndexOf('\r') < 0) return text;
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>Convert LF-normalized (or any) text to the given line-break style.</summary>
    public static string ToEol(string text, EolStyle eol)
    {
        var lf = NormalizeToLf(text);
        return eol == EolStyle.CrLf ? lf.Replace("\n", "\r\n") : lf;
    }

    public static string EolName(EolStyle? eol) => eol == EolStyle.CrLf ? "crlf" : "lf";

    public static EolStyle? ParseEol(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "lf" or "\n" or "unix" => EolStyle.Lf,
        "crlf" or "\r\n" or "windows" => EolStyle.CrLf,
        "auto" or "native" or "os" => OperatingSystem.IsWindows() ? EolStyle.CrLf : EolStyle.Lf,
        _ => null,
    };

    /// <summary>
    /// The encoding a file is in, decided from a <b>sample</b> of its first bytes (a page of a huge file cannot hold the
    /// whole thing). Same rules as <see cref="Decode"/>, with one difference that only a sample needs: a sample can end
    /// in the middle of a multi-byte character, and a truncated tail is not a reason to call the file Latin-1. So the
    /// last few bytes are dropped until the strict UTF-8 decode succeeds, and only a failure that survives that is
    /// legacy. <paramref name="legacy"/> says whether the file round-trips as Latin-1, exactly as in
    /// <see cref="TextDocument.Legacy"/>.
    /// </summary>
    public static (Encoding Encoding, bool Legacy, int BomLength) DetectEncoding(ReadOnlySpan<byte> sample)
    {
        var (bomLen, bomEnc) = DetectBom(sample);
        if (bomLen > 0 && bomEnc is not null) return (bomEnc, Legacy: false, bomLen);
        for (var trim = 0; trim <= 3 && sample.Length > trim; trim++)
        {
            try
            {
                _ = Utf8Strict.GetString(sample[..^trim]);
                return (Utf8NoBom, Legacy: false, 0);
            }
            catch (DecoderFallbackException) { /* a bad byte, or a character the sample cut in half */ }
        }
        return (Encoding.Latin1, Legacy: true, 0);
    }

    /// <summary>Decode bytes: BOM-aware (UTF-8/UTF-16), strict UTF-8 with Latin-1 fallback, LF-normalized.</summary>
    public static TextDocument Decode(byte[] bytes)
    {
        var (bomLen, bomEnc) = DetectBom(bytes);
        string raw;
        Encoding enc;
        var legacy = false;
        if (bomEnc is not null)
        {
            enc = bomEnc;
            raw = enc.GetString(bytes, bomLen, bytes.Length - bomLen);
        }
        else
        {
            try
            {
                raw = Utf8Strict.GetString(bytes);
                enc = Utf8NoBom;
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8 (e.g. a Windows-1252 file): Latin-1 maps every byte to a char, so unchanged text round-trips byte-for-byte.
                raw = Encoding.Latin1.GetString(bytes);
                enc = Encoding.Latin1;
                legacy = true;
            }
        }
        var stats = CountEol(raw);
        return new TextDocument
        {
            Text = NormalizeToLf(raw),
            DetectedEol = stats.Style,
            Stats = stats,
            Bom = bomLen > 0,
            Encoding = enc,
            Legacy = legacy,
            ByteLength = bytes.Length,
        };
    }

    public static TextDocument Load(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>Encode LF text with the given EOL style, BOM and encoding. Latin-1 falls back to UTF-8 when the text is not representable.</summary>
    public static byte[] Encode(string text, EolStyle eol, bool bom, Encoding? encoding = null)
    {
        var converted = ToEol(text, eol);
        encoding ??= Utf8NoBom;
        if (encoding.CodePage == Encoding.Latin1.CodePage && converted.Any(c => c > 0xFF))
            encoding = Utf8NoBom;
        byte[] preamble = [];
        if (bom)
        {
            preamble = encoding switch
            {
                UnicodeEncoding { CodePage: 1201 } => [0xFE, 0xFF],
                UnicodeEncoding => [0xFF, 0xFE],
                _ when encoding.CodePage == Encoding.UTF8.CodePage => [0xEF, 0xBB, 0xBF],
                _ => [],
            };
        }
        var body = encoding.GetBytes(converted);
        if (preamble.Length == 0) return body;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>Encode a document's (possibly modified) text back with its original EOL style, BOM and encoding.</summary>
    public static byte[] Encode(TextDocument doc, EolStyle fallbackEol) =>
        Encode(doc.Text, doc.DetectedEol ?? fallbackEol, doc.Bom, doc.Encoding);

    /// <summary>Number of lines of an LF-normalized text (a trailing newline does not start a new line).</summary>
    public static int CountLines(string lfText)
    {
        if (lfText.Length == 0) return 0;
        var n = lfText.AsSpan().Count('\n');
        return lfText[^1] == '\n' ? n : n + 1;
    }

    /// <summary>Split LF-normalized text into lines (without the empty element after a trailing newline).</summary>
    public static string[] SplitLines(string lfText)
    {
        if (lfText.Length == 0) return [];
        var lines = lfText.Split('\n');
        return lfText[^1] == '\n' ? lines[..^1] : lines;
    }

    /// <summary>
    /// Write bytes atomically: write a temp file next to the target, then rename over it.
    /// Symlinks are written through (the link target is replaced), unix file modes are preserved,
    /// and when the rename is refused (e.g. the file is locked by an editor on Windows) it falls back to an in-place write.
    /// </summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        var target = path;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.LinkTarget is not null && fi.ResolveLinkTarget(returnFinalTarget: true) is { } resolved)
                target = resolved.FullName;
        }
        catch { /* broken link: write the path itself */ }

        var dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        Directory.CreateDirectory(dir);
        var exists = File.Exists(target);
        if (exists && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException($"{target} is read-only.");
        if (exists && OperatingSystem.IsWindows() && (File.GetAttributes(target) & (FileAttributes.Hidden | FileAttributes.System)) != 0)
        {
            // Replacing hidden/system files by rename is refused on Windows (and would drop the attributes): write in place.
            using var inPlace = new FileStream(target, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite);
            inPlace.Write(data);
            return;
        }

        var tmp = Path.Combine(dir, $".{Path.GetFileName(target)}.netpi-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.None))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }
            if (exists && !OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(tmp, File.GetUnixFileMode(target)); } catch { }
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, target, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 4)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && exists)
                {
                    // Rename refused (file held open without FILE_SHARE_DELETE): overwrite in place instead.
                    using var fs = new FileStream(target, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite);
                    fs.Write(data);
                    return;
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
