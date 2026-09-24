# netPI

A fast, minimal LLM agent harness in the spirit of [pi](https://pi.dev): a small .NET 10 core where **everything is a
runtime-reloadable plugin** — model providers, tools, the agent loop, lanes, compaction, retries, the panel tabs —
and a Svelte UI shown in a WebView2 window. The desktop app starts its own server; the same server also runs
headless for any browser.

![netPI running three subagents on a 2-lane local model](docs/images/netpi-subagents.png)

## Highlights

- **Sessions first, projects switchable.** A session is the unit of work; a project is just a name + folder. Switch
  the project mid-session and the agent gets a short notice with its new working directory. Open sessions are tabs.
- **Lanes.** Every model belongs to a pool with N lanes (your local `qwen3.8-27b` via AiProxy reports
  `concurrency: 2` → 2 lanes; Claude gets its own pool). Agents hold a lane per run and queue when the pool is full.
  An orchestrator that waits for its workers **yields** its lane to them and resumes with only their final reports.
- **Agents manage agents.** `agent_spawn`, `agent_wait`, `agent_send`, `agent_list`, `agent_result`, `agent_cancel`,
  `lanes_list`; subagents get their own (viewable, steerable) sessions and report back automatically.
- **Steering and queueing.** While an agent runs, *Enter* steers it (delivered after the current tool call) and
  *Alt+Enter* queues a follow-up; *Esc* stops.
- **Tools as plugins:** `read`, `write`, `edit` (multi-edit, replace-all), `grep`, `find`, `ls` — all CRLF/LF
  agnostic, preserving each file's line endings and BOM — plus `bash` (Git Bash on Windows), `pwsh` and background
  processes. Replace any tool by registering one with the same name.
- **Providers:** AiProxy / any OpenAI-compatible server (Responses API by default, Chat Completions per model) and
  Anthropic Claude (thinking, prompt caching).
- **Robustness plugins:** auto-compaction, auto-nudge (continues agents that stall or get cut off mid-thinking),
  retry on lost connections/stalled streams, repair of tool calls a model wrote as text (`<tool_call>…`).
- **Context:** a system-prompt plugin that tools contribute to, and an AGENTS.md plugin (global
  `~/.netpi/AGENTS.md` + every `AGENTS.md`/`CLAUDE.md` from the file system root down to the project).
- **Ideas backlog:** agents and you park research, plans and requirements in `ideas.json` in the project folder
  (tools `idea_*` + the Ideas tab); "send to chat" when it's time to implement.
- **UI:** left and right panels with vertical, pluggable tabs (Sessions, Projects, Files | Work, Ideas,
  Diagnostics), collapsible thinking/tool blocks, diffs, live shell output, pruned chat history with "load earlier",
  slash commands and `@` file mentions. Window size and position are remembered.
- **SQLite** storage (the OS's own SQLite: no native packages to ship).

## Quick start (Windows)

Requirements: **.NET 10 SDK**, the **WebView2 runtime** (built into Windows 11), **Git for Windows** (for the `bash`
tool). Optional: PowerShell 7 (`pwsh` tool), Node.js 22 (only to change the UI — built bundles are committed).

```powershell
cd C:\AI\NetPI
.\build.ps1 -Run          # builds into artifacts\app and starts artifacts\app\NetPI.exe
```

On first start `%USERPROFILE%\.netpi\settings.json` is created: AiProxy at `http://127.0.0.1:8090` (Responses
transport) and `aiproxy/qwen3.8-27b` as the default model. For Claude set `providers.anthropic.apiKey` (or the
`ANTHROPIC_API_KEY` environment variable). All keys: [docs/SETTINGS.md](docs/SETTINGS.md).

Headless: `artifacts\app\netpi-server.exe --open` (prints and opens a tokenized URL). Linux/macOS: `./build.sh`, then
`artifacts/app/netpi-server --open`.

## Using it

| | |
|---|---|
| *Enter* / *Shift+Enter* | send / newline |
| *Enter* while running · *Alt+Enter* · *Esc* | steer · queue a follow-up · stop |
| `/` | commands: `/new /rename /model /project /compact /idea /reload /settings /help /abort` |
| `@` | mention a file of the session's workspace |
| *Ctrl+T*, *Ctrl+W*, *Ctrl+Tab*, *Ctrl+1…9* | new, close, cycle, pick session tabs |
| *Ctrl+B* / *Ctrl+Alt+B* | toggle left / right panel · *Ctrl+K* command palette |

The model and reasoning-effort pickers and the context meter sit in the composer. The **Work** tab shows lanes
(busy/capacity, queues), agents (active and recent) and processes (with live output and kill); **Diagnostics** shows
plugins (reload/enable/disable), tools, RPC methods, the live event bus, logs and the exact system prompt.

## Architecture

```
NetPI.exe (WinForms + WebView2) ─┐          ┌─ plugins/ (collectible load contexts, hot reload)
netpi-server (headless) ─────────┴─ NetPI.Host ─┤   NetPI.Agent        agent loop, steering/queue, subagents, yield
   Kestrel 127.0.0.1 + WebSocket (token auth)   │   NetPI.Lanes        lane pools, queueing, usage/budgets
   plugin manager · event bus · service/RPC/    │   NetPI.Context      system prompt builder + sections
   tool/UI registries · SQLite · settings ·     │   NetPI.AgentsMd     AGENTS.md / CLAUDE.md
   session store · model catalog                │   NetPI.Providers.*  AiProxy (OpenAI-compatible), Anthropic
                                                │   NetPI.Tools.*      files, shell, agents
NetPI.Abstractions: the contracts plugins use   │   NetPI.Compaction · NetPI.Nudge · NetPI.Retry · NetPI.ToolRepair
web/ (Svelte 5): the UI + plugin tab kit        │   NetPI.Ideas · NetPI.Work · NetPI.Diagnostics
                                                └─ ~/.netpi/plugins/ (your own)
```

Data: `~/.netpi/` — `settings.json`, `netpi.db` (sessions, messages, projects, agents, usage), `AGENTS.md`,
`logs/`, `webview/`, `window.json`, `workspace/` (cwd of sessions without a project).

## Develop

```powershell
dotnet build plugins\NetPI.Nudge     # while NetPI runs: the plugin hot-reloads
.\build.ps1                          # while NetPI runs: everything but the host is built, plugins hot-reload
npm run build:plugins                # plugin tab UIs → tabs reload
npm run dev                          # UI dev server against a running NetPI (see docs/UI.md)
.\build.ps1 -Test                    # unit suites (NuGet-free console runners)
```

| doc | |
|---|---|
| [docs/PLUGINS.md](docs/PLUGINS.md) | writing plugins (tools, providers, hooks, prompt sections, tabs) |
| [docs/PROTOCOL.md](docs/PROTOCOL.md) | UI ⇄ server protocol, RPC methods, events |
| [docs/TOOLS.md](docs/TOOLS.md) | built-in tools and their UI details |
| [docs/SETTINGS.md](docs/SETTINGS.md) | every setting |
| [docs/UI.md](docs/UI.md) | the Svelte app and the plugin tab kit |
| [docs/TESTING.md](docs/TESTING.md) | unit suites, the mock model server, the end-to-end suite |
| [docs/AIPROXY-AGENT-GUIDE.md](docs/AIPROXY-AGENT-GUIDE.md) | the local AiProxy server |
