namespace NetPI.Abstractions;

/// <summary>
/// A plugin-contributed tab for the Web UI's right-side panel. The main Svelte
/// shell owns layout/chrome; plugin content is isolated behind <see cref="EntryUrl"/>
/// so hot-reloading a plugin cannot destabilize the chat application.
/// </summary>
public sealed record WebPanelDefinition(
    string Id,
    string Title,
    string Icon,
    string EntryUrl,
    int Order = 0);

/// <summary>
/// Hot-reloadable registry for plugin-contributed Web UI panels.
/// Registrations made through <see cref="IPluginContext.WebPanels"/> are owned
/// by that plugin generation and disappear automatically on unload.
/// </summary>
public interface IWebPanelRegistry
{
    IDisposable Register(WebPanelDefinition panel);
    IReadOnlyList<WebPanelDefinition> All();
}
