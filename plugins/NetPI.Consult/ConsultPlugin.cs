using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Consult;

/// <summary>
/// <c>consult</c>: a plan or design as plain text, asked of the Claude CLI model (Opus/Sonnet on the owner's
/// subscription) during a run: one text-only model call (Purpose <c>consult</c> — the CLI runs it without tools,
/// without a CLI session, in its scratch folder), one answer. It is deliberately not <c>agent_spawn</c> on a CLI
/// model (a full CLI session with tools) and not <c>decide</c> (bound to AiProxy's HTTP endpoints).
/// <para>
/// The Anthropic-terms guard: only the chats the owner starts may trigger a consult. A chat started by a webhook,
/// a schedule, the phone, a script or as a subagent is refused with the reason that says so.
/// </para>
/// </summary>
[NetPiPlugin("netpi.consult", Name = "Consult",
    Description = "consult: a text-only design consultation through the Claude CLI model (the owner's subscription)",
    Order = 64)]
public sealed class ConsultPlugin : INetPiPlugin
{
    /// <summary>The provider kind that serves consults: the Claude CLI on the owner's subscription.</summary>
    public const string ProviderType = "claude-cli";
    /// <summary>The tool's availability and its cause on the bus: the registry's <c>tools.changed</c> says what, this says why.</summary>
    public const string ToolsChanged = "consult.toolsChanged";

    private IPluginContext _ctx;
    private ConsultTool _tool;
    private IDisposable? _toolRegistration;
    private IDisposable? _settingsSub;
    private bool _available;

    public ConsultPlugin()
    {
        _ctx = null!;
        _tool = null!;
    }

    public Task StartAsync(IPluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _tool = new ConsultTool(ctx);
        ctx.Services.Register(new SettingsSection
        {
            Id = "consult", Title = "Consult", Group = "Tools", Order = 55,
            Settings =
            [
                SettingInfo.Str("consult.model", "Consult model", "claude-cli/claude-opus-5-5",
                    "The Claude CLI model a consult asks (the owner's subscription; Opus by default). Several, comma-separated: the first one the model list has is used. The tool's model argument picks another for one call (a cheaper one).")
                    with { Type = "pickText", Options = ["@models"] },
                SettingInfo.Int("consult.timeoutSeconds", "Timeout", 120, "How long one consult may wait for the CLI before it gives up with a note (the run goes on).", 5, 3600, "s"),
                SettingInfo.Int("consult.maxAnswerChars", "Answer length", 16_000, "The answer is cut at this many characters, with a note to ask for less.", 1_000, 100_000, "chars"),
                SettingInfo.Choice("consult.effort", "Reasoning effort", "medium", ["low", "medium", "high", "xhigh", "max"],
                    "The CLI's --effort for the call (the Claude CLI's efforts, the same list its provider offers)."),
            ],
        });
        _settingsSub = ctx.Events.Subscribe(EventTypes.SettingsChanged, _ => Reconcile());
        Reconcile();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _settingsSub?.Dispose();
        _settingsSub = null;
        _toolRegistration?.Dispose();
        _toolRegistration = null;
        return Task.CompletedTask;
    }

    /// <summary>Whether the tool is available: at least one <c>claude-cli</c> entry in <c>providers.instances</c> that is not disabled.</summary>
    internal static bool Available(ISettings settings) =>
        ProviderInstances.Of(settings, ProviderType).Any(entry => Enabled(entry.Config));

    private static bool Enabled(JsonObject config) =>
        config["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out var enabled) || enabled;

    /// <summary>Register or unregister the tool as the providers' presence says, and say why on the bus.</summary>
    private void Reconcile()
    {
        var now = Available(_ctx.Settings);
        if (now == _available) return;
        if (now)
        {
            _toolRegistration = _ctx.Tools.Register(_tool);
            _ctx.Events.Publish(ToolsChanged, new { available = true, reason = "a claude-cli provider is present and enabled" });
        }
        else
        {
            _toolRegistration?.Dispose();
            _toolRegistration = null;
            var configured = ProviderInstances.Of(_ctx.Settings, ProviderType).Count > 0;
            _ctx.Events.Publish(ToolsChanged, new { available = false, reason = configured ? "every claude-cli provider is disabled" : "no claude-cli provider is configured" });
        }
        _available = now;
    }
}

/// <summary>
/// The <c>consult</c> tool: one text-only call of a Claude CLI model with the brief (and optional context), one answer.
/// A failure (no model, a model on another provider, the CLI's error, a timeout) degrades to a note that names the
/// reason: the run goes on, and there is no retry — one CLI process per call.
/// </summary>
internal sealed class ConsultTool(IPluginContext ctx) : IAgentTool
{
    public const string DefaultModel = "claude-cli/claude-opus-5-5";
    public const int DefaultTimeoutSeconds = 120;
    public const int DefaultMaxAnswerChars = 16_000;

    public const string SystemPrompt =
        "You are a senior engineer acting as a design consultant. Answer the consultation in plain prose or markdown. " +
        "In this call you have no tools and cannot read files or run code: work only from the brief and its context, " +
        "and say exactly what is missing when you need it. Never write tool calls, <invoke> blocks or function-call markup.";

    public ToolDefinition Definition { get; } = new()
    {
        Name = "consult",
        Label = "Consult",
        Category = "general",
        ReadOnly = true,
        SummaryArg = "brief",
        Description = "Ask the Claude CLI model (Opus/Sonnet on the owner's subscription) for a plan or a design as plain text: one text-only call, no tools, no follow-up. Owner-started chats only.",
        Help =
            "One model call, one answer: the Claude CLI model (consult.model, default claude-cli/claude-opus-5-5) reads the brief " +
            "(and the optional context) with no tools and answers in plain prose/markdown. The answer is cut at consult.maxAnswerChars " +
            "(16000) with a note to ask for less; the call gives up after consult.timeoutSeconds (120) with a note, and the run goes on " +
            "either way — there is no retry. model picks a claude-cli model for one call (a cheaper one); a model on any other provider " +
            "is refused with the reason. A consult is refused in a chat the owner did not start (a webhook's, a schedule's, the phone's, " +
            "a script's or a subagent's): the Claude CLI runs on the owner's subscription (Anthropic's terms), and only owner-started chats may use it.",
        Parameters = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "brief": { "type": "string", "description": "What to consult on: the decision or design, with the alternatives if there are any" },
                "model": { "type": "string", "description": "A claude-cli model to ask instead of consult.model (a cheaper one)" },
                "context": { "type": "string", "description": "What the consultant should know: constraints, prior decisions, the relevant state" }
              },
              "required": ["brief"]
            }
            """)!.AsObject(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext tc, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var brief = a.Str("brief")?.Trim();
        if (string.IsNullOrWhiteSpace(brief))
            return ToolResult.Error("consult needs a brief: what to consult on (a few sentences, with the alternatives if there are any).");

        // The owner's subscription, Anthropic's terms: only the chats the owner starts may consult.
        var session = ctx.Sessions.GetSession(tc.SessionId);
        if (session is null) return ToolResult.Error("The consult did not run: the chat is gone.");
        if (UnattendedReason(session) is { } reason)
            return ToolResult.Error($"The consult is refused: {reason}. The Claude CLI runs on the owner's subscription (Anthropic's terms), " +
                "and only chats the owner starts may use it.");

        // The model: the argument, else consult.model; of a comma list the first the catalog knows wins.
        var picked = await PickModelAsync(a.Str("model"), ct).ConfigureAwait(false);
        if (picked is null)
        {
            var cliModels = ctx.Models.Cached
                .Where(m => string.Equals(m.Provider, ConsultPlugin.ProviderType, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Id).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
            return ToolResult.Ok("The consult did not run: no model was found." +
                (cliModels.Count > 0
                    ? $" The Claude CLI models available now: {string.Join(", ", cliModels)} — pick one with the model argument."
                    : " No Claude CLI model is in the model list: add a claude-cli provider (Settings → Models → Claude CLI) and enable it.") +
                " The run goes on; ask again once the provider is set up.");
        }
        if (!string.Equals(picked.Provider, ConsultPlugin.ProviderType, StringComparison.OrdinalIgnoreCase))
            return ToolResult.Error($"The consult cannot use {picked.Ref}: it works only through the claude-cli provider (the owner's Claude subscription), " +
                $"and {picked.Ref} is on the {picked.Provider} provider. Pick a claude-cli model with the model argument, or set consult.model to one.");

        var request = new ModelRequest
        {
            Model = picked,
            SystemPrompt = SystemPrompt,
            Messages = [ChatMessage.UserText(Message(a.Str("context"), brief))],
            ReasoningEffort = ctx.Settings.Get("consult.effort", "medium"),
            SessionId = tc.SessionId,
            AgentId = tc.AgentId,
            Purpose = "consult",
        };

        var timeoutSeconds = Math.Max(1, ctx.Settings.Get("consult.timeoutSeconds", DefaultTimeoutSeconds));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var sw = Stopwatch.StartNew();
        ChatMessage? answer = null;
        var notices = new List<string>();
        try
        {
            await foreach (var e in ctx.Models.StreamAsync(request, cts.Token).ConfigureAwait(false))
            {
                switch (e)
                {
                    case StreamNotice n: notices.Add(n.Text); break;
                    case StreamCompleted c: answer = c.Message; break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return ToolResult.Error($"The consult did not answer in {timeoutSeconds}s, so it was stopped. The run goes on: ask for less, " +
                "or raise consult.timeoutSeconds.");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Consult in session {Session} could not run", tc.SessionId);
            return ToolResult.Error($"The consult did not complete: {ex.Message}");
        }
        var ms = (long)sw.Elapsed.TotalMilliseconds;

        if (answer is null)
            return ToolResult.Error("The consult ended without an answer (the CLI stopped early). The run goes on; ask again.");

        var maxChars = Math.Max(1, ctx.Settings.Get("consult.maxAnswerChars", DefaultMaxAnswerChars));
        var text = answer.Text.Trim();
        var truncated = text.Length > maxChars;
        if (truncated) text = text[..maxChars] + $"\n…cut at {maxChars} chars, ask for less";
        if (notices.Count > 0)
            text += "\n\n" + string.Join("\n", notices.Distinct());

        return ToolResult.Ok(text, new { model = picked.Ref, ms, chars = text.Length, truncated });
    }

    /// <summary>The model to ask: the argument, else <c>consult.model</c>; of a comma list the first the catalog knows.</summary>
    private async Task<ModelInfo?> PickModelAsync(string? model, CancellationToken ct)
    {
        var text = string.IsNullOrWhiteSpace(model)
            ? ctx.Settings.Get("consult.model", DefaultModel) ?? DefaultModel
            : model;
        foreach (var candidate in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ModelInfo? info = null;
            try { info = await ctx.Models.FindAsync(candidate, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.Logger.LogDebug(ex, "Consult: looking up {Model} failed", candidate);
            }
            if (info is not null) return info;
        }
        return null;
    }

    private static string Message(string? context, string brief) =>
        string.IsNullOrWhiteSpace(context)
            ? brief
            : $"Context (what the consultant should know):\n{context.Trim()}\n\nConsultation brief:\n{brief}";

    /// <summary>
    /// Why this chat is not the owner's (null: it is): a subagent's (its kind), a webhook's (<c>meta.webhook</c>), a
    /// schedule's (<c>meta.schedule</c>), a script's (<c>meta.quiet</c>, what the host sets for an external
    /// <c>sessions.create</c>) or the phone's (<c>meta.origin</c>, what the phone plugin writes for its
    /// <c>sessions.create</c>).
    /// </summary>
    internal static string? UnattendedReason(SessionInfo session)
    {
        if (session.Kind == "subagent") return "this chat is a subagent's";
        var meta = session.Meta;
        if (Str(meta, "webhook") is { } hook)
            return $"this chat was started by the webhook {Str(meta, "webhookName") ?? hook}";
        if (Str(meta, "schedule") is { } schedule)
            return "this chat was started by a schedule";
        if (meta?["quiet"] is JsonValue quiet && quiet.TryGetValue<bool>(out var isQuiet) && isQuiet)
            return "this chat was started by a script or another program";
        if (Str(meta, "origin") is { } origin)
            return $"this chat was started from {origin}";
        return null;
    }

    private static string? Str(JsonObject? meta, string key) =>
        meta?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
