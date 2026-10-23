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
/// <c>context.reset { sessionId }</c> (forgets the prompt and tool baseline, so the next call renders them again);
/// <c>context.prompts { sessionId, limit?, beforeVersion? }</c> → the newest prompts the session was sent (from its meta), with their tools (the chat shows them). Event
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
            if (d?["sessionId"]?.GetValue<string>() is { Length: > 0 } to) prompts.Fork(to);
        });
        // the runtime records every prompt it sends in the session's meta: that is the new-prompt event
        context.Events.Subscribe(EventTypes.SessionChanged, e =>
        {
            var d = e.As<JsonObject>();
            if (d?["sessionId"]?.GetValue<string>() is not { Length: > 0 } id
                || d["keys"] is not JsonArray keys || !keys.Any(k => k?.GetValue<string>() == SessionPrompt.HistoryKey)) return;
            if (context.Sessions.GetSession(id) is not { } session || SessionPrompt.LastSent(session) is not { } last) return;
            context.Events.Publish(new BusEvent
            {
                Type = SystemPromptBuilder.SentEvent, SessionId = id,
                Data = new JsonObject { ["sessionId"] = id, ["version"] = last.Version, ["afterSeq"] = last.AfterSeq },
            });
        });

        context.Rpc.Register(new RpcMethod("context.preview", "The system prompt and tools a session is sent: { sessionId } → { systemPrompt, frozen, tools, estimatedTokens }", ReadOnly: true, Params: [RpcParam.Req("sessionId")]),
            async (req, token) =>
        {
            var sessionId = req.Required("sessionId");
            return await PreviewAsync(context, builder, sessionId, token).ConfigureAwait(false);
        });

        // a profile switch: the next model call renders the prompt again and takes a new tool baseline (one full re-read)
        context.Rpc.Register(new RpcMethod("context.reset", "Forget a session's frozen system prompt and tool baseline; its next model call renders them again: { sessionId } → true", ReadOnly: false, Params: [RpcParam.Req("sessionId")]),
            (req, _) =>
        {
            var id = req.Required("sessionId");
            context.Sessions.UpdateSession(id, SessionPrompt.Invalidate);
            prompts.Reset(id);
            return Task.FromResult<object?>(true);
        });

        context.Rpc.Register(new RpcMethod("context.prompts", "The newest system prompts a session was sent, with their tools, oldest first within the page: { sessionId, limit?, beforeVersion? } → { prompts: [{ version, afterSeq, createdAt, systemPrompt, provisional?, tools: [{ name, description, parameters, revision? }] }], more }", ReadOnly: true,
            Params: [RpcParam.Req("sessionId"), RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("beforeVersion", RpcParamType.Integer)]),
            (req, _) =>
            Task.FromResult<object?>(PromptsJson(context, prompts, req.Required("sessionId"), int.TryParse(req.Str("limit"), out var limit) ? limit : null, int.TryParse(req.Str("beforeVersion"), out var before) ? before : null)));

        context.Rpc.Register(new RpcMethod("context.toolsets", "A session's tools now and every change since its first model call, with the cause: { sessionId } → { sessionId, tools, baseline: { tools, sinceSeq } | null, changes: [{ seq, time, added, removed, cause: plugin-reload|profile|user|settings|unknown, plugins, text }], reloads: [{ ids, time, kind }] }", ReadOnly: true, Params: [RpcParam.Req("sessionId")]),
            (req, _) =>
            Task.FromResult<object?>(ToolSets.Build(context, toolNotices, prompts, req.Required("sessionId"))));
        return Task.CompletedTask;
    }

    internal static async Task<JsonObject> PreviewAsync(IPluginContext ctx, ISystemPromptBuilder fallback, string sessionId, CancellationToken ct)
    {
        var session = ctx.Sessions.Require(sessionId);
        var project = session.ProjectId is null ? null : ctx.Sessions.GetProject(session.ProjectId);
        var cwd = ctx.Sessions.GetCwd(session);
        var modelRef = session.Model ?? ctx.Models.DefaultModelRef;
        ModelInfo? model = null;
        try { if (modelRef is not null) model = await ctx.Models.FindAsync(modelRef, ct).ConfigureAwait(false); } catch { }
        model ??= PlaceholderModel(modelRef);

        var agent = ctx.Services.Get<IAgentRuntime>()?.GetBySession(sessionId);
        var tools = ActiveTools(ctx, agent, session);
        var defs = tools.Select(t => t.Definition).ToList();
        var exposure = ToolExposure.For(ctx.Settings, agent, session);
        var deferred = ToolSelection.Eligible(ctx.Tools, agent, session, ctx.Settings)
            .Where(t => !exposure.IsDeclared(t.Definition)).Select(t => t.Definition).ToList();
        var builder = ctx.Services.Get<ISystemPromptBuilder>() ?? fallback;
        var pc = new PromptContext
        {
            Session = session,
            Project = project,
            Cwd = cwd,
            Model = model,
            Tools = defs,
            DeferredTools = deferred,
            Agent = agent,
        };
        // Previewing must not freeze the prompt of a session that has not called a model yet. A foreign builder has only
        // BuildAsync, which freezes by contract, so its prompt is not previewed.
        var (prompt, frozen) = builder is SystemPromptBuilder ours
            ? await ours.PreviewAsync(pc, ct).ConfigureAwait(false)
            : ($"(The system prompt comes from {builder.GetType().Name}, which cannot preview it without freezing it.)", false);

        long chars = prompt.Length;
        var toolArr = new JsonArray();
        foreach (var d in defs)
        {
            var schema = d.ParametersChars;
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
    /// What <c>context.prompts</c> returns: a page of the prompts the session was sent (from its meta history), the newest
    /// <paramref name="limit"/> (default 50, at most 200) before <paramref name="beforeVersion"/>, oldest first. A tool whose
    /// definition is not stored is named only.
    /// </summary>
    internal static JsonObject PromptsJson(IPluginContext ctx, PromptStore prompts, string sessionId, int? limit = null, int? beforeVersion = null)
    {
        var history = SessionPrompt.History(ctx.Sessions.Require(sessionId));
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var older = history.Where(p => beforeVersion is null || p.Version < beforeVersion).ToList();
        var page = older.Skip(Math.Max(0, older.Count - take)).ToList();
        var list = new JsonArray();
        foreach (var p in page)
        {
            var tools = new JsonArray();
            foreach (var t in p.Tools)
            {
                var item = new JsonObject { ["name"] = t.Name };
                if (prompts.Definition(t.Def) is { } def)
                {
                    item["description"] = def["description"]?.DeepClone();
                    item["parameters"] = def["parameters"]?.DeepClone();
                }
                if (t.Revision is not null) item["revision"] = t.Revision;
                tools.Add(item);
            }
            var entry = new JsonObject { ["version"] = p.Version, ["afterSeq"] = p.AfterSeq };
            if (p.CreatedAt is not null) entry["createdAt"] = p.CreatedAt;
            entry["systemPrompt"] = p.Prompt;
            if (p.Provisional) entry["provisional"] = true;
            entry["tools"] = tools;
            list.Add(entry);
        }
        return new JsonObject { ["prompts"] = list, ["more"] = older.Count > page.Count };
    }

    private static ModelInfo PlaceholderModel(string? modelRef)
    {
        var r = modelRef ?? "none/none";
        var slash = r.IndexOf('/');
        return slash > 0 ? new ModelInfo { Provider = r[..slash], Id = r[(slash + 1)..] } : new ModelInfo { Provider = "none", Id = r };
    }

    /// <summary>
    /// Same filtering and order as the agent runtime: allowlist, no orchestration tools at the maximum depth, not the tools
    /// switched off for the session, no deferred tool, by name.
    /// </summary>
    internal static List<IAgentTool> ActiveTools(IPluginContext ctx, AgentInfo? agent, SessionInfo? session = null)
    {
        return [.. ToolSelection.Declared(ToolSelection.Eligible(ctx.Tools, agent, session, ctx.Settings), ToolExposure.For(ctx.Settings, agent, session))];
    }
}
