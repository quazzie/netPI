using System.Globalization;
using System.Text.Json.Nodes;

namespace NetPI.Schedules;

/// <summary>
/// When a schedule runs: once at a moment (<c>once</c>), every N minutes (<c>every</c>), or daily at a wall-clock time in a
/// time zone (<c>daily</c>). Full cron is deliberately not here: these three cover "tonight", "every hour" and "every
/// morning", and each one's next run is a line of arithmetic a test can check.
/// </summary>
internal sealed record Cadence(string Kind, DateTimeOffset? At = null, int? Minutes = null, TimeOnly? Time = null, string? Tz = null)
{
    public const string Once = "once", Every = "every", Daily = "daily";

    /// <summary>A cadence from its JSON (<c>{ kind, at? | minutes? | time?, tz? }</c>), or a bad_request saying what is wrong.</summary>
    public static Cadence Parse(JsonNode? node, int minMinutes)
    {
        if (node is not JsonObject o) throw Bad("cadence must be an object: { kind: once|every|daily, at | minutes | time, tz? }");
        var kind = Str(o["kind"])?.Trim().ToLowerInvariant();
        switch (kind)
        {
            case Once:
                if (!DateTimeOffset.TryParse(Str(o["at"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                    throw Bad("a once cadence needs at: an ISO 8601 time (\"2026-10-04T07:30:00+02:00\")");
                return new Cadence(Once, At: at);
            case Every:
                var minutes = o["minutes"] is JsonValue v && v.TryGetValue<int>(out var m) ? m
                    : int.TryParse(Str(o["minutes"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : 0;
                if (minutes < minMinutes) throw Bad($"an every cadence needs minutes, at least {minMinutes} (schedules.minIntervalMinutes)");
                return new Cadence(Every, Minutes: minutes);
            case Daily:
                if (!TimeOnly.TryParseExact(Str(o["time"]) ?? "", ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    throw Bad("a daily cadence needs time: \"HH:mm\" (24-hour)");
                var tz = Str(o["tz"])?.Trim();
                if (!string.IsNullOrEmpty(tz)) Zone(tz);   // an unknown zone is refused now, not at the first run
                return new Cadence(Daily, Time: time, Tz: string.IsNullOrEmpty(tz) ? null : tz);
            default:
                throw Bad("cadence.kind must be once, every or daily");
        }
    }

    public JsonObject ToJson() => Kind switch
    {
        Once => new JsonObject { ["kind"] = Once, ["at"] = At!.Value.ToString("O", CultureInfo.InvariantCulture) },
        Every => new JsonObject { ["kind"] = Every, ["minutes"] = Minutes },
        _ => new JsonObject { ["kind"] = Daily, ["time"] = Time!.Value.ToString("HH:mm", CultureInfo.InvariantCulture), ["tz"] = Tz },
    };

    /// <summary>The first run of a new (or re-enabled) schedule, seen from <paramref name="now"/>.</summary>
    public DateTimeOffset First(DateTimeOffset now) => Kind switch
    {
        Once => At!.Value,
        Every => now.AddMinutes(Minutes!.Value),
        _ => NextDaily(now),
    };

    /// <summary>The run after one that happened (or was skipped) at <paramref name="now"/>; null when there is none (a once).</summary>
    public DateTimeOffset? After(DateTimeOffset now) => Kind switch
    {
        Once => null,
        Every => now.AddMinutes(Minutes!.Value),   // from now, not from the due time: a late run never stacks a backlog
        _ => NextDaily(now),
    };

    public string Describe() => Kind switch
    {
        Once => $"once at {At:yyyy-MM-dd HH:mm zzz}",
        Every => $"every {Minutes} min",
        _ => $"daily at {Time:HH\\:mm}{(Tz is null ? "" : " " + Tz)}",
    };

    /// <summary>The next <see cref="Time"/> in the zone strictly after <paramref name="now"/>; a time the clocks skip (spring forward) moves an hour on.</summary>
    private DateTimeOffset NextDaily(DateTimeOffset now)
    {
        var zone = Tz is null ? TimeZoneInfo.Local : Zone(Tz);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        for (var day = 0; day < 3; day++)
        {
            var wall = local.Date.AddDays(day) + Time!.Value.ToTimeSpan();
            if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
            var at = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
            if (at > now) return at;
        }
        return now.AddDays(1);   // unreachable: one of three days is always after now
    }

    private static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw Bad($"unknown time zone \"{id}\" (an IANA id such as \"Europe/Stockholm\", or \"UTC\")");
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString();

    private static RpcException Bad(string message) => new("bad_request", message);
}
