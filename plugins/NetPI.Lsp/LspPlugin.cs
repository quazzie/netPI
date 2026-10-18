using Microsoft.Extensions.Logging;

namespace NetPI.Lsp;

/// <summary>
/// Language servers for code work (idea-fe26oh): the <c>lsp</c> tool asks csharp-ls (C#), svelte-language-server (.svelte)
/// typescript-language-server (JavaScript/TypeScript) and gopls (Go, installed by the user) for diagnostics, definitions and symbols. The servers are
/// downloaded on first use into <c>&lt;home&gt;/lsp/servers</c>, pinned and checked (plugin.json names the downloads), started
/// lazily per workspace root, at most <c>lsp.maxServers</c> at once, and stopped after <c>lsp.idleMinutes</c> unused.
/// </summary>
[NetPiPlugin("netpi.lsp", Name = "Language servers", Description = "lsp: diagnostics, definitions and symbols from language servers (C#, Svelte, JavaScript/TypeScript, Go)", Order = 27)]
public sealed class LspPlugin : INetPiPlugin
{
    private LspPool? _pool;
    private CancellationTokenSource? _sweep;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "lsp", Title = "Language servers", Group = "Tools", Order = 70,
            Settings =
            [
                SettingInfo.Int("lsp.maxServers", "Servers at once", 3,
                    "How many language servers run at once (one per language and workspace); one more stops the least recently used. Each holds a project in memory (csharp-ls: several hundred MB).", 1, 8),
                SettingInfo.Int("lsp.idleMinutes", "Idle stop", 15, "A server nobody asked for this long is stopped; the next request starts it again.", 1, 240, "min"),
            ],
        });
        var http = context.Track(new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
        // the plugin's real folder (it runs from a shadow copy), where servers/js holds the npm lockfile the JS servers install from
        var installer = new LspInstaller(Path.Combine(context.Paths.Home, "lsp", "servers"), context.PluginDirectory, http, context.Logger);
        var pool = new LspPool(installer, context.Logger,
            () => Math.Clamp(context.Settings.Get("lsp.maxServers", 3), 1, 8),
            () => TimeSpan.FromMinutes(Math.Clamp(context.Settings.Get("lsp.idleMinutes", 15), 1, 240)));
        _pool = pool;
        context.Tools.Register(new LspTool(pool, context.Rpc));
        _sweep = CancellationTokenSource.CreateLinkedTokenSource(context.Stopping);
        var token = _sweep.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            try { while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) await pool.SweepAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { context.Logger.LogWarning(ex, "lsp: the idle sweep stopped"); }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_sweep is { } sweep)
        {
            sweep.Cancel();
            sweep.Dispose();
            _sweep = null;
        }
        if (_pool is { } pool) await pool.DisposeAsync().ConfigureAwait(false);
        _pool = null;
    }
}
