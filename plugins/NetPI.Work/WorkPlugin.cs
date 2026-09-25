using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Work;

/// <summary>
/// "Work" tab: one RPC (<c>work.snapshot</c>) that aggregates the agents, runs, processes and usage from the plugins that
/// provide them. Each part is optional: a missing or failing method yields null (and an entry in <c>errors</c>).
/// </summary>
[NetPiPlugin("netpi.work", Name = "Work", Description = "Overview of the agents, runs, processes and usage", Order = 80)]
public sealed class WorkPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var rpc = context.Rpc;
        var logger = context.Logger;
        context.Rpc.Register("work.snapshot", async (_, rpcCt) => await SnapshotAsync(rpc, logger, rpcCt).ConfigureAwait(false),
            "Aggregated overview for the Work tab → { agents, runs, processes, usage, time, errors? }");
        context.Ui.AddTab(new UiTabInfo { Id = "work", Title = "Work", Panel = UiPanel.Right, Icon = "work", Order = 10, Module = "ui.js" });
        return Task.CompletedTask;
    }

    /// <summary>Parts of the snapshot: result key → (RPC method, params).</summary>
    public static readonly (string Key, string Method, object? Params)[] Parts =
    [
        ("agents", "agents.list", null),
        ("runs", "runs.list", new JsonObject { ["includeFinished"] = true }),
        ("processes", "processes.list", null),
        ("usage", "usage.summary", null),
    ];

    public static async Task<JsonObject> SnapshotAsync(IRpcRegistry rpc, ILogger? logger, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var tasks = Parts.Select(async p =>
        {
            if (!rpc.Exists(p.Method)) return (p.Key, (JsonNode?)null, (string?)null);
            try
            {
                var parameters = p.Params is JsonNode n ? n.DeepClone() : null;
                var result = await rpc.InvokeAsync(p.Method, parameters, timeout.Token).ConfigureAwait(false);
                return (p.Key, NetPiJson.ToNode(result), (string?)null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "work.snapshot: {Method} failed", p.Method);
                return (p.Key, (JsonNode?)null, (string?)$"{p.Method}: {ex.Message}");
            }
        }).ToList();
        var parts = await Task.WhenAll(tasks).ConfigureAwait(false);

        var snapshot = new JsonObject();
        JsonObject? errors = null;
        foreach (var (key, value, error) in parts)
        {
            snapshot[key] = value;
            if (error is not null) (errors ??= [])[key] = error;
        }
        snapshot["time"] = DateTimeOffset.UtcNow.ToString("O");
        if (errors is not null) snapshot["errors"] = errors;
        return snapshot;
    }
}
