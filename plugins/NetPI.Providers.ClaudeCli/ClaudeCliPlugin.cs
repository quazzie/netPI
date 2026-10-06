using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

[assembly: InternalsVisibleTo("NetPI.Providers.Tests")]

namespace NetPI.Providers.ClaudeCli;

/// <summary>Registers the <c>claude-cli</c> provider (settings <c>providers.claudeCli</c>).</summary>
[NetPiPlugin("netpi.providers.claudecli", Name = "Claude CLI provider",
    Description = "Claude models through the installed Claude Code CLI (claude -p) on the owner's subscription: text in, text out, no tools.",
    Order = 12)]
public sealed class ClaudeCliPlugin : INetPiPlugin
{
    public const string SettingsPath = "providers.claudeCli";

    public ClaudeCliProvider? Provider { get; private set; }

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "claude-cli", Title = "Claude CLI", Group = "Models", Order = 52,
            Settings =
            [
                SettingInfo.Bool("providers.claudeCli.enabled", "Enabled", false,
                    "Runs the official `claude` CLI as a subprocess, signed in once with `claude auth login` (subscription, not an API key). Text in, text out: the model gets no tools, so use it for planner and designer agents with tools off."),
                SettingInfo.Str("providers.claudeCli.executable", "Executable", "claude", "Name on PATH or a full path."),
                SettingInfo.List("providers.claudeCli.models", "Models", [.. ClaudeCliOptions.DefaultModels], "Ids passed to `claude --model`."),
                SettingInfo.Int("providers.claudeCli.timeoutSeconds", "Timeout", 900, "A call that runs longer is killed.", 10, 7200, "s"),
            ],
        });
        Provider = new ClaudeCliProvider(() => context.Settings.GetNode(SettingsPath) as JsonObject, context.Logger);
        context.Services.Register<IModelProvider>(Provider);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
