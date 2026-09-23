using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetPI.Agent;

/// <summary>
/// The agent runtime (<see cref="IAgentRuntime"/>) and its RPC methods: <c>agent.send</c>, <c>agent.abort</c>,
/// <c>agent.queue</c>, <c>agent.dequeue</c>, <c>agents.list</c>, <c>agent.get</c>.
/// <para>Settings: <c>agent.maxTurns</c> (200), <c>agent.defaultMaxOutputTokens</c> (16384), <c>agent.maxToolResultChars</c> (60000),
/// <c>agent.parallelReadOnlyTools</c> (true), <c>agents.maxDepth</c> (3).</para>
/// </summary>
[NetPiPlugin("netpi.agent", Name = "Agent runtime", Description = "Model/tool loop, steering and follow-ups, subagents and lanes", Order = 50)]
public sealed class AgentPlugin : INetPiPlugin
{
    private AgentRuntime? _runtime;

    internal AgentRuntime? Runtime => _runtime;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var runtime = new AgentRuntime(context);
        runtime.Initialize();
        _runtime = runtime;
        context.Services.Register<IAgentRuntime>(runtime);

        context.Rpc.Register("agent.send", async (req, token) =>
        {
            var sessionId = req.Required("sessionId");
            if (context.Sessions.GetSession(sessionId) is null) throw new RpcException("not_found", $"No session {sessionId}");
            var text = req.Str("text") ?? "";
            var images = ParseImages(req.Prop("images"));
            if (string.IsNullOrWhiteSpace(text) && images is null) throw new RpcException("bad_request", "Empty message");
            var mode = ParseMode(req.Str("mode"));
            try
            {
                return await runtime.SendAsync(sessionId, new UserInput { Text = text, Images = images }, mode, token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new RpcException("unavailable", ex.Message);
            }
        }, "Send a message to a session's agent: { sessionId, text, images?, mode?: auto|steer|queue } → AgentInfo");

        context.Rpc.Register("agent.abort", async (req, _) =>
        {
            var id = req.Str("sessionId") ?? req.Required("id");
            return await runtime.AbortAsync(id).ConfigureAwait(false);
        }, "Abort the current run of a session's agent: { sessionId } → bool");

        context.Rpc.Register("agent.queue", (req, _) =>
            Task.FromResult<object?>(runtime.GetQueue(req.Required("sessionId"))),
            "Pending steering/follow-up inputs: { sessionId } → QueuedInput[]");

        context.Rpc.Register("agent.dequeue", (req, _) =>
            Task.FromResult<object?>(runtime.RemoveQueued(req.Required("sessionId"), req.Required("id"))),
            "Remove a pending input: { sessionId, id } → bool");

        context.Rpc.Register("agents.list", (req, _) =>
            Task.FromResult<object?>(runtime.List(req.Bool("includeFinished") ?? true)),
            "Agents (active and recent): { includeFinished? } → AgentInfo[]");

        context.Rpc.Register("agent.get", (req, _) =>
        {
            var id = req.Str("id");
            var sessionId = req.Str("sessionId");
            var info = id is not null ? runtime.Get(id) : sessionId is not null ? runtime.GetBySession(sessionId) : null;
            return Task.FromResult<object?>(info);
        }, "One agent: { id? , sessionId? } → AgentInfo | null");

        context.Logger.LogInformation("Agent runtime started ({Count} recent agents)", runtime.List().Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_runtime is not null) await _runtime.StopAsync(ct).ConfigureAwait(false);
    }

    internal static DeliveryMode ParseMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "steer" => DeliveryMode.Steer,
        "queue" or "followup" or "follow-up" => DeliveryMode.Queue,
        _ => DeliveryMode.Auto,
    };

    private static List<ImagePart>? ParseImages(JsonElement? images)
    {
        if (images is not { ValueKind: JsonValueKind.Array } arr) return null;
        var list = new List<ImagePart>();
        foreach (var img in arr.EnumerateArray())
        {
            if (img.ValueKind != JsonValueKind.Object) continue;
            var data = img.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (string.IsNullOrEmpty(data)) continue;
            var mediaType = img.TryGetProperty("mediaType", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            // accept data URLs too
            if (data.StartsWith("data:", StringComparison.Ordinal) && data.IndexOf(";base64,", StringComparison.Ordinal) is var idx and > 5)
            {
                mediaType ??= data[5..idx];
                data = data[(idx + 8)..];
            }
            list.Add(new ImagePart { MediaType = mediaType ?? "image/png", Data = data });
        }
        return list.Count == 0 ? null : list;
    }
}
