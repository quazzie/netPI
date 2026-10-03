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
/// <para>RPC: <c>profiles.list</c>, <c>profiles.apply { sessionId, profile }</c>.</para>
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

        context.Rpc.Register("profiles.list", (_, _) => Task.FromResult<object?>(profiles.ListJson()),
            "The profiles: → { defaultProfile, profiles: { id, name, prompt, toolsOff }[] }");
        context.Rpc.Register("profiles.apply", async (req, token) =>
        {
            var sessionId = req.Required("sessionId");
            var id = req.Str("profile");
            return await profiles.ApplyAsync(sessionId, string.IsNullOrWhiteSpace(id) || id == ProfileService.None ? null : id, token)
                .ConfigureAwait(false);
        }, "Give a chat a profile (null = none): { sessionId, profile } → SessionInfo. In a started chat the system prompt is rendered again at the next model call (one full re-read).");
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
