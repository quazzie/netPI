using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Tests;

/// <summary>
/// The one argument reader (NetPI.ToolArgs) the tools, hooks and guards share. Table over the union of what the old
/// copies disagreed on: a string-encoded arguments root, the key spellings, numbers and bools as strings, arrays as
/// strings — plus the schema builder every tool definition is built from.
/// </summary>
public static class ToolArgsTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static void Register(TestRunner r)
    {
        r.Add("args: a string-encoded arguments object is unwrapped (the double-encoded root)", () =>
        {
            // "{\"a\":1}" — the arguments object sent as a JSON string, the way some models do it
            Check.Equal("1", new ToolArgs(El("\"{\\\"a\\\":1}\"")).Str("a"), "string root is unwrapped");
            Check.Equal(1, new ToolArgs(El("\"{\\\"a\\\":1}\"")).Int("a"));
            Check.True(new ToolArgs(El("\"{\\\"a\\\":1}\"")).Has("a"), "unwrapped properties are found by name");
            Check.Equal(JsonValueKind.Object, new ToolArgs(El("\"{\\\"a\\\":1}\"")).Raw.ValueKind);

            // an object root is not touched; a string that does not parse is not an object to read from
            Check.Equal("x", new ToolArgs(El("{\"a\":\"x\"}")).Str("a"));
            Check.False(new ToolArgs(El("\"{bad\"")).Has("a"), "an unparseable string reads nothing");
            Check.Equal(JsonValueKind.String, new ToolArgs(El("\"{bad\"")).Raw.ValueKind);
            Check.Equal(JsonValueKind.Array, new ToolArgs(El("[1,2]")).Raw.ValueKind, "a non-object root keeps its kind in Raw");
            // a double-encoded root unwraps level by level, until it is no longer a string
            var doubleEncoded = JsonSerializer.Serialize("\"{\\\"a\\\":1}\"");
            Check.Equal("1", new ToolArgs(El(doubleEncoded)).Str("a"), "a double-encoded root is unwrapped");
        });

        r.Add("args: names match ignoring case, '_', '-' and spaces", () =>
        {
            var a = new ToolArgs(El("{\"file_path\":\"x\"}"));
            foreach (var name in new[] { "file_path", "filePath", "FilePath", "FILE_PATH", "file-path", "file path", "File-Path" })
                Check.Equal("x", a.Str(name), $"\"{name}\" names the same property");
            Check.False(new ToolArgs(El("{\"other\":\"x\"}")).Has("path"), "an unrelated name is not found");
        });

        r.Add("args: string values (numbers and bools as their JSON text, arrays as lines)", () =>
        {
            Check.Equal("5", new ToolArgs(El("{\"a\":5}")).Str("a"), "a number reads as its text");
            Check.Equal("true", new ToolArgs(El("{\"a\":true}")).Str("a"), "a boolean reads as its text");
            Check.Equal("a\nb", new ToolArgs(El("{\"a\":[\"a\",\"b\"]}")).Str("a"), "an array reads as its lines");
            Check.Equal("1\n2", new ToolArgs(El("{\"a\":[1,2]}")).Str("a"));
            Check.Equal(null, new ToolArgs(El("{\"a\":null}")).Str("a"), "null is not a value");
            Check.Equal(null, new ToolArgs(El("{}")).Str("a"), "absent is null");
        });

        r.Add("args: ints (a fraction truncates, strings parse invariant)", () =>
        {
            Check.Equal(12, new ToolArgs(El("{\"a\":12}")).Int("a"));
            Check.Equal(12, new ToolArgs(El("{\"a\":12.5}")).Int("a"), "12.5 reads as 12");
            Check.Equal(12, new ToolArgs(El("{\"a\":\"12\"}")).Int("a"), "\"12\" reads as 12");
            Check.Equal(12, new ToolArgs(El("{\"a\":\"12.5\"}")).Int("a"), "\"12.5\" reads as 12");
            Check.Equal(null, new ToolArgs(El("{\"a\":\"abc\"}")).Int("a"));
            Check.Equal(null, new ToolArgs(El("{\"a\":true}")).Int("a"));
            Check.Equal(null, new ToolArgs(El("{}")).Int("a"));
        });

        r.Add("args: doubles and booleans (\"yes\" and 1 are true, \"maybe\" is no value)", () =>
        {
            Check.Equal(1.5, new ToolArgs(El("{\"a\":1.5}")).Double("a"));
            Check.Equal(1.5, new ToolArgs(El("{\"a\":\"1.5\"}")).Double("a"));
            Check.Equal(null, new ToolArgs(El("{\"a\":\"abc\"}")).Double("a"));

            Check.True(new ToolArgs(El("{\"a\":true}")).Bool("a") == true);
            Check.True(new ToolArgs(El("{\"a\":false}")).Bool("a") == false);
            Check.True(new ToolArgs(El("{\"a\":1}")).Bool("a") == true, "1 is true");
            Check.True(new ToolArgs(El("{\"a\":0}")).Bool("a") == false, "0 is false");
            foreach (var s in new[] { "true", "yes", "1", "on", "y" })
                Check.True(new ToolArgs(El($"{{\"a\":\"{s}\"}}")).Bool("a") == true, $"\"{s}\" is true");
            foreach (var s in new[] { "false", "no", "0", "off", "n", "" })
                Check.True(new ToolArgs(El($"{{\"a\":\"{s}\"}}")).Bool("a") == false, $"\"{s}\" is false");
            Check.Equal(null, new ToolArgs(El("{\"a\":\"maybe\"}")).Bool("a"), "anything else is no value");
            Check.Equal(null, new ToolArgs(El("{}")).Bool("a"));
        });

        r.Add("args: lists (an array, a string holding one, a single value as a one-element list)", () =>
        {
            var a = new ToolArgs(El("{\"a\":[1,\"b\",true]}"));
            Check.Equal(3, a.List("a")!.Count);
            Check.Equal(JsonValueKind.Number, a.List("a")![0].ValueKind);

            var one = new ToolArgs(El("{\"a\":\"solo\"}"));
            Check.Equal("solo", one.List("a")!.Single().GetString(), "one string is a one-element list");
            var json = new ToolArgs(El("{\"a\":\"[1,2]\"}"));
            Check.Equal(2, json.List("a")!.Count, "a string holding a JSON array is parsed");
            var obj = new ToolArgs(El("{\"a\":{\"x\":1}}"));
            Check.Equal(JsonValueKind.Object, obj.List("a")!.Single().ValueKind, "one object is a one-element list");
            Check.Equal(null, new ToolArgs(El("{}")).List("a"));
        });

        r.Add("args: Parse(string) — the hooks' view of a call's raw arguments", () =>
        {
            Check.Equal(JsonValueKind.Object, ToolArgs.Parse(null).Raw.ValueKind, "no arguments is an empty object");
            Check.Equal(JsonValueKind.Object, ToolArgs.Parse("  ").Raw.ValueKind, "blank is an empty object");
            Check.False(ToolArgs.Parse(null).Has("a"));
            Check.Equal("1", ToolArgs.Parse("{\"a\":1}").Str("a"));
            Check.Equal("1", ToolArgs.Parse("\"{\\\"a\\\":1}\"").Str("a"), "a string-encoded object is unwrapped");
            Check.Equal("1", ToolArgs.Parse(JsonSerializer.Serialize("\"{\\\"a\\\":1}\"")).Str("a"), "a double-encoded root is unwrapped, as the tool sees it");
            Check.Equal(JsonValueKind.Undefined, ToolArgs.Parse("{bad").Raw.ValueKind, "input that does not parse reads nothing (and never throws)");
            Check.False(ToolArgs.Parse("{bad").Has("a"));
        });

        r.Add("args: ToolSchema builds the tool parameter definitions", () =>
        {
            var o = ToolSchema.Object(("path", ToolSchema.Str("the file"), true), ("offset", ToolSchema.Int(""), false), ("all", ToolSchema.Bool("every session"), false));
            Check.Equal("object", o["type"]?.GetValue<string>());
            Check.True(o["properties"]!["path"] is not null && o["properties"]!["offset"] is not null && o["properties"]!["all"] is not null);
            Check.Equal("the file", o["properties"]?["path"]?["description"]?.GetValue<string>());
            Check.True(o["properties"]!["offset"]!["description"] is null, "an empty description is left out");
            Check.Equal("path", (o["required"] as JsonArray)!.Single()!.GetValue<string>(), "only required properties are listed");

            var s = ToolSchema.Str("", "info", "list");
            Check.Equal("string", s["type"]?.GetValue<string>());
            Check.Equal(2, (s["enum"] as JsonArray)!.Count);
            Check.Equal("array", ToolSchema.Array("", ToolSchema.Str(""))["type"]?.GetValue<string>());
        });
    }
}
