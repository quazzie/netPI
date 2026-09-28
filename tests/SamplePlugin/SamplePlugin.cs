using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetPI;

namespace SamplePlugin;

/// <summary>Plugin-defined payload types: they live in the collectible load context and must not pin it after unload.</summary>
public sealed record EchoResult(string Text, string Version);
public sealed record Pong(string Version, int Count);

/// <summary>
/// Exercises every registry the host scopes per plugin, plus plugin-typed JSON (RPC results and event payloads),
/// database access with anonymous-type parameters and a background loop bound to <see cref="IPluginContext.Stopping"/>.
/// </summary>
[NetPiPlugin("test.sample", Name = "Sample plugin", Description = "Host test plugin", Order = 50)]
public sealed class SamplePlugin : INetPiPlugin
{
#if SAMPLE_V2
    public const string Value = "v2";
#elif SAMPLE_FAIL
    public const string Value = "fail";
#else
    public const string Value = "v1";
#endif

    private int _pings;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        // Was the previous version's tool still registered when this one started? A reload swaps (NetPI.Host's
        // ReloadEntryAsync): the old registrations go only after the new instance is up, so a tool is never absent and
        // no running chat sees a "tools changed" notice. On a first start there is nothing to be present. A bool, not
        // the tool: holding the old instance's object would root its load context and the unload check would fail.
        var overlapped = context.Tools.Get("sample_echo") is not null;
        context.Rpc.Register("sample.value", (_, _) => Task.FromResult<object?>(Value), "Returns the build variant");
        context.Rpc.Register("sample.overlap", (_, _) => Task.FromResult<object?>(overlapped ? "present" : "none"),
            "Whether the previous version's tool was still registered when this one started");
        context.Rpc.Register("sample.echo", (req, _) => Task.FromResult<object?>(new EchoResult(req.Str("text") ?? "", Value)));
        context.Tools.Register(new EchoTool());
        context.Services.Register<IPromptSection>(new Section());
        context.Ui.AddTab(new UiTabInfo { Id = "sample", Title = "Sample", Icon = "list" });
        context.Ui.AddCommand(new SlashCommandInfo { Name = "sample", Description = "Sample command", Rpc = "sample.value" });
        context.Http.Map("hello", http => http.Response.WriteAsync("hello " + Value));
        context.Events.Subscribe("sample.ping", _ => context.Events.Publish("sample.pong", new Pong(Value, Interlocked.Increment(ref _pings))));

        // A registration the plugin disposes itself: the host disposes it again on unload (must be harmless).
        var temporary = context.Services.Register<IPromptSection>(new Section());
        temporary.Dispose();

        context.Db.Migrate("test.sample", "CREATE TABLE sample_items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, variant TEXT)");
        context.Db.Insert("INSERT INTO sample_items(name, variant) VALUES(@name, @variant)", new { name = "started", variant = Value });

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, context.Stopping); }
            catch (OperationCanceledException) { }
        });

        context.Logger.LogInformation("Sample plugin {Value} started", Value);
#if SAMPLE_FAIL
        throw new InvalidOperationException("sample plugin failure");
#else
        return Task.CompletedTask;
#endif
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private sealed class Section : IPromptSection
    {
        public string Id => "sample";
        public int Order => 900;
        public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct) => new("sample " + Value);
    }

    private sealed class EchoTool : IAgentTool
    {
        public ToolDefinition Definition { get; } = new()
        {
            Name = "sample_echo",
            Description = "Echo the text argument",
            Label = "Echo",
            ReadOnly = true,
        };

        public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok(Value + ":" + (args.TryGetProperty("text", out var t) ? t.GetString() : "")));
    }
}
