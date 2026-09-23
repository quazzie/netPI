using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPI;

/// <summary>Shared JSON conventions (camelCase, string enums, nulls omitted).</summary>
public static class NetPiJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    public static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            AllowOutOfOrderMetadataProperties = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    // Serializer metadata for types that live in collectible (plugin) load contexts must not be cached in the
    // static options above, or the plugin could never be unloaded. Such types use this instance instead,
    // which the host replaces whenever a plugin is unloaded.
    private static volatile JsonSerializerOptions _collectible = Create(indented: false);

    /// <summary>Options instance for plugin-defined types. Reset by the host on plugin unload.</summary>
    public static JsonSerializerOptions Collectible => _collectible;

    /// <summary>Called by the host after a plugin unload so cached metadata of unloaded types is released.</summary>
    public static void ResetCollectibleCache() => _collectible = Create(indented: false);

    /// <summary>The right options for a runtime type (plugin types → <see cref="Collectible"/>).</summary>
    public static JsonSerializerOptions For(Type type) => type.IsCollectible ? _collectible : Options;

    public static JsonSerializerOptions For(object? value) => value is null ? Options : For(value.GetType());

    public static string Serialize<T>(T value, bool indented = false)
    {
        if (value is null) return "null";
        var t = value.GetType();
        if (t.IsCollectible) return JsonSerializer.Serialize(value, t, _collectible);
        return JsonSerializer.Serialize(value, t, indented ? Indented : Options);
    }

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, For(typeof(T)));

    /// <summary>Serialize any value (plugin types included) to a JsonElement safely.</summary>
    public static JsonElement ToElement(object? value) =>
        value is null ? default : JsonSerializer.SerializeToElement(value, value.GetType(), For(value));

    /// <summary>Serialize any value to a JsonNode safely (null for null).</summary>
    public static System.Text.Json.Nodes.JsonNode? ToNode(object? value) =>
        value switch
        {
            null => null,
            System.Text.Json.Nodes.JsonNode n => n.DeepClone(),
            JsonElement e => System.Text.Json.Nodes.JsonNode.Parse(e.GetRawText()),
            _ => JsonSerializer.SerializeToNode(value, value.GetType(), For(value)),
        };
}

/// <summary>Short, time-sortable ids: <c>prefix_</c> + 8 chars base36 millis + 4 random chars.</summary>
public static class Ids
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    public static string New(string prefix)
    {
        Span<char> buf = stackalloc char[12];
        var ms = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 7; i >= 0; i--) { buf[i] = Alphabet[(int)(ms % 36)]; ms /= 36; }
        Span<byte> rnd = stackalloc byte[4];
        RandomNumberGenerator.Fill(rnd);
        for (var i = 0; i < 4; i++) buf[8 + i] = Alphabet[rnd[i] % 36];
        return prefix + "_" + new string(buf);
    }

    public static string Short(int length = 8)
    {
        Span<byte> rnd = stackalloc byte[length];
        RandomNumberGenerator.Fill(rnd);
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[rnd[i] % 36];
        return new string(chars);
    }
}
