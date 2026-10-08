using System.Text.Json.Nodes;

namespace NetPI.Profiles;

/// <summary>
/// Profiles: named instructions and tools for chats (settings <c>profiles.&lt;id&gt; = { name, prompt, toolsOff }</c> and
/// <c>profiles.defaultProfile</c>). A new chat gets its project's default profile (<c>project.meta.profile</c>, or
/// <c>"none"</c>) or else the global default. Applying a profile sets the chat's <c>meta.profile</c>, <c>meta.identity</c>
/// (the opening of its system prompt, rendered by the context plugin) and <c>meta.toolsOff</c> (its tool switches).
/// Before the first message that is free; in a started chat the profile switch invalidates the chat's prompt revision, so
/// the prompt is rendered again at the next model call (one full re-read; no <c>context.reset</c> call is needed) and a
/// <c>profile</c> notice tells the model. Subagents do not get profiles: their owner chooses their tools.
/// Chats the app starts by itself call <c>profiles.applyAuto</c>, which uses <c>profiles.autoProfile</c> in place of the global default.
/// <para>RPC: <c>profiles.list</c>, <c>profiles.apply { sessionId, profile }</c>, <c>profiles.applyAuto { sessionId, toolsOff? }</c>.</para>
/// </summary>
[NetPiPlugin("netpi.profiles", Name = "Profiles", Description = "Named instructions and tools for chats, with a default per project", Order = 45)]
public sealed class ProfilesPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var profiles = new ProfileService(context);
        context.Services.Register<IAgentHook>(new ProfileHook(profiles));
        // a new chat shows its profile before the first message; the hook makes sure it is there before the first call
        context.Events.Subscribe(EventTypes.SessionCreated, e =>
        {
            if (e.As<JsonObject>()?["session"]?["id"]?.GetValue<string>() is { Length: > 0 } id) profiles.EnsureDefault(id);
        });

        context.Rpc.Register(new RpcMethod("profiles.list", "The profiles: → { defaultProfile, profiles: { id, name, prompt, toolsOff }[] }", ReadOnly: true, Params: []),
            (_, _) => Task.FromResult<object?>(profiles.ListJson()));
        context.Rpc.Register(new RpcMethod("profiles.apply", "Give a chat a profile (null = none): { sessionId, profile } → SessionInfo. In a started chat the system prompt is rendered again at the next model call (one full re-read).", ReadOnly: false, Params: [RpcParam.Req("sessionId"), RpcParam.Opt("profile")]),
            async (req, token) =>
        {
            var sessionId = req.Required("sessionId");
            var id = req.Str("profile");
            return await profiles.ApplyAsync(sessionId, string.IsNullOrWhiteSpace(id) || id == ProfileService.None ? null : id, token)
                .ConfigureAwait(false);
        });
        context.Rpc.Register(new RpcMethod("profiles.applyAuto", "Give a chat the app starts by itself (a plan carried out, idea work, a schedule) the automatic profile (settings profiles.autoProfile; its project's default first): " +
           "{ sessionId, toolsOff? (tools kept off whatever the profile says) } → SessionInfo.", ReadOnly: false, Params: [RpcParam.Req("sessionId"), RpcParam.Opt("toolsOff", RpcParamType.Array)]),
            async (req, token) =>
        {
            var off = req.Prop("toolsOff") is { ValueKind: System.Text.Json.JsonValueKind.Array } a
                ? a.EnumerateArray().Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : null;
            return await profiles.ApplyAutoAsync(req.Required("sessionId"), off, token).ConfigureAwait(false);
        });
        return Task.CompletedTask;
    }
}

/// <summary>Before a chat's first model call, its default profile (if it has none yet).</summary>
internal sealed class ProfileHook(ProfileService profiles) : IAgentHook
{
    /// <summary>First of all hooks: everything after it reads the session's prompt and tools.</summary>
    public int Order => -1000;

    public ValueTask OnRunStartAsync(AgentRunContext run)
    {
        if (!run.Agent.IsSubagent) profiles.EnsureDefault(run.Session.Id);
        return ValueTask.CompletedTask;
    }
}
