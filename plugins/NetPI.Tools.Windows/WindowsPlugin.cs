using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Windows;

/// <summary>
/// Windows tools: <c>windows</c>, native Windows apps through UI Automation (a helper process,
/// <c>src/NetPI.WindowsAgent</c>). Windows only: on another OS the plugin registers nothing.
/// </summary>
[NetPiPlugin("netpi.tools.windows", Name = "Windows tools", Description = "windows: read and use native Windows apps through UI Automation", Order = 27)]
public sealed class WindowsPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            context.Logger.LogInformation("windows: not on Windows, no tool");
            return Task.CompletedTask;
        }
        context.Services.Register(new SettingsSection
        {
            Id = "windows", Title = "Windows apps", Group = "Tools", Order = 35,
            Settings =
            [
                SettingInfo.Int("windows.maxControls", "Controls listed per window", 300, "The ones nearest the visible part; find, and snapshot with all, reach the rest.", 50, 5000),
                SettingInfo.Bool("windows.journal", "Keep a journal of the steps", true, "Each step (the action, the controls, the result) as a JSON line in <home>/windows/: data to train a smaller control picker on."),
            ],
        });
        var agent = new WindowsAgent(context);
        context.Tools.Register(new WindowsTool(context, agent));
        context.Stopping.Register(() =>
        {
            try { agent.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { context.Logger.LogDebug(ex, "stopping the UI Automation helper failed"); }
        });
        return Task.CompletedTask;
    }
}
