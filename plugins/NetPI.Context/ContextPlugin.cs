using System.Text.Json.Nodes;

namespace NetPI.Context;

/// <summary>
/// System prompt builder (<see cref="ISystemPromptBuilder"/>) that renders all <see cref="IPromptSection"/>s, plus a bare
/// base: identity (0), environment (100), the active tools' guidelines (200), subagent role (800), append (900).
/// Feature guidance comes from the plugins that own the feature (their tools' guidelines or their own sections).
/// A session's prompt is frozen at its first model call; its working directory and project arrive as "project" notices.
/// <para>Settings: <c>context.customPrompt</c> (replaces the identity section), <c>context.appendPrompt</c>.</para>
/// <para>RPC: <c>context.preview { sessionId }</c> → <c>{ systemPrompt, frozen, tools: [{name, description}], estimatedTokens }</c>.</para>
/// </summary>
[NetPiPlugin("netpi.context", Name = "Context", Description = "System prompt (frozen per session) and working-directory notices", Order = 40)]
public sealed class ContextPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var prompts = new PromptStore(context);
        prompts.Initialize();
        var builder = new SystemPromptBuilder(context, prompts);
        context.Services.Register<ISystemPromptBuilder>(builder);
        context.Services.Register<IPromptSection>(new IdentitySection(context.Settings));
        context.Services.Register<IPromptSection>(new EnvironmentSection());
        context.Services.Register<IPromptSection>(new ToolsSection(context.Settings));
        context.Services.Register<IPromptSection>(new SubagentSection());
        context.Services.Register<IPromptSection>(new AppendSection(context.Settings));

        var notices = new ProjectNotices(context);
        context.Services.Register<IAgentHook>(notices);
        context.Events.Subscribe(EventTypes.SessionProject, e =>
        {
            if (e.As<JsonObject>()?["sessionId"]?.GetValue<string>() is { Length: > 0 } id) notices.OnProjectChanged(id);
        });
        context.Events.Subscribe(EventTypes.SessionDeleted, e =>
        {
            if (e.As<JsonObject>()?["id"]?.GetValue<string>() is { Length: > 0 } id) prompts.Delete(id);
        });

        context.Rpc.Register("context.preview", async (req, token) =>
        {
            var sessionId = req.Required("sessionId");
            return await PreviewAsync(context, builder, sessionId, token).ConfigureAwait(false);
        }, "The system prompt and tools a session is sent: { sessionId } → { systemPrompt, frozen, tools, estimatedTokens }");
        return Task.CompletedTask;
    }

    internal static async Task<JsonObject> PreviewAsync(IPluginContext ctx, ISystemPromptBuilder fallback, string sessionId, CancellationToken ct)
    {
        var session = ctx.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"No session {sessionId}");
        var project = session.ProjectId is null ? null : ctx.Sessions.GetProject(session.ProjectId);
        var cwd = ctx.Sessions.GetCwd(session);
        var modelRef = session.Model ?? ctx.Models.DefaultModelRef;
        ModelInfo? model = null;
        try { if (modelRef is not null) model = await ctx.Models.FindAsync(modelRef, ct).ConfigureAwait(false); } catch { }
        model ??= PlaceholderModel(modelRef);

        var agent = ctx.Services.Get<IAgentRuntime>()?.GetBySession(sessionId);
        var tools = ActiveTools(ctx, agent);
        var defs = tools.Select(t => t.Definition).ToList();
        var builder = ctx.Services.Get<ISystemPromptBuilder>() ?? fallback;
        var pc = new PromptContext
        {
            Session = session,
            Project = project,
            Cwd = cwd,
            Model = model,
            Tools = defs,
            Agent = agent,
        };
        // Previewing must not freeze the prompt of a session that has not called a model yet.
        var (prompt, frozen) = builder is SystemPromptBuilder ours
            ? await ours.PreviewAsync(pc, ct).ConfigureAwait(false)
            : (await builder.BuildAsync(pc, ct).ConfigureAwait(false), false);

        long chars = prompt.Length;
        var toolArr = new JsonArray();
        foreach (var d in defs)
        {
            chars += d.Name.Length + d.Description.Length + d.Parameters.ToJsonString().Length + 16;
            toolArr.Add(new JsonObject { ["name"] = d.Name, ["description"] = d.Description });
        }
        return new JsonObject
        {
            ["systemPrompt"] = prompt,
            ["frozen"] = frozen,
            ["tools"] = toolArr,
            ["estimatedTokens"] = chars / 4,
        };
    }

    private static ModelInfo PlaceholderModel(string? modelRef)
    {
        var r = modelRef ?? "none/none";
        var slash = r.IndexOf('/');
        return slash > 0 ? new ModelInfo { Provider = r[..slash], Id = r[(slash + 1)..] } : new ModelInfo { Provider = "none", Id = r };
    }

    /// <summary>Same filtering and order as the agent runtime: allowlist, no orchestration tools at the maximum depth, by name.</summary>
    internal static List<IAgentTool> ActiveTools(IPluginContext ctx, AgentInfo? agent)
    {
        var maxDepth = 3;
        try { maxDepth = ctx.Settings.Get("agents.maxDepth", 3); } catch { }
        return ctx.Tools.All.Where(t =>
        {
            var d = t.Definition;
            if (agent?.ToolAllowlist is { } allow && !allow.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) return false;
            if (agent is not null && agent.Depth >= maxDepth && d.Category == "agents" && d.Name != "agent_send") return false;
            return true;
        }).OrderBy(t => t.Definition.Name, StringComparer.Ordinal).ToList();
    }
}
