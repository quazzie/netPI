using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("NetPI.Providers.Tests")]

namespace NetPI.Providers.ClaudeCli;

/// <summary>Registers the <c>claude-cli</c> provider (settings <c>providers.claudeCli</c>).</summary>
[NetPiPlugin("netpi.providers.claudecli", Name = "Claude CLI provider",
    Description = "Claude models through the installed Claude Code CLI (claude -p) on the owner's subscription, working in the session's workspace with the CLI's own tools.",
    Order = 12)]
public sealed class ClaudeCliPlugin : INetPiPlugin
{
    public const string SettingsPath = "providers.claudeCli";

    private IDisposable? _settingsSub;

    public ClaudeCliProvider? Provider { get; private set; }

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "claude-cli", Title = "Claude CLI", Group = "Models", Order = 52,
            Settings =
            [
                SettingInfo.Bool("providers.claudeCli.enabled", "Enabled", false,
                    "Runs the official `claude` CLI as a subprocess, signed in once with `claude auth login` (subscription, not an API key). The CLI works with its own tools (read, edit, shell, search) in the session's workspace; NetPI's tools are not used."),
                SettingInfo.Str("providers.claudeCli.executable", "Executable", "claude", "Name on PATH or a full path."),
                SettingInfo.List("providers.claudeCli.models", "Models", [.. ClaudeCliOptions.DefaultModels], "Ids passed to `claude --model`."),
                SettingInfo.Int("providers.claudeCli.timeoutSeconds", "Timeout", 900, "A call that runs longer is killed.", 10, 7200, "s"),
                SettingInfo.Str("providers.claudeCli.tools", "CLI tools", "default",
                    "default: every built-in tool of the CLI. none: text only. Or a comma-separated list for --tools, e.g. Read,Grep,Glob for read-only."),
                SettingInfo.Choice("providers.claudeCli.permissionMode", "Permissions", "bypassPermissions", [.. ClaudeCliOptions.PermissionModes],
                    "A print-mode call cannot ask, so bypassPermissions lets the CLI's tools run without prompts; the workspace guard of NetPI does not apply to them."),
                SettingInfo.Str("providers.claudeCli.settingSources", "Settings files", "",
                    "Which settings the CLI loads: user, project, local (comma-separated). Empty loads none; they bring hooks and permission rules."),
                SettingInfo.Bool("providers.claudeCli.connectors", "Account connectors", false, "Let the CLI load the account's MCP connectors."),
            ],
        });
        Provider = new ClaudeCliProvider(() => context.Settings.GetNode(SettingsPath) as JsonObject, context.Logger,
            sessionId => CwdOf(context, sessionId));
        context.Services.Register<IModelProvider>(Provider);
        // Enabling the provider or editing its models changes the catalog: say so, as the other providers do.
        _settingsSub = context.Events.Subscribe(EventTypes.SettingsChanged, _ =>
        {
            if (Provider is { } p && p.ModelsChangedSinceLastList())
                context.Events.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = p.Id });
        });
        return Task.CompletedTask;
    }

    /// <summary>The folder the session works in (its workspace, else its project), or null when there is none.</summary>
    private static string? CwdOf(IPluginContext context, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        try
        {
            if (context.Sessions.GetSession(sessionId) is not { } session) return null;
            var resolver = context.Services.Get<IWorkspaceResolver>();
            return SessionWorkspace.Of(session) is not null && resolver is not null ? resolver.CwdOf(session) : context.Sessions.GetCwd(session);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "claude-cli: could not resolve the working folder of {Session}", sessionId);
            return null;
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _settingsSub?.Dispose();
        _settingsSub = null;
        return Task.CompletedTask;
    }
}
