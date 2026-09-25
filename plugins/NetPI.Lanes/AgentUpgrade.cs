using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Lanes;

/// <summary>
/// Lanes become agents, once. The lanes the user set up (<c>lanes.&lt;id&gt; = { model, capacity, use, cost, budget }</c>)
/// become agents (<c>agents.&lt;id&gt;</c>, capacity → instances), plus an agent for the default model when none of them
/// runs it; the lanes and <c>lanes.pools</c> are removed, so later starts find nothing to do. A settings file without
/// either (a new install has an agent already; tests) is left alone.
/// </summary>
internal static class AgentUpgrade
{
    private static readonly HashSet<string> OldReserved = new(StringComparer.OrdinalIgnoreCase) { "pools", "budgets", "localDefaultCapacity", "cloudDefaultCapacity" };

    /// <summary>Returns the agents it set up.</summary>
    public static List<string> Run(ISettings settings)
    {
        var created = new List<string>();
        var root = settings.Snapshot();
        if (root["lanes"] is not JsonObject lanes) return created;
        var old = lanes.Where(kv => !OldReserved.Contains(kv.Key) && kv.Value is JsonObject o && Text(o["model"]) is not null)
            .Select(kv => (Id: kv.Key, Cfg: (JsonObject)kv.Value!)).ToList();
        if (old.Count == 0 && !lanes.ContainsKey("pools")) return created;

        if (root["agents"] is not JsonObject agents) root["agents"] = agents = new JsonObject();
        foreach (var (id, cfg) in old)
        {
            var agent = new JsonObject { ["model"] = Text(cfg["model"]) };
            if (cfg["capacity"] is { } capacity) agent["instances"] = capacity.DeepClone();
            foreach (var key in (string[])["use", "cost", "budget", "disabled"])
                if (cfg[key] is { } value) agent[key] = value.DeepClone();
            var name = Unique(agents, Slug(id));
            agents[name] = agent;
            created.Add(name);
            lanes.Remove(id);
        }
        // the default model gets an agent, so chats run as they did
        if (Text(root["defaultModel"]) is { } defaultModel
            && !agents.Any(kv => kv.Value is JsonObject a && string.Equals(Text(a["model"]), defaultModel, StringComparison.OrdinalIgnoreCase)))
        {
            var name = Unique(agents, Slug(defaultModel.Contains('/') ? defaultModel[(defaultModel.IndexOf('/') + 1)..] : defaultModel));
            agents[name] = new JsonObject { ["model"] = defaultModel };
            created.Add(name);
        }
        lanes.Remove("pools");
        settings.Replace(root);
        return created;
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    /// <summary>An agent id: letters, digits, '-' and '_' (a settings path segment).</summary>
    internal static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length == 0 ? "agent" : slug;
    }

    private static string Unique(JsonObject agents, string name)
    {
        var id = name;
        for (var i = 2; agents.ContainsKey(id) || LaneScheduler.Reserved.Contains(id); i++) id = $"{name}-{i}";
        return id;
    }
}
