using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Phone;

/// <summary>
/// Pairing and device tokens. The desktop mints a one-time code (<see cref="NewCode"/>, five minutes, one use); the phone swaps it
/// for a device token (<see cref="Redeem"/>) that it keeps as an HttpOnly cookie. Only the SHA-256 of a token is stored, so a leaked
/// database does not leak a working one. A device: <c>{ id, name, tokenHash, createdAt, lastSeenAt, lastFrom?, userAgent? }</c>.
/// </summary>
internal sealed class PhoneAuth(IPluginData data, TimeProvider time)
{
    public static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(1);
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";   // no 0/O/1/I/L: a code can be read aloud

    private readonly IDataCollection _devices = data.Collection("devices", new CollectionSpec().Text("tokenHash").Integer("createdMs"));
    private readonly Dictionary<string, DateTimeOffset> _codes = [];
    private readonly Dictionary<string, DateTimeOffset> _seen = [];

    public (string Code, DateTimeOffset Expires) NewCode()
    {
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var code = new string(chars);
        var now = time.GetUtcNow();
        lock (_codes)
        {
            foreach (var old in _codes.Where(c => c.Value <= now).Select(c => c.Key).ToList()) _codes.Remove(old);
            if (_codes.Count >= 20) _codes.Remove(_codes.OrderBy(c => c.Value).First().Key);
            _codes[code] = now + CodeLife;
        }
        return (code, now + CodeLife);
    }

    /// <summary>Spend a code for a new device: its token (shown to the phone once) and its record; null when the code is unknown, used or expired.</summary>
    public (string Token, JsonObject Device)? Redeem(string code, string name, string? from, string? userAgent)
    {
        var key = (code ?? "").Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();
        var now = time.GetUtcNow();
        lock (_codes)
        {
            if (!_codes.Remove(key, out var expires) || expires <= now) return null;
        }
        var token = "phn_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var id = "dev_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var doc = new JsonObject
        {
            ["id"] = id,
            ["name"] = Clean(name, 60) is { Length: > 0 } n ? n : "Phone",
            ["tokenHash"] = Hash(token),
            ["createdMs"] = now.ToUnixTimeMilliseconds(),
            ["createdAt"] = Iso(now),
            ["lastSeenAt"] = Iso(now),
            ["lastFrom"] = from,
            ["userAgent"] = Clean(userAgent, 200),
        };
        _devices.Put(id, doc);
        return (token, doc);
    }

    /// <summary>The device a token belongs to, or null. Its last-seen time is written at most once a minute.</summary>
    public JsonObject? Authenticate(string? token, string? from)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return null;
        var hash = Hash(token);
        var hit = _devices.Find(new DataQuery().Eq("tokenHash", hash).Take(1)).FirstOrDefault();
        if (hit is null) return null;
        // the lookup is by the index; the comparison is still constant-time on the stored value
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hit.Doc["tokenHash"]?.GetValue<string>() ?? ""), Encoding.ASCII.GetBytes(hash))) return null;
        var now = time.GetUtcNow();
        bool touch;
        lock (_seen) { touch = !_seen.TryGetValue(hit.Key, out var last) || now - last >= SeenEvery; if (touch) _seen[hit.Key] = now; }
        if (touch)
        {
            hit.Doc["lastSeenAt"] = Iso(now);
            if (from is not null) hit.Doc["lastFrom"] = from;
            _devices.Put(hit.Key, hit.Doc);
        }
        return hit.Doc;
    }

    /// <summary>The paired devices, newest first, without their token hashes.</summary>
    public IReadOnlyList<JsonObject> List() =>
        [.. _devices.Find(new DataQuery().Order("createdMs", descending: true)).Select(d => Shown(d.Doc))];

    public bool Revoke(string id) { lock (_seen) _seen.Remove(id); return _devices.Delete(id); }

    public static JsonObject Shown(JsonObject doc)
    {
        var copy = (JsonObject)doc.DeepClone();
        copy.Remove("tokenHash");
        copy.Remove("createdMs");
        return copy;
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    private static string? Clean(string? s, int max)
    {
        if (s is null) return null;
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > max ? t[..max] : t;
    }
}
