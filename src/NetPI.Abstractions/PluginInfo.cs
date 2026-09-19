namespace NetPI.Abstractions;

/// <summary>Stable metadata describing a plugin.</summary>
public sealed record PluginInfo(string Id, string Name, string Version)
{
    public override string ToString() => $"{Id} {Name} v{Version}";
}
