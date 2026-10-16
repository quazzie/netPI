using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Diagnostics;

/// <summary>
/// The day files of the log (<c>&lt;home&gt;/logs/netpi-YYYYMMDD.log</c>) as diag.logs entries, so an investigation of
/// something that happened days ago, or before the last restart, does not mean opening the files by hand: the host's own
/// <c>logs.recent</c> is a ring of the last 2000 lines of this process. A line is <c>yyyy-MM-dd HH:mm:ss.fff LVL [category]
/// message</c> (LogSink.Format); lines that do not start like that belong to the entry above them (an exception, a message
/// with line breaks).
/// </summary>
public static partial class LogFiles
{
    [GeneratedRegex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) (TRC|DBG|INF|WRN|ERR|CRT|\?\?\?) \[([^\]]*)\] ?(.*)$")]
    private static partial Regex EntryStart();

    /// <summary>The rank of a level word or tag: trace 0, debug 1, info 2, warn 3, error and critical 4.</summary>
    public static int Rank(string? level) => (level ?? "").ToLowerInvariant() switch
    {
        var l when l.StartsWith("tr", StringComparison.Ordinal) => 0,
        var l when l.StartsWith("d", StringComparison.Ordinal) => 1,
        var l when l.StartsWith("i", StringComparison.Ordinal) => 2,
        var l when l.StartsWith("w", StringComparison.Ordinal) => 3,
        _ => 4,
    };

    /// <summary>What the entries of a file must match. A null bound is no bound; <c>Before</c> is exclusive, <c>To</c> inclusive.</summary>
    public sealed record Filter(int MinRank, string? Category, string? Contains, DateTime? From, DateTime? To, DateTime? Before);

    /// <summary>The newest <paramref name="limit"/> entries that match, oldest first, reading the day files newest first and stopping
    /// when there are enough.</summary>
    public static JsonArray Read(string dir, Filter f, int limit, CancellationToken ct)
    {
        var days = Days(dir).Where(d => (f.From is null || d.Day >= DateOnly.FromDateTime(f.From.Value))
                                        && (f.To is null || d.Day <= DateOnly.FromDateTime(f.To.Value))
                                        && (f.Before is null || d.Day <= DateOnly.FromDateTime(f.Before.Value))).ToList();
        var found = new List<JsonObject>();
        foreach (var (_, path) in days)
        {
            var inFile = new Queue<JsonObject>();
            foreach (var e in Entries(path, ct))
            {
                if (!Matches(e, f)) continue;
                inFile.Enqueue(e.ToJson());
                if (inFile.Count > limit) inFile.Dequeue();
            }
            found.InsertRange(0, inFile.TakeLast(limit - found.Count));
            if (found.Count >= limit) break;
        }
        return new JsonArray([.. found.Select(e => (JsonNode?)e)]);
    }

    /// <summary>The day files, newest first.</summary>
    public static List<(DateOnly Day, string Path)> Days(string dir)
    {
        var list = new List<(DateOnly, string)>();
        if (!Directory.Exists(dir)) return list;
        foreach (var path in Directory.EnumerateFiles(dir, "netpi-*.log"))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(path)["netpi-".Length..];
            if (DateOnly.TryParseExact(stem, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) list.Add((day, path));
        }
        return [.. list.OrderByDescending(d => d.Item1)];
    }

    private static bool Matches(Entry e, Filter f) =>
        e.Rank >= f.MinRank
        && (f.From is null || e.Time >= f.From) && (f.To is null || e.Time <= f.To) && (f.Before is null || e.Time < f.Before)
        && (f.Category is null || e.Category.Contains(f.Category, StringComparison.OrdinalIgnoreCase))
        && (f.Contains is null || e.Message.Contains(f.Contains, StringComparison.OrdinalIgnoreCase));

    private sealed record Entry(DateTime Time, int Rank, string Level, string Category, string Message)
    {
        public JsonObject ToJson() => new()
        {
            ["time"] = new DateTimeOffset(Time).ToString("O", CultureInfo.InvariantCulture), ["level"] = Level, ["category"] = Category, ["message"] = Message,
        };
    }

    /// <summary>The entries of one file, oldest first. Opened the way the writer shares it, so today's file can be read while the host appends.</summary>
    private static IEnumerable<Entry> Entries(string path, CancellationToken ct)
    {
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (IOException) { yield break; }
        using var reader = new StreamReader(fs, Encoding.UTF8);
        DateTime time = default;
        string level = "", category = "";
        StringBuilder? message = null;
        var n = 0;
        while (reader.ReadLine() is { } line)
        {
            if ((++n & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            var m = EntryStart().Match(line);
            if (!m.Success)
            {
                message?.Append('\n').Append(line);
                continue;
            }
            if (message is not null) yield return new Entry(time, Rank(level), level, category, message.ToString());
            time = DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
            level = m.Groups[2].Value.ToLowerInvariant();
            category = m.Groups[3].Value;
            message = new StringBuilder(m.Groups[4].Value);
        }
        if (message is not null) yield return new Entry(time, Rank(level), level, category, message.ToString());
    }
}
