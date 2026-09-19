Yes. I think the design is sufficiently specified now that implementation can start without inventing product behavior.

# netPI implementation plan

## 1. Design goals

netPI should be a **small .NET 10 agent harness whose useful behavior lives almost entirely in hot-reloadable plugins**.

The priorities, in order, are:

1. Fast agent loop with very little abstraction in the hot path.
2. Fast WebUI even for very long sessions.
3. Manual hot reload of practically everything.
4. Clean provider-independent transcript/message format.
5. Pi-like coding-agent behavior rather than building a giant framework.
6. AiProxy as the single model/provider surface.
7. SQLite persistence from day one.
8. Simple extension points for future functionality without bloating the core.

This matches Pi's current philosophy: small core, workflow behavior pushed into extensions. Pi currently exposes `read`, `bash`, `powershell` on Windows, `edit`, `write`, `grep`, `find`, and `ls` as built-ins, and deliberately leaves things such as background bash to extensions. ([Pi][1])

---

# 2. Solution layout

Only these two projects are permanently loaded:

```text
src/
  netPI.Host/
  netPI.Abstractions/
```

Everything else is reloadable:

```text
plugins/
  netPI.Agent/
  netPI.Provider.AiProxy/
  netPI.Storage.Sqlite/
  netPI.Context.Pi/
  netPI.AutoCompact/
  netPI.Retry/
  netPI.Web/

  netPI.Tool.Read/
  netPI.Tool.Write/
  netPI.Tool.Edit/
  netPI.Tool.Grep/
  netPI.Tool.Bash/
  netPI.Tool.PowerShell/

  netPI.BackgroundTasks/
```

Frontend source:

```text
web/
  netpi-web/
    Svelte 5
    TypeScript
    Vite
    TanStack Virtual
```

Runtime directory:

```text
~/.netpi/
  config.json
  netpi.db
  logs/
  plugin-cache/
```

Project configuration/context:

```text
<workspace>/
  .netpi/
    AGENTS.md
    AGENTS.override.md
    SYSTEM.md
    APPEND_SYSTEM.md
```

No branching, MCP, permissions UI, subagents, plans, to-dos, skills system, etc. in v1 unless needed later.

---

# 3. Non-reloadable host

`netPI.Host` must remain boring.

Its responsibilities are only:

```text
process lifetime
plugin discovery/loading
collectible AssemblyLoadContexts
service registry
event bus
reload coordination
shutdown
```

Startup essentially becomes:

```csharp
var runtime = new HostRuntime();

await runtime.Plugins.LoadAllAsync();
await runtime.Plugins.StartAllAsync();

await runtime.Lifetime.WaitForShutdownAsync();

await runtime.Plugins.StopAllAsync();
```

The Web server is **not** owned by Host. `netPI.Web` starts/stops its own ASP.NET Core server, allowing the entire Web layer to unload.

Avoid:

* Autofac
* MediatR
* EF Core
* SignalR
* generic host machinery unless actually necessary
* reflection-heavy request pipelines

Use the BCL and small focused libraries.

---

# 4. Abstractions

`netPI.Abstractions` is effectively the ABI between collectible ALCs.

Keep it small and stable.

Primary contracts:

```text
INetPiPlugin
IServiceRegistry
IEventBus

IAgentRuntime
IAgentState

IModelProvider
IModelCatalog

ISessionStore

IToolRegistry
IAgentTool

ISystemPromptProvider

ISteeringQueue

IShellCommandResolver

IBackgroundJobManager
```

And stable DTOs:

```text
AgentMessage
MessagePart
ToolDefinition
ToolResult
ModelInfo
ModelRequest
ModelEvent
SessionInfo
SessionEntry
AgentEvent
```

No implementation-specific types cross a plugin boundary.

Especially never expose:

```text
AiProxyProvider
SqliteConnection
WebApplication
Process
Svelte concepts
plugin implementation delegates
```

from one ALC into another.

---

# 5. Plugin lifecycle

Use collectible `AssemblyLoadContext`.

Each plugin directory contains its DLL plus its private dependencies:

```text
plugins/
  netPI.Tool.Read/
    netPI.Tool.Read.dll
    dependency-a.dll
```

Plugin interface:

```csharp
public interface INetPiPlugin
{
    PluginInfo Info { get; }

    ValueTask LoadAsync(
        IPluginContext context,
        CancellationToken cancellationToken);

    ValueTask StartAsync(
        CancellationToken cancellationToken);

    ValueTask StopAsync(
        CancellationToken cancellationToken);

    ValueTask UnloadAsync(
        CancellationToken cancellationToken);
}
```

`LoadAsync` registers things.

`StartAsync` starts active resources such as the Web server.

`StopAsync` drains/stops them.

`UnloadAsync` releases anything remaining.

### Windows-friendly loading

Do **not** execute plugins directly from their build output.

On load/reload:

```text
plugin build directory
       ↓ copy
~/.netpi/plugin-cache/<plugin>/<generation>/
       ↓
load copied assembly into collectible ALC
```

This means Visual Studio/`dotnet build` can freely overwrite the original DLL while netPI is running.

The custom ALC uses `AssemblyDependencyResolver`, except:

```text
netPI.Abstractions
```

must always resolve from the default ALC. Otherwise interface type identity breaks.

---

# 6. Plugin leases and reload

Every registered service/tool/event handler belongs to a plugin.

Acquiring one creates a lease:

```csharp
await using var lease =
    services.Acquire<IModelProvider>("aiproxy");

var provider = lease.Value;
```

The lease increments that plugin's active-use counter.

Manual reload:

```text
Reload requested
      ↓
Active → Draining
      ↓
new leases denied
      ↓
existing leases continue
      ↓
refcount reaches zero
      ↓
StopAsync
      ↓
remove registrations/subscriptions
      ↓
UnloadAsync
      ↓
ALC.Unload()
      ↓
load newest DLL
      ↓
Active
```

That gives exactly the requested semantics: **reload when that plugin is idle**.

Special reload policies:

```text
normal plugin       PluginIdle
netPI.Agent         AgentIdle
netPI.Web           AgentIdle
```

Web waits for the active agent run to finish before unloading, then connected browsers reconnect automatically.

No filesystem watcher. Reload is explicitly triggered from the UI/API.

---

# 7. Avoiding ALC leaks

The service registry and event bus need strict rules.

Plugin registration returns an owned registration:

```csharp
IDisposable Register<T>(string id, T instance);
```

The Host records:

```text
pluginId → registrations
pluginId → event subscriptions
pluginId → active leases
```

On unload every registration is removed automatically even if the plugin forgot.

Never allow plugin code to attach directly to static events in Host.

In development builds, keep a `WeakReference` to unloaded ALCs and expose:

```text
Plugins
  Tool.Read     gen 17    collected
  Tool.Grep     gen 9     collected
  Agent         gen 4     unload pending
```

That will catch accidental ALC retention early.

---

# 8. Message model

Follow Pi's provider-neutral approach.

```csharp
public sealed record AgentMessage(
    string Id,
    MessageRole Role,
    IReadOnlyList<MessagePart> Parts,
    DateTimeOffset CreatedAt);
```

Parts:

```csharp
public abstract record MessagePart;

public sealed record TextPart(string Text) : MessagePart;

public sealed record ThinkingPart(
    string Text,
    string? Signature = null) : MessagePart;

public sealed record ToolCallPart(
    string Id,
    string Name,
    JsonElement Arguments) : MessagePart;

public sealed record ToolResultPart(
    string ToolCallId,
    string ToolName,
    IReadOnlyList<MessagePart> Parts,
    bool IsError = false) : MessagePart;

public sealed record ImagePart(
    string MimeType,
    byte[] Data) : MessagePart;
```

Images can exist in the model from day one even if attachment UX isn't a first milestone.

No OpenAI-specific objects enter the session transcript.

---

# 9. Streaming event model

Provider output is normalized immediately.

```csharp
abstract record ModelEvent;

record ModelStarted(...) : ModelEvent;
record ThinkingStarted(...) : ModelEvent;
record ThinkingDelta(string Text) : ModelEvent;
record ThinkingCompleted(...) : ModelEvent;

record TextStarted(...) : ModelEvent;
record TextDelta(string Text) : ModelEvent;
record TextCompleted(...) : ModelEvent;

record ToolCallStarted(string Id, string Name) : ModelEvent;
record ToolCallArgumentsDelta(string Id, string Delta) : ModelEvent;
record ToolCallCompleted(...) : ModelEvent;

record UsageUpdated(...) : ModelEvent;
record ModelCompleted(AgentMessage Message) : ModelEvent;
```

The same normalized events feed:

```text
Agent
SQLite
WebSocket
UI
```

The WebUI never parses OpenAI stream chunks.

---

# 10. Agent loop

Keep the actual loop exceptionally small.

Conceptually:

```csharp
while (true)
{
    await boundary.BeforeModelAsync(context);

    var assistant =
        await RunModelAsync(context, cancellationToken);

    await session.AppendAsync(assistant);

    if (assistant.ToolCalls.Count == 0)
        break;

    var results =
        await ExecuteToolsAsync(
            assistant.ToolCalls,
            context,
            cancellationToken);

    await session.AppendAsync(results);

    await ProcessTurnBoundaryAsync(
        context,
        cancellationToken);
}
```

Execution path:

```text
USER
 ↓
build context
 ↓
MODEL STREAM
 ↓
assistant completed
 ↓
tool calls?
 │
 ├── no ─────────────────────────→ idle
 │
 └── yes
      ↓
 sequential preflight
      ↓
 parallel execution
      ↓
 append results in call order
      ↓
 ───────────── TURN BOUNDARY ─────────────
      ↓
 drain steering
      ↓
 auto-compaction hook
      ↓
 next model call
```

No retry logic, compaction algorithm, OpenAI code, SQLite code, or WebSocket code lives here.

---

# 11. Tool-call parallelism

Copy Pi's behavior.

For an assistant response containing:

```text
read(a)
grep(b)
bash(c)
```

first resolve/acquire all three tool plugin leases.

Then preflight sequentially:

```text
preflight read
preflight grep
preflight bash
```

There is no approval UI currently, so preflight mainly validates arguments and runtime prerequisites.

Then execute concurrently:

```csharp
var executions = preparedCalls.Select(
    x => x.Tool.ExecuteAsync(x.Context, ct));

var results = await Task.WhenAll(executions);
```

Results are inserted into the transcript in the **original call order**, not completion order.

Hold each tool's lease from resolution through execution completion, preventing it from unloading halfway through an invocation.

---

# 12. Steering

Each active session owns:

```csharp
Channel<QueuedUserMessage>
```

When idle:

```text
Send → new user turn
```

When busy:

```text
Send/Steer → steering queue
```

A steering message never cancels the currently running tool batch.

Example:

```text
assistant calls tool A + B
              ↓
user: "Actually use the other config file"
              ↓
A + B finish
              ↓
tool results appended
              ↓
steering message appended as User
              ↓
next model call sees it
```

This closely matches Pi's concept of queued steering between model turns. Pi's extension API also exposes lifecycle interception around agent starts, tool calls and context modification, which is the same general boundary-oriented design we want. ([Pi][2])

Expose queued steering visibly in the composer:

```text
Queued for next turn:
"Don't modify Foo.cs..."
```

Cancellation remains distinct:

```text
Stop button → cancel active run
```

---

# 13. AiProxy provider

`netPI.Provider.AiProxy` is the only v1 provider.

It has four responsibilities:

```text
GET /v1/models
Chat Completions request serialization
Chat Completions stream parsing
netPI ↔ OpenAI message/tool translation
```

It does **not** understand:

```text
NInfer
llama.cpp
backend loading
routing
policy aliases
loaded/stopped/offline
AiProxy internal telemetry
```

AiProxy owns those.

## Model discovery

Refresh only:

```text
netPI startup

and

model picker opened
```

No polling.

Provider holds the last successful in-process catalog.

```csharp
public interface IModelProvider
{
    IReadOnlyList<ModelInfo> CachedModels { get; }

    ValueTask<IReadOnlyList<ModelInfo>>
        RefreshModelsAsync(CancellationToken ct);

    IAsyncEnumerable<ModelEvent>
        StreamAsync(ModelRequest request, CancellationToken ct);
}
```

If model-picker refresh fails:

```text
keep old catalog
show non-blocking refresh error
```

## Model metadata

Retain useful enriched AiProxy information:

```csharp
public sealed record ModelInfo(
    string Id,
    int? ContextWindow,
    int? MaxOutputTokens,
    IReadOnlySet<InputModality> InputModalities,
    ReasoningProfile? Reasoning);
```

Do not retain backend status.

The AiProxy reasoning JSON should be parsed according to its actual schema rather than model-name heuristics. Keep unmapped fields via `JsonExtensionData` so AiProxy can evolve without breaking netPI.

Model UI becomes data-driven:

```text
model supports:
  context = 262144
  reasoning = off/low/medium/high
  image = yes
```

Therefore:

```text
context meter      uses ContextWindow
compaction          uses ContextWindow
reasoning dropdown  uses ReasoningProfile
image controls      use InputModalities
max output          uses MaxOutputTokens
```

No netPI capability table.

---

# 14. Chat Completions

Initial endpoint:

```text
POST /v1/chat/completions
stream: true
```

Provider maps:

```text
system prompt
user/assistant/tool messages
thinking/reasoning
parallel tool calls
tool schemas
reasoning level
usage
```

Streaming tool-call argument fragments must be accumulated independently by call index/ID.

Do **not** attempt to parse each JSON argument fragment independently.

Only deserialize after the tool call finishes.

Preserve assistant reasoning/thinking content in the internal message so it can be round-tripped on subsequent requests where AiProxy/model semantics require it.

---

# 15. Pi-style system prompt plugin

`netPI.Context.Pi` owns prompt construction.

It should be **very similar in structure and intent to Pi, but written for netPI rather than copied literally**.

Pi's current system-prompt inputs include selected tools, tool descriptions/guidelines, cwd, context files, custom sections and appended prompt text. Its prompt can also be modified through extensions without stuffing that behavior into the agent loop. ([Pi][2])

Suggested sections:

```text
base identity/instructions

available tools
  concise descriptions
  behavioral guidance

working environment
  OS
  shell availability
  cwd/workspace

project context
  loaded .netpi/AGENTS.md files

current date

optional APPEND_SYSTEM
```

Keep it minimal. Avoid gigantic boilerplate.

---

# 16. `.netpi/AGENTS.md`

Adapt Pi's layering semantics to netPI's directory convention.

For workspace:

```text
C:\src\foo\bar
```

discover from broad → specific:

```text
~/.netpi/AGENTS.md

C:\.netpi\AGENTS.md
C:\src\.netpi\AGENTS.md
C:\src\foo\.netpi\AGENTS.md
C:\src\foo\bar\.netpi\AGENTS.md
```

If a directory has:

```text
.netpi/AGENTS.override.md
```

use that instead of its ordinary `AGENTS.md`.

More specific context layers after broader context.

Pi currently layers global, ancestor and current-directory context instructions and supports an override file for a directory; we're preserving that behavior while changing the location to `.netpi`. ([Pi][3])

Cache based on:

```text
path
mtime
length
```

and re-read only when changed.

Context should refresh at the beginning of a new user turn, not every streamed token or tool call.

---

# 17. System prompt overrides

Support:

```text
~/.netpi/SYSTEM.md
~/.netpi/APPEND_SYSTEM.md

<workspace>/.netpi/SYSTEM.md
<workspace>/.netpi/APPEND_SYSTEM.md
```

Semantics:

```text
SYSTEM.md
  replaces netPI's base prompt

APPEND_SYSTEM.md
  appends user instructions

AGENTS context
  still added regardless
```

Pi likewise keeps context files available when the base system prompt is overridden. ([Pi][1])

Project version overrides global for `SYSTEM.md`; append files can layer global → project.

---

# 18. Tool plugins

Initial active tool set:

```text
read
write
edit
grep
bash
powershell
```

Pi currently exposes this same core family, plus `find` and `ls`; we'll omit those two initially because you didn't ask for them. ([Pi][1])

All relative paths resolve against the session workspace.

Absolute paths are allowed.

`..` may escape the workspace.

No permission prompt.

---

# 19. `read`

Minimal schema:

```json
{
  "path": "src/Foo.cs",
  "offset": 120,
  "limit": 200
}
```

Behavior:

```text
relative → resolve from workspace
absolute → use unchanged
text only
line numbers
bounded output
clear truncation marker
```

Avoid returning multi-megabyte files accidentally.

Large reads should tell the model where to continue:

```text
Showing lines 120-319 of 2841.
Use offset=320 to continue.
```

Binary files return a useful error rather than garbage.

---

# 20. `write`

Schema:

```json
{
  "path": "src/Foo.cs",
  "content": "..."
}
```

Whole-file create/replace only.

Create parent directories where sensible.

UTF-8.

Do not overload `write` with edit semantics.

---

# 21. `edit`

Exact replacement:

```json
{
  "path": "src/Foo.cs",
  "oldText": "...",
  "newText": "..."
}
```

Rules:

```text
0 matches → fail
1 match   → replace
>1 match  → fail as ambiguous
```

No fuzzy matching.

No hidden AI patching.

Preserve the rest of the file byte-for-byte as much as normal text handling allows.

---

# 22. `grep`

Schema kept small:

```json
{
  "pattern": "IModelProvider",
  "path": "src",
  "glob": "*.cs"
}
```

Support regex and sensible result limits.

For speed:

```text
if rg exists → use ripgrep
otherwise    → managed .NET fallback
```

Normalize both paths into the same returned result format:

```text
src/Foo.cs:42: public interface IModelProvider
```

This makes netPI portable without sacrificing speed on developer machines.

---

# 23. Bash detection

`netPI.Tool.Bash` detects its backend once at load and re-probes on plugin reload.

### Windows

Preference:

```text
1. explicit config override
2. usable native bash on PATH
3. Git for Windows bash
4. other native/MSYS bash
5. WSL bash
```

Native Windows-accessible Bash is preferred over WSL because workspace and absolute Windows paths work naturally.

Probe candidates rather than assuming what `bash.exe` is.

Record:

```text
executable
flavor
version
invocation form
```

Example:

```text
bash
Git Bash
5.2.x
```

If WSL is selected, the resolver owns Windows ↔ WSL working-directory conversion.

### Linux/macOS

```text
config override
PATH bash
/bin/bash
```

---

# 24. PowerShell detection

Tool remains named:

```text
powershell
```

Detection:

```text
1. explicit config override
2. pwsh
3. powershell.exe on Windows
```

Prefer modern PowerShell (`pwsh`) whenever available.

UI/plugin diagnostics can show:

```text
PowerShell: pwsh 7.x
Bash: Git Bash 5.x
```

---

# 25. Foreground shell execution

Each shell tool executes one foreground command.

Example:

```json
{
  "command": "dotnet test",
  "timeoutMs": 120000
}
```

Runtime:

```text
working directory = session workspace
stdout streamed
stderr streamed
exit code returned
cancellable
timeout supported
```

Each invocation gets a fresh shell.

Therefore:

```text
cd foo
```

only affects that invocation.

The next call starts in the workspace again, as specified.

---

# 26. Shell-command resolver

Separate resolution from process ownership.

Stable abstraction:

```csharp
public interface IShellCommandResolver
{
    string ShellId { get; }

    ValueTask<ResolvedCommand> ResolveAsync(
        string command,
        string workingDirectory,
        CancellationToken ct);
}
```

Return only plain data:

```csharp
public sealed record ResolvedCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string,string>? Environment);
```

This becomes important for background jobs.

---

# 27. Background tasks

`netPI.BackgroundTasks` is independent from foreground shell execution, matching Pi's own choice not to mix background bash into the core tool. ([Pi][1])

Register:

```text
background_start
background_output
background_list
background_kill
```

Start:

```json
{
  "shell": "powershell",
  "command": "dotnet watch run"
}
```

The background plugin briefly leases the corresponding shell resolver:

```text
acquire PowerShell plugin
 ↓
resolve executable + arguments
 ↓
copy ResolvedCommand
 ↓
release PowerShell plugin
 ↓
BackgroundTasks starts Process
```

That is important: once launched, the job belongs entirely to `BackgroundTasks`.

Therefore:

```text
background job running
+
reload PowerShell plugin
```

is allowed.

Existing process continues unchanged.

---

# 28. Background process storage

Jobs are runtime-only.

```text
BackgroundJob
  id
  shell
  command
  pid
  startedAt
  exitCode?
  state
```

Capture stdout/stderr into a bounded ring/spool.

For example:

```text
recent in-memory tail
+
optional temp log file
```

`background_output` accepts an offset/cursor so the model doesn't repeatedly receive the complete output.

On netPI shutdown:

```text
kill all remaining background jobs
wait briefly
force kill process tree if needed
```

They do **not** survive netPI restart.

---

# 29. SQLite session storage

Use `Microsoft.Data.Sqlite`, not EF Core.

Single DB:

```text
~/.netpi/netpi.db
```

Startup pragmas:

```sql
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA foreign_keys=ON;
```

Core schema:

```sql
sessions
--------
id
title
workspace
provider_id
model_id
reasoning_level
created_at
updated_at
last_sequence

session_entries
---------------
id
session_id
sequence
entry_type
created_at
payload_json

settings
--------
key
value_json
```

Indexes:

```text
sessions(updated_at)
session_entries(session_id, sequence)
```

No branching tables yet.

---

# 30. Append-only sessions

Never continually rewrite a giant conversation JSON blob.

Store entries append-only:

```text
user_message
assistant_message
tool_result
compaction
model_change
session_metadata
```

This provides:

```text
fast append
safe crash recovery
easy pagination
simple future migrations
future branch support if desired
```

The active model context is reconstructed from session entries plus the latest relevant compaction.

---

# 31. Compaction entries

Never delete old transcript data because it was compacted.

Persist something like:

```json
{
  "type": "compaction",
  "summary": "...",
  "summarizedThroughSequence": 419,
  "retainedFromSequence": 387
}
```

The UI can still display the original entire session.

The LLM context builder instead sees:

```text
system prompt
compaction summary
retained recent messages
new entries
```

This cleanly separates:

```text
historical UI transcript
vs
active model context
```

---

# 32. AutoCompact plugin

`netPI.AutoCompact` subscribes to turn/context events.

Pi currently triggers automatic compaction when context use exceeds `contextWindow - reserveTokens`, checking after tool results before the next assistant response. Its defaults are currently 16,384 reserved tokens and 20,000 recent tokens retained. ([Pi][4])

Use that as the initial netPI behavior:

```json
{
  "plugins": {
    "autoCompact": {
      "enabled": true,
      "reserveTokens": 16384,
      "keepRecentTokens": 20000
    }
  }
}
```

Trigger:

```text
estimatedContext > model.ContextWindow - reserveTokens
```

Context measurement:

```text
last provider-reported prompt usage
+
estimate of messages/tool results added since then
```

Avoid bringing in model-specific tokenizers initially.

Provider usage is authoritative where available; incremental estimation only covers content added afterward.

---

# 33. Compaction execution

The plugin asks the active provider/model for a summary with tools disabled.

Structured summary should preserve coding state:

```text
goal
user instructions
important decisions
work completed
current implementation state
errors/issues
files read
files modified
commands/results
next actions
```

Keep the most recent `keepRecentTokens` approximately intact.

After compaction:

```text
persist CompactionEntry
rebuild active context
resume same agent run
```

The Agent plugin itself only sees:

```text
context changed
continue
```

---

# 34. Retry plugin

`netPI.Retry` owns retry policy around model requests.

Candidate retryable categories:

```text
connection reset
timeout
HTTP 408
HTTP 429
HTTP 5xx
temporary stream interruption before completion
```

Non-retry by default:

```text
bad request
authentication
unknown model
invalid tool/schema request
other ordinary 4xx
```

Config section:

```json
{
  "plugins": {
    "retry": {
      "maxAttempts": 3,
      "baseDelayMs": 500,
      "maxDelayMs": 5000
    }
  }
}
```

The retry plugin returns a policy decision. Agent does not contain status-code conditions.

If the model response has already streamed user-visible partial content, don't silently duplicate it. Mark the failed attempt and restart in a clearly defined manner.

---

# 35. Global configuration

One file:

```text
~/.netpi/config.json
```

Each plugin owns its own section:

```json
{
  "host": {
    "pluginDirectory": "./plugins"
  },

  "plugins": {
    "aiProxy": {
      "baseUrl": "http://127.0.0.1:8080",
      "apiKey": null
    },

    "web": {
      "listen": "127.0.0.1",
      "port": 7331
    },

    "autoCompact": {
      "enabled": true,
      "reserveTokens": 16384,
      "keepRecentTokens": 20000
    },

    "retry": {
      "maxAttempts": 3
    },

    "bash": {
      "executable": null
    },

    "powershell": {
      "executable": null
    }
  }
}
```

Plugin config is accessed as raw/typed JSON through a stable config service.

Plugins should tolerate unknown properties for forward compatibility.

---

# 36. Web plugin

Use ASP.NET Core **Minimal APIs** inside `netPI.Web`.

Responsibilities:

```text
serve compiled Svelte files
WebSocket endpoint
small REST/bootstrap endpoints if useful
session history pagination
model refresh endpoint
plugin reload commands
```

No MVC.

No Razor.

No SignalR.

Main transport:

```text
/ws
```

native WebSocket + JSON.

---

## Replace §37 — Frontend layout

Use:

```text
Svelte 5
TypeScript
Vite
@tanstack/svelte-virtual
native WebSocket
```

The **primary screen is the conversation**, not a dashboard.

```text
┌─────────────────────────────────────────────────────────────┐
│ netPI                     session / workspace        ⋯       │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│                                                             │
│   User                                                      │
│   Implement the provider...                                 │
│                                                             │
│   Assistant                                                 │
│   ▼ Thinking                                                │
│     ...                                                     │
│                                                             │
│   I'll inspect the existing implementation.                 │
│                                                             │
│   ┌─ read ─────────────────────────────────────────────┐     │
│   │ src/Foo.cs                              42 lines  │     │
│   └───────────────────────────────────────────────────┘     │
│                                                             │
│   ...                                                       │
│                                                             │
│                                                             │
├─────────────────────────────────────────────────────────────┤
│  Message, / command...                                      │
│                                                             │
│  +   /     @        AiProxy/model-name   Medium ▾     ↑     │
│                                                    Send     │
└─────────────────────────────────────────────────────────────┘
```

No permanently visible plugin panel, diagnostics pane, model sidebar, etc.

Those are secondary screens/popovers.

The transcript gets essentially all available vertical space.

---

## Replace the previous session-sidebar concept

Sessions should behave like other harnesses: accessible from a small control/menu rather than consuming a large permanent left column.

Something like:

```text
┌─────────────────────────────────────────────────────┐
│ ☰  netPI   My session                         ⋯     │
└─────────────────────────────────────────────────────┘
```

`☰` can open a temporary sidebar containing:

```text
New session

Recent sessions
────────────────
netPI provider work
radio work
HA changes
...

Plugins
Settings
```

Close it and you're back to a full-width chat.

On a wide screen we can optionally pin it later, but the default UI should remain **chat-first**.

---

## Replace composer section

The composer should be the main control surface, like in your screenshot.

```text
╭─────────────────────────────────────────────────────────────╮
│ Message or run a task, / commands, @ files or sessions      │
│                                                             │
│                                                             │
│ +    /    @                qwen3.8-27b   Medium ▾      ↑    │
╰─────────────────────────────────────────────────────────────╯
```

Bottom row:

```text
left                                              right
─────────────────────────────────────────────────────────
+       attachments/future

/       commands

@       file/session/context picker


                         model selector
                         reasoning selector
                         Send / Steer button
```

Model and reasoning belong **inside the composer**, not in a top settings bar.

---

## Send / Steer behavior

There should be **one primary button whose meaning follows agent state**.

When idle:

```text
┌──────┐
│  ↑   │  Send
└──────┘
```

Submitting creates a normal user turn.

When agent is running:

```text
┌──────┐
│  ↑   │  Steer
└──────┘
```

Submitting queues that prompt for the next turn boundary.

So there is no need for separate Send and Steer buttons.

Internally:

```ts
if (agentState === "Idle")
    send("chat.send", text);
else
    send("chat.steer", text);
```

The button can use the same icon and show `Send` / `Steer` as tooltip or nearby text.

A separate stop button appears while running:

```text
qwen3.8-27b   Medium ▾       ■   ↑
                              Stop Steer
```

This keeps **cancel** and **steer** distinct.

---

## Commands

The input should understand `/` commands directly.

Typing:

```text
/
```

opens an autocomplete menu above the composer:

```text
/model          Select model
/reasoning      Set reasoning level
/new            New session
/compact        Compact context now
/reload         Reload plugin
/plugins        Plugin manager
/workspace      Change workspace
/clear          Clear/new conversation
```

Plugins should be able to register commands:

```csharp
public interface ICommandRegistry
{
    IDisposable Register(CommandDefinition command);
}
```

So `/compact` can actually belong to `AutoCompact.Plugin`, `/reload` to the runtime/plugin management surface, etc.

This keeps commands hot-reloadable too.

---

## `@` picker

Typing `@` should open a lightweight searchable picker:

```text
@src/Foo.cs
@src/AgentLoop.cs
@session:old-session
```

For v1 I'd support:

```text
files
sessions
```

without building a giant resource system.

Selecting a file inserts a structured reference into the outgoing message rather than forcing the user to manually write paths.

The actual file still gets read through the normal context/tool machinery.

---

## Model selector

The bottom-right model label:

```text
qwen3.8-27b ▾
```

opens the model picker.

Opening it performs the already-decided:

```text
show cached models immediately
        +
GET /v1/models
        ↓
refresh list
```

Example:

```text
Select model
────────────────────────────

Search models...

qwen3.8-27b
262k · reasoning · vision

gemma-4-26b
128k · reasoning

qwen3.8-9b
128k
```

Don't show AiProxy backend/status information.

Only useful capabilities:

```text
context
reasoning
modalities
possibly max output
```

---

## Reasoning selector

Directly beside model:

```text
qwen3.8-27b   Medium ▾
```

Clicking `Medium` shows only the reasoning levels advertised by AiProxy for that model:

```text
Off
Low
Medium ✓
High
```

If the selected model does not advertise reasoning:

```text
qwen3.8-9b
```

then don't show a meaningless disabled reasoning selector. Hide it or show a subtle non-interactive value.

---

## Conversation rendering

Keep the previous virtualization design, but visually make it a normal chat transcript.

Blocks:

```text
UserMessage

AssistantMessage
  ThinkingBlock
  TextBlock
  ToolCallBlock
  TextBlock
```

Thinking should be collapsible:

```text
▼ Thinking                         4.8s
  I'll inspect the provider...
```

After completion it can default to collapsed:

```text
▶ Thought for 4.8s
```

Tool calls should be compact too:

```text
┌ read  src/AgentLoop.cs                         ✓ 12 ms ┐
└────────────────────────────────────────────────────────┘
```

Expandable for input/output.

Shell:

```text
┌ powershell  dotnet test                       ✓ 3.2s ┐
│ 87 passed                                             │
└───────────────────────────────────────────────────────┘
```

Background:

```text
┌ background_start  dotnet watch run            ● bg_4 ┐
└───────────────────────────────────────────────────────┘
```

The transcript shouldn't look like an admin console.

---

## Status line

A subtle line around/below the composer can expose the useful nerdy information without taking over the UI, similar to your screenshot:

```text
28 turns · 477 tool steps · 166 tok/s · 184k / 262k · cache 79%
```

Useful candidates:

```text
turns
tool calls/steps
current context / context limit
prefill/decode rate if AiProxy exposes it
cache hit if AiProxy exposes it
elapsed run time
```

This information is optional and unobtrusive.

No giant metrics cards.

---

## Plugin/settings UI

Move the plugin controls out of the main screen.

From `⋯`:

```text
Plugins
Settings
Session details
Diagnostics
```

Plugin page:

```text
Plugins

Agent                 Active        Reload
Provider.AiProxy      Active        Reload
Storage.Sqlite        Active        Reload
Context.Pi            Active        Reload
AutoCompact           Active        Reload
Retry                 Active        Reload
Read                   Active        Reload
...

                                  Reload All
```

It's an administrative screen you visit when needed, not part of the working chat layout.

---

## Updated Svelte component structure

Replace the earlier tree with approximately:

```text
App
├── HarnessHeader
├── ConversationViewport
│   └── VirtualConversation
│       ├── UserMessage
│       └── AssistantMessage
│           ├── ThinkingBlock
│           ├── MarkdownBlock
│           └── ToolCallBlock
│
├── Composer
│   ├── PromptEditor
│   ├── AttachmentButton
│   ├── CommandMenu
│   ├── MentionMenu
│   ├── ModelPicker
│   ├── ReasoningPicker
│   ├── StopButton
│   └── SendSteerButton
│
├── RunStatusLine
│
└── Overlays
    ├── SessionDrawer
    ├── PluginManager
    ├── Settings
    └── Diagnostics
```


---

# 38. Long-session strategy

Do not load/render an entire giant session every time.

Use both:

### Server pagination

Opening session:

```text
load latest ~200 entries
```

Scrolling upward:

```text
GET older entries before sequence X
```

### DOM virtualization

Even if thousands of entries are loaded client-side:

```text
TanStack Virtual
```

mounts only visible rows plus overscan.

### Immutable completed rows

Once a transcript block completes:

```text
do not update its object
do not rerender it
```

Only active objects mutate.

---

# 39. Streaming UI optimization

Model providers may emit tiny deltas faster than the browser should render them.

Web plugin coalesces compatible deltas for a very short window, e.g. roughly one browser frame:

```text
provider:
h
e
l
l
o

      ↓

WebSocket:
"hello"
```

Do this independently for:

```text
thinking.delta
text.delta
tool.output
```

Aim for responsive updates around 16–25 ms rather than hundreds of DOM operations per second.

This won't affect persisted content or agent behavior.

---

# 40. Markdown rendering

Do not reparse a 20,000-character answer on every token.

During streaming:

```text
append text
render active block cheaply
```

At sensible throttled intervals or block completion:

```text
Markdown parse
```

Completed block becomes stable.

Tool blocks should default collapsed for large output.

Very large tool results should render only a bounded preview until expanded.

---

# 41. WebSocket protocol

Client commands:

```text
chat.send
chat.steer
agent.cancel

session.create
session.open
session.rename

models.refresh

plugin.reload
plugin.reloadAll

background.*   only if UI needs direct controls
```

Server events:

```text
agent.state

session.created
session.entry
session.updated

assistant.started
thinking.delta
thinking.completed
text.delta
text.completed

tool.started
tool.output
tool.completed

assistant.completed
usage.updated

models.updated
models.refreshFailed

plugin.state
plugin.reloaded
plugin.reloadFailed

error
```

Every message uses:

```json
{
  "type": "...",
  "requestId": "...",
  "sessionId": "...",
  "payload": {}
}
```

where applicable.

No OpenAI wire objects reach the browser.

---

# 42. Model picker behavior

At netPI startup:

```text
AiProxy GET /v1/models
 ↓
cache
```

When model picker opens:

```text
models.refresh
 ↓
GET /v1/models
 ↓
models.updated
```

No periodic refresh.

Opening the picker should display cached models immediately, then refresh asynchronously so the UI doesn't feel blocked.

Reasoning choices update when model changes.

---

# 43. Session/workspace model

Every session has exactly one workspace.

```text
session.workspace
```

Tools resolve relative paths from there.

Foreground shells start there.

Context discovery starts there.

Workspace does not silently change when a shell command runs `cd`.

Changing workspace should create/change explicit session metadata, not happen as a side effect of tool execution.

For v1, workspace selection can be a validated path field. A richer server-side folder browser can be added without affecting the agent architecture.

---

# 44. Agent state machine

Expose a small state enum:

```text
Idle
Preparing
CallingModel
ExecutingTools
Compacting
Retrying
Cancelling
```

Do not turn this into a workflow engine.

It exists for:

```text
UI status
plugin reload gating
shutdown
diagnostics
```

Agent plugin holds its own plugin lease for the duration of a run, so Agent cannot reload mid-run.

---

# 45. Event bus

Use typed events.

Examples:

```text
SessionOpened
AgentStarting
BeforeModelRequest
ModelStreamEvent
AssistantCompleted

BeforeToolBatch
BeforeToolCall
AfterToolCall
AfterToolBatch

TurnBoundary
ContextBuilding
ContextBuilt

ModelRequestFailed
AgentCompleted
AgentCancelled
```

Handlers execute in deterministic priority/order.

Event subscriptions are plugin-owned and automatically removed during unload.

Do not use the event bus for high-volume text deltas internally when a direct stream is simpler. Use direct streaming for the hot data path; events for lifecycle/extensibility.

That distinction keeps the system fast.

---

# 46. System-prompt extensibility

The prompt plugin should expose structured inputs rather than only one giant string:

```csharp
SystemPromptBuilder
{
    BasePrompt
    Tools
    ToolGuidelines
    Environment
    ContextFiles
    CustomSections
    AppendPrompt
}
```

Then future plugins can say:

```text
add section
add guideline
modify tool instructions
```

without regex-editing a blob.

Pi currently exposes structured system-prompt options to extensions for essentially this reason. ([Pi][2])

Final flattening to text happens immediately before provider request generation.

---

# 47. Logging

Use `Microsoft.Extensions.Logging` abstractions if convenient, but keep logging simple.

Structured logs:

```text
timestamp
level
plugin
sessionId?
runId?
event
message
```

Do not log:

```text
full prompts by default
API keys
complete tool outputs
AGENTS.md contents
```

Developer verbose mode can add protocol diagnostics explicitly.

Expose a small diagnostics view in the WebUI eventually:

```text
plugins
provider
model catalog age
shell detection
active background jobs
database path
```

---

# 48. Shutdown

Ctrl+C/process shutdown:

```text
stop accepting agent work
cancel active run
wait for tools
kill background process trees
stop Web server
flush SQLite
stop plugins reverse-order
unload plugins
exit
```

Background jobs must never be orphaned intentionally.

Register OS process-exit handling as a best-effort fallback.

---

# 49. Build/development workflow

A plugin should be independently buildable:

```text
dotnet build src/netPI.Tool.Read
```

output to staging:

```text
artifacts/plugins/netPI.Tool.Read/
```

Then WebUI:

```text
npm run build
```

output can be copied into:

```text
netPI.Web/wwwroot/
```

The normal developer loop:

```text
edit plugin
dotnet build
open Plugins UI
Reload
```

No netPI restart.

For WebUI:

```text
edit Svelte
npm build
Reload Web
browser reconnects
```

Could add automatic build scripts later; not automatic plugin reload.

---

# 50. Testing strategy

The architecture needs tests specifically aimed at the things likely to fail.

### Abstractions / Host

Test:

```text
load plugin
acquire service
request reload
verify reload waits for lease
release lease
verify new generation loads

reload repeatedly
verify old ALC is collectible

event subscription removed after reload

plugin throws during Load
old version remains usable or clear Failed state
```

### Agent

Fake provider scenarios:

```text
plain response
single tool call
parallel tool calls
thinking + text
steering while tools running
cancellation
provider exception
tool exception
```

### AiProxy

Replay captured SSE/OpenAI streams:

```text
text only
reasoning
interleaved reasoning/text
one tool call
multiple parallel tool calls
fragmented JSON arguments
usage chunk
disconnect mid-stream
```

### Context

Fixture directory trees for:

```text
global AGENTS
parent .netpi/AGENTS
child .netpi/AGENTS
override
SYSTEM
APPEND_SYSTEM
```

### Storage

Test:

```text
append ordering
restart/resume
pagination
compaction reconstruction
WAL concurrency
```

### Tools

Temp directory fixtures plus absolute paths outside workspace.

### Shell

Detection tests for:

```text
pwsh
Windows PowerShell fallback
Git Bash
PATH bash
WSL fallback
Linux bash
no bash available
```

### UI

Most important performance test:

```text
10k–50k transcript entries
active streamed assistant message
continuous thinking/text deltas
scroll during streaming
```

Verify old entries aren't rerendering continuously.

---

# 51. Implementation order

I would implement in this sequence because each stage produces something independently testable.

### Phase 1 — Runtime

Build:

```text
Abstractions
Host
PluginLoadContext
PluginManager
ServiceRegistry
leases
EventBus
config
```

Prove repeated hot reload with a dummy plugin before writing the agent.

### Phase 2 — Storage

Build SQLite plugin and sessions.

Verify:

```text
create
append
resume
paginate
```

### Phase 3 — Provider

Implement AiProxy:

```text
/v1/models
chat/completions streaming
reasoning
tool calls
usage
```

Test entirely without Agent using captured/provider fixtures.

### Phase 4 — Agent

Implement:

```text
basic loop
messages
stream events
tool execution
parallel calls
steering
cancellation
```

At this point it can run headlessly in tests.

### Phase 5 — Basic tools

Implement:

```text
read
write
edit
grep
bash
powershell
```

### Phase 6 — Pi context

Implement:

```text
Pi-like base prompt
.netpi/AGENTS traversal
SYSTEM
APPEND_SYSTEM
tool guidance
cwd/environment
```

### Phase 7 — Web

Implement:

```text
ASP.NET Minimal server
WebSocket
Svelte shell
session list
conversation
composer
thinking
tools
model/reasoning picker
```

### Phase 8 — Performance pass

Add:

```text
history pagination
virtualization
delta batching
stable completed components
large-tool-output collapsing
```

Do this before piling more UI features on.

### Phase 9 — AutoCompact

Implement Pi-like thresholds, compaction entries and active-context reconstruction.

### Phase 10 — Retry

Implement provider retry policy as its own plugin.

### Phase 11 — Background tasks

Implement:

```text
start
list
output
kill
process-tree cleanup
shell resolver integration
```

### Phase 12 — Plugin management UI

Show:

```text
name
version/generation
status
active leases
reload button
last error
```

Plus:

```text
Reload All
```

with proper drain behavior.

---

# 52. Definition of v1 complete

I would consider the first usable netPI complete when this works end-to-end:

```text
Start netPI
 ↓
plugins load
 ↓
AiProxy catalog fetched
 ↓
open WebUI
 ↓
create session + workspace
 ↓
select model + advertised reasoning level
 ↓
.netpi/AGENTS.md injected
 ↓
send prompt
 ↓
reasoning streams visibly
 ↓
assistant invokes multiple tools
 ↓
tools run in parallel
 ↓
send steering message while tools run
 ↓
steering appears at next turn boundary
 ↓
agent continues
 ↓
session survives netPI restart
 ↓
long history remains smooth
 ↓
auto-compaction occurs near context limit
 ↓
background PowerShell/Bash process works
 ↓
edit a plugin, rebuild it, press Reload
 ↓
plugin drains and new DLL takes over
 ↓
edit WebUI, rebuild, Reload Web
 ↓
agent-idle reload + browser reconnect
```

That gives you a **small Pi-like .NET harness rather than a framework pretending to be an agent platform**: a tiny stable runtime, a tiny loop, AiProxy as the clean model surface, and practically everything you will want to experiment with as a reloadable DLL.

[1]: https://pi.dev/docs/latest/usage "Using Pi · Documentation · Pi"
[2]: https://pi.dev/docs/latest/extensions?utm_source=chatgpt.com "Extensions · Documentation · Pi"
[3]: https://pi.dev/docs/latest/usage?utm_source=chatgpt.com "Using Pi · Documentation · Pi"
[4]: https://pi.dev/docs/latest/compaction?utm_source=chatgpt.com "Compaction & Branch Summarization · Documentation · Pi"
