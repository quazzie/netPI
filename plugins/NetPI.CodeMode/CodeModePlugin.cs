using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.CodeMode;

/// <summary>
/// The <c>codemode</c> tool (idea-w9ibt2, after pi 1.0's codemode): the model writes one JavaScript script that calls its
/// tools, and only what the script outputs reaches the model. Scripts run in Jint (see <see cref="ScriptRunner"/>); their
/// tool calls go through the runtime's pipeline (<see cref="INestedToolCalls"/>), so every policy that checks the model's
/// calls checks a script's.
/// <para>Settings: <c>codemode.timeoutSeconds</c> (600), <c>codemode.memoryMb</c> (256).</para>
/// </summary>
[NetPiPlugin("netpi.codemode", Name = "Code mode", Description = "The codemode tool: one JavaScript script calls the agent's tools (in parallel, filtered) and only its output reaches the model", Order = 70)]
public sealed class CodeModePlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Tools.Register(new CodeModeTool(context));
        context.Services.Register(new SettingsSection
        {
            Id = "codemode", Title = "Code mode", Group = "Runs", Order = 40,
            Settings =
            [
                SettingInfo.Int(CodeModeTool.TimeoutSetting, "Script time limit", CodeModeTool.DefaultTimeoutSeconds,
                    "A script still running after this is stopped; a script can ask for a different limit (timeoutSeconds).", 1, CodeModeTool.MaxTimeoutSeconds, "s"),
                SettingInfo.Int(CodeModeTool.MemorySetting, "Script memory", CodeModeTool.DefaultMemoryMb,
                    "What one script may allocate.", 16, 4096, "MB"),
            ],
        });
        return Task.CompletedTask;
    }
}

internal sealed class CodeModeTool(IPluginContext ctx) : IAgentTool
{
    public const string Name = "codemode";
    public const string TimeoutSetting = "codemode.timeoutSeconds";
    public const string MemorySetting = "codemode.memoryMb";
    public const int DefaultTimeoutSeconds = 600;
    public const int MaxTimeoutSeconds = 3600;
    public const int DefaultMemoryMb = 256;
    public const int MaxCodeChars = 200_000;

    public ToolDefinition Definition { get; } = new()
    {
        Name = Name, Label = "Code mode", Category = "code", SummaryArg = "code",
        Description =
            "Run a JavaScript script that calls your tools; only what it outputs reaches you. Use it to run many calls in parallel " +
            "(Promise.all), filter or aggregate large results, or chain calls without a turn each. `code` is the body of an async " +
            "function (await and return work) in a sandbox without Node, files, network or timers: it reaches the outside only " +
            "through tools.\n" +
            "Globals: tools.<name>(args) resolves to a tool's text and throws on its error (any of your tools, and deferred ones: " +
            "find those with searchTools); callTool(name, args) resolves to { content, isError, details, images } and never throws " +
            "for the tool's error; text(v) and `return v` add output; console.log(...); image(block) shows an image; " +
            "searchTools(query, { limit }) and ALL_TOOLS list { name, description }; describeTool(name) gives the description and " +
            "JSON-schema parameters (throws for an unknown name); store(key, value) / load(key) keep small JSON across this " +
            "chat's scripts (saved when the script succeeds).\n" +
            "Calls that change something run one at a time, read-only ones in parallel; each passes the same policy checks as yours. " +
            "A failed script keeps its output so far; calls it made are not undone.",
        PromptGuidelines =
        [
            "When a task takes many tool calls whose results matter only together (searching many files, parallel lookups, filtering big outputs), write one codemode script instead of separate calls.",
        ],
        Parameters = JsonNode.Parse($$$"""
            {"type":"object","properties":{
              "code":{"type":"string","description":"JavaScript: the body of an async function."},
              "timeoutSeconds":{"type":"integer","minimum":1,"maximum":{{{MaxTimeoutSeconds}}},"description":"Stop the script after this long (default {{{DefaultTimeoutSeconds}}})."}},
             "required":["code"],"additionalProperties":false}
            """)!.AsObject(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (context.Features.Get<INestedToolCalls>() is not { } nested)
            return ToolResult.Error("codemode runs only as a call of your own, not from inside another tool or script.");
        var a = new ToolArgs(args);
        var code = a.Str("code") ?? "";
        if (string.IsNullOrWhiteSpace(code)) return ToolResult.Error("code is empty: give the script's JavaScript.");
        if (code.Length > MaxCodeChars) return ToolResult.Error($"code has {code.Length:N0} characters; a script may have {MaxCodeChars:N0}.");

        var seconds = Math.Clamp(a.Int("timeoutSeconds") ?? ctx.Settings.GetInt(TimeoutSetting, DefaultTimeoutSeconds), 1, MaxTimeoutSeconds);
        var memoryMb = Math.Clamp(ctx.Settings.GetInt(MemorySetting, DefaultMemoryMb), 16, 4096);
        var store = ScriptStore.Of(ctx.Sessions.GetSession(context.SessionId));
        var host = new ScriptHost(nested, store, context.Output);
        var outcome = await new ScriptRunner(host, new ScriptLimits(TimeSpan.FromSeconds(seconds), memoryMb * 1024L * 1024L))
            .RunAsync(code, ct).ConfigureAwait(false);

        if (outcome.Ok && store.Dirty)
        {
            var values = store.ToJson();
            ctx.Sessions.UpdateSession(context.SessionId, s =>
            {
                if (values.Count == 0) { s.Meta?.Remove(ScriptStore.MetaKey); return; }
                s.Meta ??= new JsonObject();
                s.Meta[ScriptStore.MetaKey] = values;
            });
        }

        var (calls, count) = host.Calls();
        var details = new JsonObject
        {
            ["kind"] = "codemode",
            ["ok"] = outcome.Ok,
            ["durationMs"] = (long)outcome.Elapsed.TotalMilliseconds,
            ["callCount"] = count,
            ["calls"] = calls,
        };
        if (outcome.TimedOut) details["timedOut"] = true;
        return new ToolResult
        {
            Content = outcome.Text,
            IsError = !outcome.Ok,
            Images = outcome.Images.Count > 0 ? [.. outcome.Images] : null,
            Details = details,
        };
    }
}
