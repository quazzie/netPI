# Writing plugins

The agent rework introduces shared `IDecisionService`, `IGitHistory` and `IResourceLeases` contracts (the Ideas plugin's background-work list, `ideas.work`, is its own: nothing else reads it). Resolve plugin-owned capabilities per operation; a caller may retain one only while that operation runs. Decide and Files implement decision/Git capabilities and preserve their RPC adapters. Ideas, Loops and Guardrails keep their base behavior without optional decision/history providers. Decision admission belongs at the capability boundary; a trusted caller can supply its actual same-model lease. External RPC callers always obtain their own admission.

Profiles writes `SessionPrompt.RevisionKey` alongside identity/tool metadata for explicit switches. Context and Runtime fallback honor it; missing revision is zero for legacy sessions. Ordinary settings, model and project changes keep the sent prefix. The registry of physical leases holds only plain data and host-owned lease handles, so an old scheduler's running requests stay counted without retaining its queues or delegates.

The full plugin base/optional/operation-required matrix is in [the independence plan](archive/2026-10-01-plugin-independence.md). The test gate scans all plugin and imported build declarations for peer dependencies and starts/stops every plugin with no peers. Executor-dependent actions explicitly report unavailable; optional UI sections remain independently usable. `diag.capabilities` reports base registrations and optional presence without claiming network endpoints are healthy.

`IResourceLeases` counts what is physically running on a shared model resource. Its implementation (`ResourceLeases`) is a plain class in the shared contracts that the agents plugin registers, so a new generation adopts the same instance and the count carries across a hot swap; with that plugin absent nothing limits admission and consumers treat "no registry" as unlimited. It holds plain data only — no plugin queues or delegates — so an unloaded plugin's running requests stay counted.

Everything in NetPI except the small host kernel is a plugin: providers, tools, the agent loop, agents, compaction,
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

`plugins/Directory.Build.props` does the rest: output to `$(AppOutDir)plugins/MyPlugin/` (the dev tree,
`artifacts/dev/app/plugins/MyPlugin/`), references to `NetPI.Abstractions` and `NetPI.Contracts` that are *not*
copied (contract types must come from the host, and a plugin that shipped its own copy would fail to resolve another
plugin's service with no error), dynamic-loading settings and copying `wwwroot/**`. Add the project to `NetPI.slnx`
(`dotnet sln NetPI.slnx add plugins/MyPlugin/MyPlugin.csproj`).

Two shared assemblies, both in `src/` and both in the host's default load context: **`NetPI.Abstractions`** is the
harness's own vocabulary (tools, context, sessions, models, the loop's contract, the storage port), and
**`NetPI.Contracts`** holds what is built on it — agent slots and the scheduler, workspaces, decisions, resource
leases, deferred tools, and the event names the plugins publish (`WorkspaceEvents`, `AgentSchedulerEvents`,
`ProcessEvents`). A plugin that speaks none of those references only Abstractions. The kernel references only
Abstractions, which is what keeps the core small (`node scripts/core-size.mjs`).

Two plugins that need the same code cannot reference each other, so the code lives once under `shared/` and each
plugin **compiles it in** — shared source, never a shared assembly:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="$(RepoRoot)shared/ProviderKit/*.cs" LinkBase="Kit" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="NetPI.Providers.Kit" />   <!-- $(RepoRoot) is set in Directory.Build.props -->
  </ItemGroup>
</Project>
```

`shared/ProviderKit/` is what the three provider plugins use (the SSE reader, JSON accessors, error mapping, the
message assembler, secret resolution, the Chat Completions parser and message builder, the model-list cache). Editing
it changes all three, in one place, with no copies to fall out of step. A copy under `plugins/` instead is a bug
waiting to happen: `tests/NetPI.Providers.Tests` fails on one.

The dialect keeps its own subclass and overrides only what differs (OpenRouter's reasoning_details, cost and
generation id; AiProxy's `reasoning_content` and usage), so no shared class grows an `if (openrouter)` branch.

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

        // a section of the system prompt (ordered: 0 identity, 100 environment, 200 tool guidelines, 300 agents,
        // 800 subagent role, 900 appended prompt); rendered once per session, see "Never rewrite what was sent"
        ctx.Services.Register<IPromptSection>(new MySection());

        // an RPC method for the UI (and for other plugins: ctx.Rpc.InvokeAsync("my.hello"))
        ctx.Rpc.Register("my.hello", (req, _) => Task.FromResult<object?>(new { hello = req.Str("name") ?? "world" }));

        // the same with its parameters declared (prefer this for a new method): a request with an unknown, missing or
        // mistyped parameter is a bad_request naming it before the handler runs, rpc.list shows the params, and a
        // handler that reads a name it did not declare throws on its first call (a test finds it, not a user)
        ctx.Rpc.Register(new RpcMethod("my.greet", "Greet: { name } → { hello }", ReadOnly: true, [RpcParam.Req("name")]),
            (req, _) => Task.FromResult<object?>(new { hello = req.Required("name") }));

        // listen to the event bus (agent.status, message.added, tool.end, agents.changed, …)
        ctx.Events.Subscribe("tool.end", e => ctx.Logger.LogInformation("tool finished: {Data}", e.Data));

        // a tab in the right panel (narrow), a slash command. UiPanel.Session instead: a view of one chat in the chat's own
        // (wide) area, switched on from the chat header or by publishing ui.open { sessionId, view: "my.plugin/hello" }
        ctx.Ui.AddTab(new UiTabInfo { Id = "hello", Title = "Hello", Panel = UiPanel.Right, Icon = "sparkle", Order = 50 });

        // an HTTP endpoint at /api/p/my.plugin/hello: the host checks its token and the Origin first. open: true skips
        // both for a client that cannot hold the per-run token (a browser extension): the handler checks a secret of its own
        ctx.Http.Map("hello", http => http.Response.WriteAsync("hello"));
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
`PromptGuidelines` (listed under "# Tools" while the tool is active), anything else as the plugin's own section (the agents
plugin adds "# Agents" for agents that can spawn subagents).

Say each thing once. A tool's description and parameters reach the model with every request, so its guidelines add only
what they don't say: when to reach for the tool, which tool to prefer, and rules that span tools. A line several tools
need goes on each of them with the same text: "# Tools" lists it once (grouped by category), and a `tools` notice leaves
it out when the model has it already. A plugin's section doesn't repeat the tools' definitions either.

### Never rewrite what was sent

A request starts with the system prompt, the tool definitions and every earlier message; the backend reuses its cache
for as long as that prefix is unchanged, and any change makes it re-prefill the whole conversation (nInfer's KV cache and
Anthropic's prompt cache alike). So nothing that was sent is ever changed; new information is only appended:

- **The system prompt is rendered once per session**, at its first model call, and then reused (the context plugin
  stores it, and keeps every prompt a session was sent with its tools for the chat to show: `context.prompts`). Sections must not depend on session state (working directory, project, files, model, time); settings and
  plugin changes reach new sessions. `context.preview` shows the stored prompt (`frozen: true`).
- **State that changes during a session is appended as a notice by the plugin that owns it**: the context plugin
  announces the working directory and project (`project` notices: at the first model call, on a switch, when a project
  folder moves), the AGENTS.md plugin the instruction files (`instructions` notices: all at first, later only edited,
  new or removed files), the skills plugin the skill catalog (`skills` notices, the same way). Pattern: an `IAgentHook` whose `OnBeforeModelCallAsync` (Order above compaction's -100) compares
  the current state with the last notice still in `turn.Messages` and, if they differ, appends a notice and calls
  `turn.ReloadMessagesAsync()`; react to events (e.g. `session.project`) to announce right away.
- **Setup notices go before the first message.** A notice that describes the chat's setting (working directory,
  instruction files, the skill catalog) sets `meta.setup: true`. Made at the first model call, it is stored after the
  first user message, but the model reads it before that message (`ContextOrder` in the runtime; nothing is cached yet at
  that call, so every later request has the same order) and the chat shows it there too (`chatItems.js`). Notices that
  answer the message (a skill loaded with `/skill:name`) stay after it, and so does every later notice.
- **A notice stored while tools run follows their results.** The assistant message and each tool result are stored as they
  happen, so a notice appended mid-batch (a profile switch, an attached idea, a commit check) sits between the calls and
  results that are not in yet. `ModelMessages.Normalize` holds it behind the results of that batch, instead of closing the
  calls as "not executed" and dropping the real results when they arrive; the stored order is unchanged. Calls that never
  get a result are still closed with the synthetic "not executed" result, before the notice.
- **Tools are sent sorted by name**, so a plugin reload does not reorder them. Every request carries the tools that are
  registered right now, so a tool from a plugin loaded mid-session is callable at the next model call. Its guidelines
  are not in the frozen prompt, so the context plugin appends a `tools` notice ("Your tools changed. New: …", with the
  new tools' `PromptGuidelines` that the model doesn't have yet; removed tools are named too, and tools the user
  switched off for the chat as such).
  Changing the tool set is the one change that re-prefills once, because the definitions sit at the top of the request.

Exceptions by necessity: compaction replaces old messages with a summary when the context is nearly full, tool-call
repair turns a tool call the model wrote as text into a real call, a profile switch in a started chat (the user's
choice) renders the system prompt again (the chat's prompt revision is invalidated; the next call re-reads the conversation once),
and a tool call whose arguments a policy hook rewrote before it ran (plan mode cutting an `agent_spawn` `tools` list to the
read-only set, `PlanHook`) is stored with the arguments the tool actually got: the runtime writes them back into the
assistant message it just received (`ToolBatch`, `UpdateMessage` when a hook changed them), so the transcript, the result and
every later request agree on what ran, and the model is not shown a call it did not get. That is an edit of a sent assistant
turn: the next request re-reads from there, and with preserved thinking (Claude 5.x; nInfer's `--preserve-thinking` likewise)
an edited assistant turn is a history edit — the thinking of that turn and after it no longer binds to the conversation that
produced it (dropped, or refused outright on newer accounts), not merely re-prefilled once. A hook that only needs to refuse
a call blocks it instead of rewriting it, which leaves the turn as it was sent.

## Extension points (`src/NetPI.Abstractions` and `src/NetPI.Contracts`)

| contract | register with | used for |
|---|---|---|
| `IAgentTool` | `ctx.Tools.Register` | tools |
| `IModelProvider` | `ctx.Services.Register<IModelProvider>` | model backends (see the AiProxy / Anthropic plugins) |
| `IModelMiddleware` | `ctx.Services.Register<IModelMiddleware>` | wrap every model call (retry, logging, budgets) |
| `IAgentHook` | `ctx.Services.Register<IAgentHook>` | agent lifecycle: before/after model calls (compaction, nudge, tool repair), tool calls (permission gates), run start/end |
| `IAgentCallObserver` | `ctx.Services.Register<IAgentCallObserver>` | watching a model call without deciding anything (metering, diagnostics): always runs, where a hook stops at the first non-null decision |
| `IPromptSection` | `ctx.Services.Register<IPromptSection>` | system prompt sections |
| `ToolResultLimit` | `ToolResultLimit.Fit(ctx.Settings, ownCap)` | a tool that pages or tails its own output stays under the tool result limit (the runner saves longer results to a file) |
| `ToolOutput`, `TextLimit` | `ToolOutput.TailLines`, `ToolOutput.Note`, `TextLimit.Head/HeadTail` | trimming long output to a limit, one shape for every tool: the last N lines within a UTF-8 byte budget, head-only and head+tail cuts (never splitting a surrogate pair), and the "[Output truncated: …]" / "[… truncated]" notes that go with them |
| `IAgentRuntime.WaitYieldedAsync` | `context.Services.Get<IAgentRuntime>()` in a tool | a tool that waits for something outside the run (the user's answer: `ask_user`) gives its agent's instance back meanwhile, like `agent_wait`; false when the user steered instead |
| `SettingsSection` | `ctx.Services.Register(new SettingsSection { … })` | the plugin's settings as controls in the settings dialog (`settings.schema`): `SettingInfo.Bool/Int/Number/Str/Text/Secret/Choice/List/ModelRef/Folder/FilePath`; `Group` picks the page (General, Models, Agents, Context, Tools); `Applies` says when a change takes effect (`"restart"`, `"new sessions"`). Show the real default: `Default` for fixed values and built-in texts (the dialog shows a text in full, to edit), `Placeholder` for what is found at runtime (the path found), never a vague "built in" |
| `ISystemPromptBuilder`, `IAgentScheduler`, `IAgentRuntime` | `ctx.Services.Register<…>(impl, priority)` | replace a core plugin's service (`IAgentScheduler` and the slot types it hands out are in `NetPI.Contracts`) |
| RPC / events / HTTP | `ctx.Rpc`, `ctx.Events`, `ctx.Http` | UI and inter-plugin communication |
| UI tabs / commands | `ctx.Ui` | left/right panel tabs, slash commands |

Services from other plugins can be reloaded at any time: resolve them per use (`ctx.Services.Get<T>()`), don't
cache them. `ctx.Settings` holds your settings (read them at use time; `settings.changed` is published on every
change), and every default is the one your own `SettingsSection` declares — the host seeds nothing for a plugin.

### Storage (`ctx.Data`)

Your own data goes into **named collections of JSON documents**, not into tables. `ctx.Data` is `IPluginData` from
the storage port (`src/NetPI.Abstractions/StoragePort.cs`), which is the core's whole idea of storage: no SQL, no
provider, no engine type. A repository class of your own is the only code that touches it.

```csharp
var items = ctx.Data.Collection("items", new CollectionSpec().Integer("ord").Text("status").Text("projectId"));

ctx.Data.Transaction(() =>                       // one atomic unit, exclusive for this plugin id across hot reloads
{
    var doc = items.Get(id) ?? throw new RpcException("not_found", $"Idea {id} not found");
    items.Put(id, doc);                          // Put replaces; Insert writes nothing and returns false when the key exists
});
```

- **Declare what you query.** `CollectionSpec` names the collection's index fields, and they are the only fields a
  `DataQuery` may filter or order by (`Eq`, `Ne`, `Lt`, `Le`, `Gt`, `Ge`, `In`, `NotIn`, `IsNull`, `NotNull`, plus
  `Order`, `Take` and the paging it sets, and `Count`/`Sum`/`DeleteWhere` over the same filters). A filter or an order on
  anything else throws, so declare every field the queries touch.
- A field with no value in a document matches no comparison (`Ne` and `NotIn` included) and sorts first ascending,
  last descending; ask for it with `IsNull`.
- `Transaction` is the compare-and-set: read, decide and write inside it, so a second writer cannot slip in between
  the read and the write. It is exclusive per plugin id **across hot-reload generations**, so two versions of your
  plugin never interleave. Never call the session store or another plugin's data from inside one.
- Documents are `JsonObject`s and your types never cross the port (a provider would pin your load context and block
  unloading): serialize with `NetPiJson.For(type)` and write the shapes down in a comment block at the top of the
  repository class, the way Ideas, the ledger and Context keep theirs.
- `ctx.Services.Get<IStorageAccess>()` is the two facts a plugin may ask without touching the store: `Info` (which
  provider, its version, where it is) and `Snapshot` (a consistent copy). Backup and Diagnostics use it.

A **new storage provider** is a class implementing `IStorageProvider`, chosen by the `storage.provider` setting at
startup, and it has to pass `tests/NetPI.Storage.Tests` — the same scenarios run against every provider, which is the
port's contract in executable form. It writes its own SQL inside itself; nothing else in the repository may know its
engine.

### The event bus: what it guarantees, and what it does not

`ctx.Events` is the only channel between plugins, and between a plugin and the UI (the host fans every `Ui` event out
to the connected clients). What the bus (`src/NetPI.Host/Events/EventBus.cs`) guarantees:

- **Ordering, per subscriber.** A single dispatcher hands each event, in publish order, to the queue of every matching
  subscriber, and each subscriber's worker runs its own queue one event at a time, in queue order. The order in which
  *different* subscribers' handlers run for one event is not a contract.
- **Best-effort delivery under overload.** Each subscriber has a queue of 2048 events; a subscriber that fills it **drops
  for itself** (counted, logged at most once per 30 s) and the bus and everyone else keep flowing. A drop is silent in
  the data: the event simply never reaches that line. The dispatcher's own input (65536 events) is the last-resort
  ceiling — a drop there, also counted, means the dispatcher is effectively stopped, and the bus says so when its
  backlog passes 1000.
- **No wedged subscriber holds the bus.** An async handler runs at most 30 s on its own line before it is let go of:
  the line moves on to the next event while the handler keeps running in the background (logged as stuck), so later
  events can overlap it. A sync handler cannot be let go of (it cannot yield): it holds only its own line for as long
  as it runs. A handler over 250 ms is reported as slow (at most once per 30 s per line). The log lines that name all
  of this are in `docs/DEBUGGING.md`.

`FlushAsync` (the `events.flush` RPC, which times out after 10 s) is how a test or a shutdown waits for what was
published: it completes once every live subscriber has processed every event published before the call, and a wedged
subscriber holds the flush for its own line only.

The consequence for a plugin: **an event is “something happened”, never “the transition”** — and a plugin that cannot
afford to miss a transition cannot subscribe to one. A state change that must not be lost is written to storage
(`ctx.Data`) or the session store first, and the event only says “re-read”. And every behavioural reaction to an event
has to be **idempotent**: your line drops under overload, and reacting to the same change twice (or only late) must
be harmless, or the reaction belongs in storage, not the bus.

Which of the host's events are display-only — losing them changes no behaviour — is a question of who reacts. The UI
line is display-only by construction: `loadAll` in `web/src/lib/state/app.svelte.js` re-reads sessions, agents, models,
tools, asks, plans and the ui registry on every (re)connect, and a chat re-reads its messages from the store (it is
written to cope with missed events). So the question is the plugins and the kernel, checked against this tree:

- **display-only** (no plugin or kernel reacts behaviourally; the UI re-reads what they mirror): `session.updated`,
  `message.added` / `message.updated`, the live `stream.*` and `tool.start` / `tool.output` / `tool.end` (the finished
  message arrives as `message.added`), `session.context`, `ui.changed`, `usage.changed` / `usage.recorded` (the payload
  is the whole budget snapshot; the budget pill loads `budget.status` on every open).
- **re-read wakes** (the payload is the whole state and the reactions are idempotent re-reads, so a missed one is
  healed by the next): `agents.changed` (is `agents.list`; the ideas plugin wakes its verify queues on it),
  `models.changed` (the catalog invalidates its cache, the scheduler refreshes), `plugins.changed` (registry re-reads).
- **load-bearing** (losing one is a behaviour change, not a refresh): `session.deleted` (plugins drop their
  per-session state), `session.created` / `session.forked` / `session.changed` / `session.project` (profiles, fork
  state, the context's tool notices), `settings.changed` (the kernel and the provider plugins reconfigure),
  `agent.status` (ideas), `plugins.reloaded` and `mcp.toolsChanged` (the context names the cause in the next “tools”
  notice).

When you add an event, decide which of the three it is and keep it there: a re-read wake carries the whole state,
and a load-bearing one gets its reaction written idempotently. None of them carry delivery.

### What a session carries, and what a fork forgets

**Per-session state:** sessions are not a bounded set — every subagent spawn creates one — so a dictionary keyed by
session id has to drop its entry when the session is deleted. Keep such a map in `SessionState<T>`
(`src/NetPI.Abstractions`): `new SessionState<T>(ctx.Events)` takes the plugin's bus and drops the entry on
`session.deleted` itself, and `Forget`/`Clear` do it by hand. Six plugins kept a raw dictionary with no removal at
all and tracked every session the process had ever seen (idea-bv3iw4).

What belongs **to a chat** is its `meta` bag: write it with `ctx.Sessions.UpdateSession(id, s => …)` — the core
stores it, copies it on a fork and announces the keys whose value changed as `session.changed { keys }` — and find the
sessions that carry something with
`ctx.Sessions.ListSessions(new SessionQuery { AttachedKey = "…", AttachedValue = "…" })`, an equality filter on one
meta key (add `IncludeUnmaterialized` to match chats that have no message yet). The core understands one key itself,
`meta.cwd`: the folder the session runs in, which `ISessionStore.GetCwd` returns (this, else the project folder, else
`workspace.default`) and which **a fork never inherits**, because a fork is a new writer. The workspaces plugin binds
a checkout this way, with `meta.workspaceId` and `meta.cwd` (`SessionWorkspace.MetaKey`, `SessionCwd.MetaKey`).

Keys that are **run state** — a goal, a checklist, a granted permission, the agent a chat runs on, an allowance to go
over a budget — are named to the store when your plugin starts:
`ctx.Sessions.DeclareForkReset("goal", "…")`. A fork then starts without them, and the store remembers every key ever
declared, so they are dropped even when your plugin is absent, disabled or throwing at that moment.

### What a tool may know about the run

`ToolContext` and `AgentRunContext` carry a typed bag (`Features`) instead of naming what a run sits in, so the core
names none of those concepts. Read what you understand and nothing else: `ctx.Workspace()` — the checkout this session
works in, `null` when it is not bound to one; a tool that mutates asks `WorkspacePaths.CheckMutation` instead of
comparing paths — and `ctx.AdmissionLease()`, the slot the run holds. A spawner asks the same question of its child
with `SpawnRequest.Workspace()` (`SpawnWorkspace`). These extensions are in `src/NetPI.Contracts`.

**Hot-reload rule:** don't hand your plugin's own types to long-lived host caches. Event payloads and RPC results may
be anonymous objects, records or `JsonObject`s — but don't put your types inside `object`-typed containers
(`Dictionary<string, object>`, `List<object>`), and use `NetPiJson.ToElement/ToNode/For(type)` instead of
`NetPiJson.Options` for your own types. After every unload the host publishes `plugins.unloaded { id, collected }` and, when
the old load context was not collected, logs the warning `Plugin <id>: the previous load context is still alive after
unload (something still references plugin objects); its memory is not reclaimed` (no tab shows it yet).

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
.\build.ps1 -Run                          # once: build, install, start
dotnet build plugins\MyPlugin              # → artifacts\dev\app: the running app sees nothing
.\build.ps1 -Publish                       # install: the running NetPI hot-reloads the changed plugins
npm run build:plugins                      # tab UI → the built output (artifacts\dev\app by default)
```

A build lands in `artifacts\dev\app` and never in the app folder, which is the one a running NetPI loads its plugins
from: rebuilding a plugin (or a test project that references one) would otherwise take its tools away from every
open chat. Installing is `.\build.ps1 -Publish`, into the running app's own folder (its `<home>\server.json` says where
that is, so a build in a worktree installs into the app that is really running; `-AppDir` overrides it, and
`NETPI_APP_DIR` does the same for the npm bundle scripts). One install at a time: the app folder's `.install.lock`, which a starting NetPI also takes (waiting up to a minute) while it installs what waits in `.pending`.

**A reload is a swap, not a restart.** The new version starts while the old one is still serving: a registration of the
same name takes over as soon as the new instance makes it, and only then are the old registrations disposed. So a tool
is never absent, a chat that calls a model in the middle sees no change at all (no "tools changed" notice), and a new
version that fails to load or start leaves the running one alone — `plugins.reloaded` says `kind: "reload-failed"` and
the plugin's `error` says why. What this asks of a plugin: `StartAsync` must tolerate a previous instance of *itself*
being alive for a few milliseconds, so don't take an exclusive resource (a port, a lock) for the whole process without
a named lock — a file that only one instance writes briefly is fine. `StopAsync` and the `Stopping` token of the
replaced version arrive *after* the new one is up, so cleanup there must tolerate the overlap too. Reloads are
serialised, one at a time.

A new host goes in place for the next start (the running files are moved into the app folder's `.old`). After a
contract change every plugin's output changes, and reloaded they would run on the old contracts, so they wait in
`.pending` and the next start installs them (`PendingBuild`, before anything loads). `-Publish -NextStart` does that
with every change, the web UI included: the running NetPI gets nothing, and `-Pending` lists what waits.

Every session shares the one running app, so a reload reaches all of them — a git worktree isolates the files, not the
process. Two things keep that cheap: the swap above, and **`plugins.quiet`** (settings), which records reloads instead
of applying them, so nothing moves under a running chat at all until you switch it off — then everything that piled up
is applied in start order.

`/reload [pluginId]` in the chat, or the Diagnostics tab, reloads plugins on demand.

Runtime records distinct sent system prefixes in durable session metadata (`sentPromptHistory`) at the model-call boundary. Fork templates trim this history to `upToSeq` and inherit that prefix even when Context was absent. Current profile setup is retained; an explicit profile switch or prompt reset in the fork renders the current identity. A fork before any recorded call renders fresh. Legacy sessions without sent history keep their known fallback; historic prefixes cannot be reconstructed retroactively.
