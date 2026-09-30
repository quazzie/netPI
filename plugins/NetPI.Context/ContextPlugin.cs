using System.Text.Json.Nodes;

namespace NetPI.Context;

/// <summary>
/// System prompt builder (<see cref="ISystemPromptBuilder"/>) that renders all <see cref="IPromptSection"/>s, plus a bare
/// base: identity (0), environment (100), the active tools' guidelines (200), subagent role (800), append (900).
/// Feature guidance comes from the plugins that own the feature (their tools' guidelines or their own sections).
/// A session's prompt is frozen at its first model call; its working directory and project arrive as "project" notices,
/// changes to its tools as "tools" notices.
/// <para>Settings: <c>context.customPrompt</c> (replaces the identity section), <c>context.appendPrompt</c>.</para>
/// <para>RPC: <c>context.preview { sessionId }</c> → <c>{ systemPrompt, frozen, tools: [{name, description, chars, schemaChars}], estimatedTokens }</c>;
/// <c>context.reset { sessionId }</c> (a profile switch: the prompt is rendered again at the next call);
/// <c>context.prompts { sessionId }</c> → every prompt the session was sent, with its tools (the chat shows them). Event
/// <c>context.prompt { sessionId, version, afterSeq }</c> when a session is sent a new prompt; <c>context.toolsets</c> →
/// its tools now and every change with its cause (a "tools" notice is what told it).</para>
/// </summary>
[NetPiPlugin("netpi.context", Name = "Context", Description = "System prompt (frozen per session), working-directory and tool-change notices", Order = 40)]
public sealed class ContextPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "context", Title = "System prompt", Group = "Context", Order = 10,
            Settings =
            [
                new SettingInfo { Key = "context.appendPrompt", Type = "text", Label = "Custom instructions", Help = "Added to the end of every session's system prompt.", Applies = "new sessions" },
                new SettingInfo { Key = "context.customPrompt", Type = "text", Label = "Identity", Help = "The opening of the system prompt: who the agent is and how it works. A profile can have its own.", Default = System.Text.Json.Nodes.JsonValue.Create(IdentitySection.Default), Applies = "new sessions" },
                new SettingInfo { Key = "context.toolDescriptions", Type = "bool", Label = "Describe every tool in the prompt too", Default = System.Text.Json.Nodes.JsonValue.Create(false), Help = "They are always in the tool schemas.", Applies = "new sessions" },
            ],
        });
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
        var toolNotices = new ToolNotices(context, prompts);
        context.Services.Register<IAgentHook>(notices);
        context.Services.Register<IAgentHook>(toolNotices);
        // what reloaded: the cause of the next tool-set change (a "tools" notice names the plugin)
        context.Events.Subscribe(EventTypes.PluginsReloaded, toolNotices.OnPluginsReloaded);
        context.Events.Subscribe(EventTypes.SessionChanged, toolNotices.OnSessionChanged);
        context.Events.Subscribe("mcp.toolsChanged", toolNotices.OnRemoteToolsChanged);
        context.Events.Subscribe(EventTypes.SessionProject, e =>
        {
            if (e.As<JsonObject>()?["sessionId"]?.GetValue<string>() is { Length: > 0 } id) notices.OnProjectChanged(id);
        });
        context.Events.Subscribe(EventTypes.SessionDeleted, e =>
        {
            if (e.As<JsonObject>()?["id"]?.GetValue<string>() is { Length: > 0 } id)
            {
                prompts.Delete(id);
                toolNotices.Forget(id);
            }
        });
        // a fork goes on with the prompt the original had at the fork point (and its chat shows it right away)
        context.Events.Subscribe(EventTypes.SessionForked, e =>
        {
            var d = e.As<JsonObject>();
            if (d?["sessionId"]?.GetValue<string>() is { Length: > 0 } to && d["fromSessionId"]?.GetValue<string>() is { Length: > 0 } from)
                prompts.Fork(from, to, d["upToSeq"]?.GetValue<long>() ?? 0);
        });

        context.Rpc.Register("context.preview", async (req, token) =>
        {
            var sessionId = req.Required("sessionId");
            return await PreviewAsync(context, builder, sessionId, token).ConfigureAwait(false);
        }, "The system prompt and tools a session is sent: { sessionId } → { systemPrompt, frozen, tools, estimatedTokens }");

        // a profile switch: the next model call renders the prompt again and takes a new tool baseline (one full re-read)
        context.Rpc.Register("context.reset", (req, _) =>
        {
            prompts.Reset(req.Required("sessionId"));
            return Task.FromResult<object?>(true);
        }, "Forget a session's frozen system prompt and tool baseline; its next model call renders them again: { sessionId } → true");

        context.Rpc.Register("context.prompts", (req, _) =>
            Task.FromResult<object?>(PromptsJson(context, prompts, req.Required("sessionId"))),
            "The system prompts a session was sent, with their tools: { sessionId } → { prompts: [{ version, afterSeq, createdAt, systemPrompt, tools: [{ name, description, parameters? }] }] }");

        context.Rpc.Register("context.toolsets", (req, _) =>
            Task.FromResult<object?>(ToolSets.Build(context, toolNotices, prompts, req.Required("sessionId"))),
            "A session's tools now and every change since its first model call, with the cause: { sessionId } → { sessionId, tools, baseline: { tools, sinceSeq } | null, changes: [{ seq, time, added, removed, cause: plugin-reload|profile|user|settings|unknown, plugins, text }], reloads: [{ ids, time, kind }] }");
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
        var tools = ActiveTools(ctx, agent, session);
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
            var schema = d.Parameters.ToJsonString().Length;
            var size = d.Name.Length + d.Description.Length + schema + 16;
            chars += size;
            // what each tool costs in every request: its description and its parameter schema, in characters
            toolArr.Add(new JsonObject { ["name"] = d.Name, ["description"] = d.Description, ["chars"] = size, ["schemaChars"] = schema });
        }
        return new JsonObject
        {
            ["systemPrompt"] = prompt,
            ["frozen"] = frozen,
            ["tools"] = toolArr,
            ["estimatedTokens"] = chars / 4,
        };
    }

    /// <summary>
    /// What <c>context.prompts</c> returns: every prompt the session was sent, oldest first. A session frozen before these were
    /// kept has its current prompt only, as version 1, with the names of its first tools.
    /// </summary>
    internal static JsonObject PromptsJson(IPluginContext ctx, PromptStore prompts, string sessionId)
    {
        if (ctx.Sessions.GetSession(sessionId) is null) throw new RpcException("not_found", $"No session {sessionId}");
        var list = new JsonArray();
        foreach (var p in prompts.Sent(sessionId))
        {
            JsonNode? tools;
            try { tools = JsonNode.Parse(p.ToolsJson); } catch { tools = new JsonArray(); }
            list.Add(new JsonObject
            {
                ["version"] = p.Version, ["afterSeq"] = p.AfterSeq, ["createdAt"] = p.CreatedAt, ["systemPrompt"] = p.Prompt, ["tools"] = tools,
            });
        }
        if (list.Count == 0 && prompts.Get(sessionId) is { } frozen)
            list.Add(new JsonObject
            {
                ["version"] = 1, ["afterSeq"] = 0, ["systemPrompt"] = frozen,
                ["tools"] = new JsonArray([.. (prompts.GetTools(sessionId)?.Names ?? []).Select(n => (JsonNode?)new JsonObject { ["name"] = n })]),
            });
        return new JsonObject { ["prompts"] = list };
    }

    private static ModelInfo PlaceholderModel(string? modelRef)
    {
        var r = modelRef ?? "none/none";
        var slash = r.IndexOf('/');
        return slash > 0 ? new ModelInfo { Provider = r[..slash], Id = r[(slash + 1)..] } : new ModelInfo { Provider = "none", Id = r };
    }

    /// <summary>
    /// Same filtering and order as the agent runtime: allowlist, no orchestration tools at the maximum depth, not the tools
    /// switched off for the session, by name.
    /// </summary>
    internal static List<IAgentTool> ActiveTools(IPluginContext ctx, AgentInfo? agent, SessionInfo? session = null)
    {
        var maxDepth = 3;
        try { maxDepth = ctx.Settings.Get("agents.maxDepth", 3); } catch { }
        return ToolSelection.Eligible(ctx.Tools, agent, session, maxDepth).Where(t => !t.Definition.Deferred).ToList();
    }
}
