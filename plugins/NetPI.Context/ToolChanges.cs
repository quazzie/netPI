using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace NetPI.Context;

/// <summary>
/// Why a session's tools changed, so a "tools" notice can say so instead of leaving the model to guess: a plugin was
/// reloaded, the user switched the tools off, a profile was switched, a setting changed, or nothing is known.
/// <para>The evidence: the session's own switches (<c>meta.toolsOff</c>), a "profile" notice just before the call, the
/// recent <c>plugins.reloaded</c> events, and the last known owner of every tool (a reloaded plugin's tools are already
/// gone when the notice is written, so the owner is remembered from the call before).</para>
/// </summary>
internal sealed class ToolChanges(IPluginContext ctx)
{
    /// <summary>The kinds of a tool-set change, as they appear in a notice's <c>cause</c> meta and in <c>context.toolsets</c>.</summary>
    public const string PluginReload = "plugin-reload";
    public const string Profile = "profile";
    public const string User = "user";
    public const string Settings = "settings";
    public const string Unknown = "unknown";

    /// <summary>A profile switch announces itself in the chat before the call that sees its tools (the profiles plugin).</summary>
    public const string ProfileNotice = "profile";

    /// <summary>How long back a plugin reload still explains a change noticed now.</summary>
    public static readonly TimeSpan ReloadWindow = TimeSpan.FromMinutes(10);
    /// <summary>
    /// A notice is written at the <em>next</em> model call, which on a long turn or a slow model can be a while after the
    /// reload, so the cause looks further back than <see cref="ReloadWindow"/> shows.
    /// </summary>
    public static readonly TimeSpan CauseWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);
    private const int Capacity = 50;

    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _owners = new(StringComparer.Ordinal);
    private readonly LinkedList<Reload> _reloads = new();
    private readonly object _gate = new();

    /// <summary>One <c>plugins.reloaded</c> event: which plugins, when, and what the host was doing to them.</summary>
    public sealed record Reload(IReadOnlyList<string> Ids, DateTimeOffset Time, string Kind);

    /// <summary>What to tell the model, and what the tool-set history keeps: a kind, the plugins behind it, one clause.</summary>
    public sealed record Change(string Cause, IReadOnlyList<string> Plugins, string Clause)
    {
        public static Change Reloaded(IReadOnlyList<string> ids) =>
            new(ToolChanges.PluginReload, ids, "plugin reload " + string.Join(", ", ids));
        public static readonly Change User = new(ToolChanges.User, [], "the user switched it off for this session");
        public static readonly Change Profile = new(ToolChanges.Profile, [], "the user switched this chat's profile");
        public static Change PluginsOff(IReadOnlyList<string> ids, string kind = "disabled") =>
            new(ToolChanges.Settings, ids, $"{(ids.Count == 1 ? "plugin" : "plugins")} {string.Join(", ", ids)} switched {(kind == "enabled" ? "on" : "off")}");
        public static Change Setting(string name) => new(ToolChanges.Settings, [], "the setting " + name + " changed");
        public static readonly Change Unknown = new(ToolChanges.Unknown, [], "");
    }

    /// <summary><c>plugins.reloaded</c>: remember it, for the next notice, the tool-set history and the diagnostics tab.</summary>
    public void OnPluginsReloaded(BusEvent e)
    {
        var d = e.As<JsonObject>();
        var ids = d?["ids"] is JsonArray a ? a.Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? [] : [];
        if (ids.Count == 0) return;
        lock (_gate)
        {
            _reloads.AddFirst(new Reload(ids, e.Time, d?["kind"]?.GetValue<string>() ?? "reload"));
            while (_reloads.Count > Capacity) _reloads.RemoveLast();
            while (_reloads.Last is { } old && DateTimeOffset.UtcNow - old.Value.Time > Keep) _reloads.RemoveLast();
        }
    }

    /// <summary>The reloads in the last <paramref name="window"/> (default <see cref="ReloadWindow"/>), newest first.</summary>
    public IReadOnlyList<Reload> Reloads(TimeSpan? window = null)
    {
        var since = DateTimeOffset.UtcNow - (window ?? ReloadWindow);
        lock (_gate) return [.. _reloads.Where(r => r.Time >= since)];
    }

    /// <summary>Remember which plugin each of the session's tools belongs to, so a tool that vanishes can be named.</summary>
    public void Remember(string sessionId, IReadOnlyList<ToolDefinition> tools)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in ctx.Tools.Registrations)
        {
            var name = r.Tool.Definition.Name;
            if (!tools.Any(t => t.Name == name)) continue;  // only what this session is sent
            if (!ReferenceEquals(ctx.Tools.Get(name), r.Tool)) continue;  // not the registration that wins
            owners[name] = r.PluginId;
        }
        lock (_owners)
            _owners.AddOrUpdate(sessionId, _ => owners, (_, old) => { foreach (var kv in owners) old[kv.Key] = kv.Value; return old; });
    }

    /// <summary>The plugin a tool belonged to the last time it was here (empty when unknown).</summary>
    public string Owner(string sessionId, string tool)
    {
        lock (_owners)
            if (_owners.TryGetValue(sessionId, out var owners) && owners.TryGetValue(tool, out var known) && known.Length > 0)
                return known;
        return ctx.Tools.Registrations.FirstOrDefault(r => r.Tool.Definition.Name == tool)?.PluginId ?? "";
    }

    /// <summary>A deleted session's tool owners are of no use to anyone.</summary>
    public void Forget(string sessionId) => _owners.TryRemove(sessionId, out _);

    /// <summary>
    /// The cause of a change: the user's own switches first, then a profile switch, then the plugin that was reloaded
    /// behind the tools that went away, then a setting. <paramref name="context"/> is the session's messages as the model has them.
    /// </summary>
    public Change Cause(string sessionId, IReadOnlyList<string> added, IReadOnlyList<string> removed,
        IReadOnlySet<string> switchedOff, IReadOnlyList<ChatMessage> context)
    {
        if (removed.Count > 0 && removed.All(n => switchedOff.Contains(n))) return Change.User;
        // the profiles plugin appends its notice right before the call that sees the profile's tools
        if (context.LastOrDefault(m => m.Role == MessageRole.Notice) is { } notice && notice.MetaString("kind") == ProfileNotice)
            return Change.Profile;

        var ids = added.Concat(removed)
            .Select(n => Owner(sessionId, n)).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        foreach (var reload in Reloads(CauseWindow))
        {
            var hit = reload.Ids.Where(id => ids.Contains(id, StringComparer.Ordinal)).ToList();
            if (hit.Count > 0) return reload.Kind == "reload" ? Change.Reloaded(hit) : Change.PluginsOff(hit, reload.Kind);
        }

        List<string> disabled = [];
        try { disabled = ctx.Settings.Get<List<string>>("tools.disabled") ?? []; } catch { }
        if (removed.Any(n => disabled.Contains(n, StringComparer.Ordinal))) return Change.Setting("tools.disabled");
        return Change.Unknown;
    }
}
