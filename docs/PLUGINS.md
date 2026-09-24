# Writing plugins

Everything in NetPI except the small host kernel is a plugin: providers, tools, the agent loop, lanes, compaction,
the right-panel tabs. A plugin is a .NET assembly in its own folder under `<app>/plugins/` (or `~/.netpi/plugins/`),
loaded into a collectible `AssemblyLoadContext`. When its DLL changes the host **hot-reloads** it: everything it
registered is removed, the old code is unloaded and the new build starts — no restart.

## Minimal plugin

```
plugins/MyPlugin/
  MyPlugin.csproj        <Project Sdk="Microsoft.NET.Sdk"></Project>
  plugin.json            { "id": "my.plugin", "name": "My plugin", "description": "…" }   (optional)
  MyPlugin.cs
  ui/main.js             (optional tab UI, built to wwwroot/ui.js)
```

`plugins/Directory.Build.props` does the rest: output to `artifacts/app/plugins/MyPlugin/`, a reference to
`NetPI.Abstractions` that is *not* copied (contract types must come from the host), dynamic-loading settings and
copying `wwwroot/**`. Add the project to `NetPI.slnx` (`dotnet sln NetPI.slnx add plugins/MyPlugin/MyPlugin.csproj`).

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NetPI;

[NetPiPlugin("my.plugin", Name = "My plugin", Order = 100)]
public sealed class MyPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext ctx, CancellationToken ct)
    {
        // a tool for agents
        ctx.Tools.Register(new WordCountTool());

        // a section of the system prompt (ordered: 0 identity, 100 environment, 200 tool guidelines, 300 lanes,
        // 800 subagent role, 900 appended prompt); rendered once per session, see "Never rewrite what was sent"
        ctx.Services.Register<IPromptSection>(new MySection());

        // an RPC method for the UI (and for other plugins: ctx.Rpc.InvokeAsync("my.hello"))
        ctx.Rpc.Register("my.hello", (req, _) => Task.FromResult<object?>(new { hello = req.Str("name") ?? "world" }));

        // listen to the event bus (agent.status, message.added, tool.end, lanes.changed, …)
        ctx.Events.Subscribe("tool.end", e => ctx.Logger.LogInformation("tool finished: {Data}", e.Data));

        // a tab in the right panel, a slash command
        ctx.Ui.AddTab(new UiTabInfo { Id = "hello", Title = "Hello", Panel = UiPanel.Right, Icon = "sparkle", Order = 50 });
        ctx.Ui.AddCommand(new SlashCommandInfo { Name = "hello", Description = "Say hello", Rpc = "my.hello" });

        // its settings as controls in the settings dialog (on the page of its group; read them with ctx.Settings)
        ctx.Services.Register(new SettingsSection
        {
            Id = "my.plugin", Title = "My plugin", Group = "Tools", Order = 100,
            Settings = [SettingInfo.Int("my.maxWords", "Largest file", 100_000, "Longer files are refused.", 1, null, "words")],
        });
        return Task.CompletedTask;
    }
}

sealed class WordCountTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "word_count",
        Label = "Words",
        Description = "Count the words in a text file.",
        Category = "files",
        ReadOnly = true,                 // read-only calls may run in parallel
        SummaryArg = "path",             // shown next to the label in the chat
        Parameters = JsonNode.Parse("""{ "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"] }""")!.AsObject(),
        PromptGuidelines = ["Use word_count instead of `wc -w`."],
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var path = ctx.ResolvePath(args.GetProperty("path").GetString()!);   // relative to the session's project folder
        var text = await File.ReadAllTextAsync(path, ct);
        var n = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return ToolResult.Ok($"{n} words", details: new { path, words = n });   // details are for the UI only
    }
}

sealed class MySection : IPromptSection
{
    public string Id => "my.section";
    public int Order => 400;
    public ValueTask<string?> RenderAsync(PromptContext c, CancellationToken ct) =>
        ValueTask.FromResult<string?>(c.Project is null ? null : $"# My plugin\nProject {c.Project.Name} uses tabs for indentation.");
}
```

Register a tool with the same name as an existing one and a higher priority (`ctx.Tools.Register(tool, priority: 10)`)
to **replace** a built-in tool; `tools.disabled` in the settings hides tools from every chat, and a chat can switch off
its own (`agent.setTools`, see `docs/TOOLS.md`).

The context plugin only provides a bare base (who the agent is, the environment it runs in, how harness notices look).
Guidance for a feature comes from the plugin that owns it, so it disappears with the plugin: tool tips as the tool's
`PromptGuidelines` (listed under "# Tools" while the tool is active), anything else as the plugin's own section (the lanes
plugin adds "# Lanes" for agents that can spawn subagents).

### Never rewrite what was sent

A request starts with the system prompt, the tool definitions and every earlier message; the backend reuses its cache
for as long as that prefix is unchanged, and any change makes it re-prefill the whole conversation (nInfer's KV cache and
Anthropic's prompt cache alike). So nothing that was sent is ever changed; new information is only appended:

- **The system prompt is rendered once per session**, at its first model call, and then reused (the context plugin
  stores it). Sections must not depend on session state (working directory, project, files, model, time); settings and
  plugin changes reach new sessions. `context.preview` shows the stored prompt (`frozen: true`).
- **State that changes during a session is appended as a notice by the plugin that owns it**: the context plugin
  announces the working directory and project (`project` notices: at the first model call, on a switch, when a project
  folder moves), the AGENTS.md plugin the instruction files (`instructions` notices: all at first, later only edited,
  new or removed files). Pattern: an `IAgentHook` whose `OnBeforeModelCallAsync` (Order above compaction's -100) compares
  the current state with the last notice still in `turn.Messages` and, if they differ, appends a notice and calls
  `turn.ReloadMessagesAsync()`; react to events (e.g. `session.project`) to announce right away.
- **Tools are sent sorted by name**, so a plugin reload does not reorder them. Every request carries the tools that are
  registered right now, so a tool from a plugin loaded mid-session is callable at the next model call. Its guidelines
  are not in the frozen prompt, so the context plugin appends a `tools` notice ("Your tools changed. New: …", with the
  new tools' `PromptGuidelines`; removed tools are named too, and tools the user switched off for the chat as such).
  Changing the tool set is the one change that re-prefills once, because the definitions sit at the top of the request.

Exceptions by necessity: compaction replaces old messages with a summary when the context is nearly full, tool-call
repair turns a tool call the model wrote as text into a real call, and a profile switch in a started chat (the user's
choice) renders the system prompt again (`context.reset`; the next call re-reads the conversation once).

## Extension points (all in `src/NetPI.Abstractions`)

| contract | register with | used for |
|---|---|---|
| `IAgentTool` | `ctx.Tools.Register` | tools |
| `IModelProvider` | `ctx.Services.Register<IModelProvider>` | model backends (see the AiProxy / Anthropic plugins) |
| `IModelMiddleware` | `ctx.Services.Register<IModelMiddleware>` | wrap every model call (retry, logging, budgets) |
| `IAgentHook` | `ctx.Services.Register<IAgentHook>` | agent lifecycle: before/after model calls (compaction, nudge, tool repair), tool calls (permission gates), run start/end |
| `IPromptSection` | `ctx.Services.Register<IPromptSection>` | system prompt sections |
| `ToolResultLimit` | `ToolResultLimit.Fit(ctx.Settings, ownCap)` | a tool that pages or tails its own output stays under the tool result limit (the runner saves longer results to a file) |
| `SettingsSection` | `ctx.Services.Register(new SettingsSection { … })` | the plugin's settings as controls in the settings dialog (`settings.schema`): `SettingInfo.Bool/Int/Number/Str/Text/Secret/Choice/List/ModelRef/Folder/FilePath`; `Group` picks the page (General, Models, Agents, Context, Tools); `Applies` says when a change takes effect (`"restart"`, `"new sessions"`). Show the real default: `Default` for fixed values and built-in texts (the dialog shows a text in full, to edit), `Placeholder` for what is found at runtime (the path found), never a vague "built in" |
| `ISystemPromptBuilder`, `ILaneScheduler`, `IAgentRuntime` | `ctx.Services.Register<…>(impl, priority)` | replace a core plugin's service |
| RPC / events / HTTP | `ctx.Rpc`, `ctx.Events`, `ctx.Http` | UI and inter-plugin communication |
| UI tabs / commands | `ctx.Ui` | left/right panel tabs, slash commands |

Services from other plugins can be reloaded at any time: resolve them per use (`ctx.Services.Get<T>()`), don't
cache them. Use `ctx.Db.Migrate("my.plugin", "CREATE TABLE …")` for your own SQLite tables and `ctx.Settings` for
settings (read them at use time; `settings.changed` is published on every change).

**Hot-reload rule:** don't hand your plugin's own types to long-lived host caches. Event payloads and RPC results may
be anonymous objects, records or `JsonObject`s — but don't put your types inside `object`-typed containers
(`Dictionary<string, object>`, `List<object>`), and use `NetPiJson.ToElement/ToNode/For(type)` instead of
`NetPiJson.Options` for your own types. The Diagnostics tab shows whether an unloaded plugin was really collected.

## Tab UI

A tab is an ES module in the plugin's `wwwroot` exporting `mount(el, ctx)` (see `docs/PROTOCOL.md` → "Plugin UI
tabs" for the `ctx` API: `rpc`, `on`, `app.openSession`, `app.insertText`, …). Write it in Svelte next to the plugin
(`plugins/MyPlugin/ui/main.js` + components, importing shared components from `@netpi/kit`) and run
`npm run build:plugins` — it bundles `wwwroot/ui.js` and copies it into the running app, which reloads the tab
(`node web/scripts/build-plugins.mjs --no-copy` leaves the running app alone).
Plain JavaScript works too: just drop a `wwwroot/ui.js`. Style with the host CSS variables and the `np-*` classes
(`web/src/styles/kit.css`). Details: `docs/UI.md`.

## Dev loop

```powershell
.\build.ps1 -Run                          # once
dotnet build plugins\MyPlugin              # while NetPI runs → the plugin hot-reloads
npm run build:plugins                      # tab UI → the tab reloads
```

`/reload [pluginId]` in the chat, or the Diagnostics tab, reloads plugins on demand.
