using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Mcp;

/// <summary>Bounded local validation. Unsupported assertion keywords are refused rather than weakened.</summary>
internal static class Schema
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$ref", "$defs", "definitions", "$comment", "title", "description", "default", "examples",
        "deprecated", "readOnly", "writeOnly", "type", "enum", "const", "properties", "required", "additionalProperties",
        "patternProperties", "propertyNames", "minProperties", "maxProperties", "dependentRequired", "dependencies",
        "items", "prefixItems", "additionalItems", "minItems", "maxItems", "uniqueItems",
        "minLength", "maxLength", "pattern", "format", "contentEncoding", "contentMediaType",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "allOf", "anyOf", "oneOf", "not", "if", "then", "else"
    };
    public static void Check(JsonObject root, bool http = false)
    {
        if (root["type"] is not null && root["type"]?.GetValue<string>() != "object") throw new McpException("The root input schema must describe an object.");
        if (root.ContainsKey("x-mcp-header")) throw new McpException("x-mcp-header must annotate a reachable property, not the root.");
        Visit(root, root, 0);
        if (http) Headers(root, null);
    }
    private static void Visit(JsonNode? node, JsonObject root, int depth)
    {
        if (depth > 32) throw new McpException("Input schema nesting exceeds 32 levels.");
        if (node is JsonValue boolean && boolean.TryGetValue<bool>(out _)) return;
        if (node is not JsonObject schema) throw new McpException("Input schema must contain schema objects or booleans.");
        foreach (var key in schema.Select(p => p.Key))
            if (!Keywords.Contains(key) && !key.StartsWith("x-", StringComparison.Ordinal)) throw new McpException("Unsupported input schema keyword: " + key);
        if (schema["$schema"] is { } dialect && !new[] { "https://json-schema.org/draft/2020-12/schema", "http://json-schema.org/draft-07/schema#", "https://json-schema.org/draft-07/schema#" }.Contains(dialect.GetValue<string>()))
            throw new McpException("Unsupported JSON Schema dialect.");
        if (schema["type"] is { } type)
        {
            var types = type is JsonArray array ? array.Select(n => n!.GetValue<string>()).ToArray() : [type.GetValue<string>()];
            if (types.Length == 0 || types.Any(t => t is not ("object" or "array" or "string" or "number" or "integer" or "boolean" or "null")))
                throw new McpException("Invalid JSON Schema type.");
        }
        foreach (var key in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties" })
            if (schema[key] is { } number && (!Number(number, out var n) || !double.IsFinite(n) || (key.StartsWith("min", StringComparison.Ordinal) || key.StartsWith("max", StringComparison.Ordinal)) && key != "minimum" && key != "maximum" && (n < 0 || n != Math.Truncate(n)) || key == "multipleOf" && n <= 0))
                throw new McpException("Invalid numeric schema keyword: " + key);
        foreach (var key in new[] { "properties", "patternProperties", "$defs", "definitions", "dependentRequired", "dependencies" })
            if (schema[key] is { } map && map is not JsonObject) throw new McpException(key + " must be an object.");
        foreach (var key in new[] { "allOf", "anyOf", "oneOf", "prefixItems", "required", "enum" })
            if (schema[key] is { } array && array is not JsonArray) throw new McpException(key + " must be an array.");
        if (schema["uniqueItems"] is { } unique && (unique is not JsonValue uv || !uv.TryGetValue<bool>(out _))) throw new McpException("uniqueItems must be boolean.");
        if (schema["required"] is JsonArray required && required.Any(n => n is not JsonValue v || !v.TryGetValue<string>(out _))) throw new McpException("required must contain strings.");
        if (schema["$ref"] is { } reference)
        {
            var pointer = reference.GetValue<string>();
            if (!pointer.StartsWith("#/", StringComparison.Ordinal) || Resolve(root, pointer) is null) throw new McpException("Only resolvable local schema references are supported.");
        }
        if (schema["pattern"] is { } pattern) _ = new Regex(pattern.GetValue<string>(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        foreach (var key in new[] { "properties", "patternProperties", "$defs", "definitions" })
            if (schema[key] is JsonObject map) foreach (var (name, child) in map)
            {
                if (key == "patternProperties") _ = new Regex(name, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                Visit(child, root, depth + 1);
            }
        foreach (var key in new[] { "allOf", "anyOf", "oneOf", "prefixItems" })
            if (schema[key] is JsonArray array) foreach (var child in array) Visit(child, root, depth + 1);
        if (schema["items"] is JsonArray legacyItems) foreach (var child in legacyItems) Visit(child, root, depth + 1);
        else if (schema["items"] is { } items) Visit(items, root, depth + 1);
        foreach (var key in new[] { "additionalProperties", "additionalItems", "propertyNames", "not", "if", "then", "else" })
            if (schema[key] is { } child) Visit(child, root, depth + 1);
        if (schema["dependencies"] is JsonObject dependencies)
            foreach (var child in dependencies.Select(p => p.Value).Where(n => n is not JsonArray)) Visit(child, root, depth + 1);
    }
    public static void Validate(JsonObject root, JsonNode? value)
    {
        var error = Error(root, root, value, "$", 0);
        if (error is not null) throw new McpException("Arguments do not match the discovered schema: " + error);
    }
    private static string? Error(JsonNode? node, JsonObject root, JsonNode? value, string path, int depth)
    {
        if (depth > 64) return path + ": recursive schema limit reached.";
        if (node is JsonValue b && b.TryGetValue<bool>(out var flag)) return flag ? null : path + ": forbidden value.";
        if (node is not JsonObject schema) return path + ": invalid schema.";
        string? Eval(JsonNode? s) => Error(s, root, value, path, depth + 1);
        if (schema["$ref"] is { } reference && Eval(Resolve(root, reference.GetValue<string>())) is { } refError) return refError;
        if (schema["type"] is { } type)
        {
            var types = type is JsonArray a ? a.Select(n => n!.GetValue<string>()) : [type.GetValue<string>()];
            if (!types.Any(t => Matches(t, value))) return path + ": expected " + string.Join(" or ", types) + ", got " + Kind(value) + ".";
        }
        if (schema["enum"] is JsonArray options && !options.Any(n => JsonNode.DeepEquals(n, value))) return path + ": value is not in enum.";
        if (schema.ContainsKey("const") && !JsonNode.DeepEquals(schema["const"], value)) return path + ": value differs from const.";
        if (schema["allOf"] is JsonArray all) foreach (var n in all) if (Eval(n) is { } e) return e;
        if (schema["anyOf"] is JsonArray any && !any.Any(n => Eval(n) is null)) return path + ": no anyOf alternative matched.";
        if (schema["oneOf"] is JsonArray one && one.Count(n => Eval(n) is null) != 1) return path + ": exactly one oneOf alternative must match.";
        if (schema["not"] is { } not && Eval(not) is null) return path + ": not assertion failed.";
        if (schema["if"] is { } condition)
        {
            var branch = Eval(condition) is null ? schema["then"] : schema["else"];
            if (branch is not null && Eval(branch) is { } e) return e;
        }
        if (value is JsonObject obj)
        {
            if (Below(schema, "minProperties", obj.Count) || Above(schema, "maxProperties", obj.Count)) return path + ": property count out of range.";
            if (schema["required"] is JsonArray required)
                foreach (var name in required) if (!obj.ContainsKey(name!.GetValue<string>())) return path + ": missing " + name.GetValue<string>();
            var props = schema["properties"] as JsonObject;
            var patterns = schema["patternProperties"] as JsonObject;
            if (schema["propertyNames"] is { } nameSchema)
                foreach (var key in obj.Select(p => p.Key))
                    if (Error(nameSchema, root, JsonValue.Create(key), path + "[" + key + "]", depth + 1) is { } nameError) return nameError;
            foreach (var (key, val) in obj)
            {
                var known = false;
                if (props?.TryGetPropertyValue(key, out var ps) == true) { known = true; if (Error(ps, root, val, path + "." + key, depth + 1) is { } e) return e; }
                if (patterns is not null) foreach (var (pattern, ps2) in patterns)
                    if (Regex.IsMatch(key, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    { known = true; if (Error(ps2, root, val, path + "." + key, depth + 1) is { } e) return e; }
                if (!known && schema["additionalProperties"] is { } extra
                    && Error(extra, root, val, path + "." + key, depth + 1) is { } additional) return additional;
            }
            foreach (var key in new[] { "dependentRequired", "dependencies" })
                if (schema[key] is JsonObject deps) foreach (var (trigger, dependent) in deps)
                    if (obj.ContainsKey(trigger))
                    {
                        if (dependent is JsonArray names) { foreach (var n in names) if (!obj.ContainsKey(n!.GetValue<string>())) return path + ": missing dependent property " + n.GetValue<string>(); }
                        else if (Eval(dependent) is { } e) return e;
                    }
        }
        if (value is JsonArray array)
        {
            if (Below(schema, "minItems", array.Count) || Above(schema, "maxItems", array.Count)) return path + ": item count out of range.";
            if (schema["uniqueItems"]?.GetValue<bool>() == true)
                for (var i = 0; i < array.Count; i++) for (var j = 0; j < i; j++) if (JsonNode.DeepEquals(array[i], array[j])) return path + ": items must be unique.";
            var prefix = schema["prefixItems"] as JsonArray ?? schema["items"] as JsonArray;
            for (var i = 0; i < array.Count; i++)
            {
                var itemSchema = prefix is not null && i < prefix.Count ? prefix[i] :
                    schema["items"] is JsonArray ? schema["additionalItems"] : schema["items"];
                if (itemSchema is not null && Error(itemSchema, root, array[i], path + "[" + i + "]", depth + 1) is { } e) return e;
            }
        }
        if (value is JsonValue v && v.TryGetValue<string>(out var text))
        {
            var length = text.EnumerateRunes().Count();
            if (Below(schema, "minLength", length) || Above(schema, "maxLength", length)) return path + ": string length out of range.";
            if (schema["pattern"] is { } pattern && !Regex.IsMatch(text, pattern.GetValue<string>(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return path + ": pattern did not match.";
            // format/contentEncoding/contentMediaType are annotations in draft 2020-12.
        }
        if (Number(value, out var number))
        {
            if (Below(schema, "minimum", number) || Above(schema, "maximum", number)) return path + ": number out of range.";
            if (schema["exclusiveMinimum"] is { } emin && number <= emin.GetValue<double>()) return path + ": exclusive minimum failed.";
            if (schema["exclusiveMaximum"] is { } emax && number >= emax.GetValue<double>()) return path + ": exclusive maximum failed.";
            if (schema["multipleOf"] is { } multiple)
            {
                var divisor = multiple.GetValue<double>();
                if (divisor <= 0 || Math.Abs(number / divisor - Math.Round(number / divisor)) > 1e-9) return path + ": multipleOf failed.";
            }
        }
        return null;
    }
    // A number written by NetPI itself (a repaired argument) is a JsonValue over long/decimal, not over a JsonElement:
    // reading only TryGetValue<double> would call every such value "not a number".
    private static bool Number(JsonNode? value, out double number)
    {
        number = 0;
        if (value is not JsonValue v) return false;
        if (v.TryGetValue<double>(out number)) return double.IsFinite(number);
        if (v.TryGetValue<long>(out var integer)) { number = integer; return true; }
        if (v.TryGetValue<decimal>(out var exact)) { number = (double)exact; return double.IsFinite(number); }
        return false;
    }
    private static string Kind(JsonNode? value) => value switch
    {
        null => "null", JsonObject => "object", JsonArray => "array",
        JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
        JsonValue v when v.TryGetValue<string>(out _) => "string",
        _ => "number",
    };
    private static bool Below(JsonObject schema, string key, double value) => schema[key] is { } n && value < n.GetValue<double>();
    private static bool Above(JsonObject schema, string key, double value) => schema[key] is { } n && value > n.GetValue<double>();
    private static bool Matches(string type, JsonNode? value) => type switch
    {
        "null" => value is null, "object" => value is JsonObject, "array" => value is JsonArray,
        "string" => value is JsonValue v && v.TryGetValue<string>(out _),
        "boolean" => value is JsonValue v && v.TryGetValue<bool>(out _),
        "number" => Number(value, out _), "integer" => Number(value, out var n) && n == Math.Truncate(n),
        _ => false,
    };
    internal static JsonNode? Resolve(JsonObject root, string pointer)
    {
        JsonNode? current = root;
        foreach (var token in pointer[2..].Split('/')) current = current?[token.Replace("~1", "/").Replace("~0", "~")];
        return current;
    }
    public static JsonObject Headers(JsonObject schema, JsonObject? arguments)
    {
        var result = new JsonObject();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(JsonNode? node, JsonNode? value, bool reachable)
        {
            if (node is not JsonObject o) return;
            if (o["x-mcp-header"] is { } annotation)
            {
                var name = annotation.GetValue<string>();
                var type = o["type"]?.GetValue<string>();
                if (!reachable || !Regex.IsMatch(name, "^[!#$%&'*+.^_\x60|~a-zA-Z0-9-]+$") || !names.Add(name)
                    || type is not ("string" or "integer" or "boolean")) throw new McpException("Invalid x-mcp-header annotation.");
                if (value is not null)
                {
                    if (type == "integer" && (!Number(value, out var integer) || Math.Abs(integer) > 9007199254740991d || integer != Math.Truncate(integer)))
                        throw new McpException("Header integer is outside the safe integer range.");
                    result[name] = type == "string" ? value.GetValue<string>() : type == "integer" && Number(value, out var n)
                        ? n.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : value.ToJsonString();
                }
            }
            foreach (var (key, child) in o)
            {
                if (key == "properties" && child is JsonObject properties)
                    foreach (var (property, propertySchema) in properties) Walk(propertySchema, value is JsonObject vals ? vals[property] : null, reachable);
                else if (child is JsonObject || child is JsonArray) WalkOther(child);
            }
        }
        void WalkOther(JsonNode? node)
        {
            if (node is JsonObject o) { if (o.ContainsKey("x-mcp-header")) Walk(o, null, false); else foreach (var child in o.Select(p => p.Value)) WalkOther(child); }
            else if (node is JsonArray a) foreach (var child in a) WalkOther(child);
        }
        Walk(schema, arguments, true);
        return result;
    }
}
