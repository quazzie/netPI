using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Shell;

/// <summary>
/// Shell tools: bash (Git Bash on Windows), pwsh, process_list / process_output / process_kill, and the
/// processes.* RPC methods. Settings: <c>shell.bashPath</c>, <c>shell.pwshPath</c>, <c>shell.timeoutSeconds</c>.
/// </summary>
[NetPiPlugin("netpi.tools.shell", Name = "Shell tools", Description = "bash (Git Bash on Windows), pwsh and background processes", Order = 20)]
public sealed class ShellPlugin : INetPiPlugin
{
    private ProcessRegistry? _registry;

    public static IReadOnlyList<IAgentTool> CreateTools(ShellService service) =>
    [
        new ShellTool("bash", service),
        new ShellTool("pwsh", service),
        new ProcessListTool(service.Registry),
        new ProcessOutputTool(service.Registry),
        new ProcessKillTool(service.Registry),
    ];

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        _registry = new ProcessRegistry(context.Events);
        var service = new ShellService(_registry, context.Settings);
        _ = Task.Run(service.CleanupTempFiles, CancellationToken.None);
        var hasPwsh = ShellLocator.FindPwsh(context.Settings, out _) is not null;
        foreach (var tool in CreateTools(service))
        {
            // don't offer the model a shell that doesn't exist (setting shell.pwshAlways forces registration)
            if (tool.Definition.Name == "pwsh" && !hasPwsh && !context.Settings.Get("shell.pwshAlways", false)) continue;
            context.Tools.Register(tool);
        }

        var registry = _registry;
        context.Rpc.Register("processes.list", (_, _) =>
            Task.FromResult<object?>(registry.List().Select(p => p.ToInfo()).ToList()),
            "Running and recent shell processes → ProcessInfo[]");

        context.Rpc.Register("processes.output", (req, _) =>
        {
            var id = req.Required("id");
            var p = registry.Get(id) ?? throw new RpcException("not_found", $"No process {id}");
            var tail = Math.Clamp(req.Int("tail") ?? 500, 1, 20_000);
            return Task.FromResult<object?>(p.Output.Tail(tail, 1024 * 1024));
        }, "Tail of a process's output: { id, tail? } → string");

        context.Rpc.Register("processes.kill", (req, _) =>
        {
            var id = req.Required("id");
            var p = registry.Get(id) ?? throw new RpcException("not_found", $"No process {id}");
            return Task.FromResult<object?>(p.Kill());
        }, "Kill a process tree: { id } → bool");

        var bash = ShellLocator.FindBash(context.Settings);
        var pwsh = ShellLocator.FindPwsh(context.Settings, out var legacy);
        context.Logger.LogInformation("Shell tools: bash={Bash}, pwsh={Pwsh}{Legacy}", bash ?? "(not found)", pwsh ?? "(not found)", legacy ? " (Windows PowerShell)" : "");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_registry is not null) await _registry.KillAllAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }
}
