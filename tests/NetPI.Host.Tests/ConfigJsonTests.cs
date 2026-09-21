using System.Text.Json;
using NetPI.Host.Config;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 P5: DeepClone must survive every JsonElement a plugin config can
/// produce — including JsonValueKind.Null (a plugin with NO section in
/// config.json resolves to a null element). A Null DeepClone crashed the
/// whole host at startup (NRE in JsonNode.Parse("null")).
/// </summary>
public sealed class ConfigJsonTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"a\":1,\"b\":{\"c\":[1,2]}}")]
    [InlineData("\"s\"")]
    public void DeepClone_SurvivesAllKinds(string json)
    {
        var clone = Parse(json).DeepClone();
        Assert.NotEqual(JsonValueKind.Undefined, clone.ValueKind);
        // detached: mutating the clone must not require the source document
        Assert.Equal(JsonSerializer.Serialize(Parse(json)),
            JsonSerializer.Serialize(JsonDocument.Parse(clone.GetRawText()).RootElement));
    }

    [Fact]
    public void GetRaw_MissingPluginSection_IsNull_NotUndefined()
    {
        var home = Path.Combine(Path.GetTempPath(), "netpi-cfg-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.json"), "{ \"plugins\": {} }");
        using var config = new ConfigService(home);
        Assert.Equal(JsonValueKind.Null, config.GetRaw("netpi.nope").ValueKind);
        try { Directory.Delete(home, recursive: true); } catch { }
    }
}
