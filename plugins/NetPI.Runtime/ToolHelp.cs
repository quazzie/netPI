using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Runtime;

/// <summary>
/// <c>{"help": true}</c> on any tool: the runtime answers with the tool's manual (<see cref="ToolDefinition.Help"/>) instead
/// of running it, so tool descriptions can stay short in every request and the details cost nothing until asked for.
/// </summary>
internal static class ToolHelp
{
    /// <summary>The call asks for help: <c>help</c> is true, and the tool has no <c>help</c> argument of its own.</summary>
    public static bool Asked(ToolDefinition def, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return false;
        if (def.Parameters["properties"] is JsonObject props && props.ContainsKey("help")) return false;
        foreach (var p in args.EnumerateObject())
            if (p.Name.Equals("help", StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.True || (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()?.Trim().ToLowerInvariant() is "true" or "yes" or "1");
        return false;
    }

    public static string Text(ToolDefinition def)
    {
        var sb = new StringBuilder($"{def.Name}: {def.Description.Trim()}");
        if (!string.IsNullOrWhiteSpace(def.Help)) sb.Append("\n\n").Append(def.Help.Trim());
        sb.Append("\n\nArguments (JSON schema): ").Append(def.Parameters.ToJsonString());
        return sb.ToString();
    }
}
