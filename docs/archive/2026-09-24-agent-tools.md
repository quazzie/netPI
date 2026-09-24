# Agent tools, projects UI and tool notices (done 2026-09-24)

What the user asked for after the bring-up (`2026-09-24-windows-bringup.md`), partly from their own agent's wish list.
Kept for reference; the current state is in `docs/HANDOFF.md` and `docs/STATUS.md`.

## Projects UI (`b701882`)

- The start screen reads "New session in [project ▾]" with the folder underneath; the chip only picks the project new
  sessions start in (also used by Ctrl+T and the + tab). The shortcut buttons that each started a session are gone.
- A projects dialog (list / new / edit: name, folder, the project's sessions, its instruction files, remove), opened
  from every project picker (Edit "current"…, New project…, Manage projects…), the Projects tab and the palette.
- Dialogs stack: Esc closes only the top one. `agentsmd.list` also takes `{ projectId }`.

## Web tools, todo, notices, file links

| change | where |
|---|---|
| `web_fetch`: a page as Markdown (main content, absolute links, code languages, tables), JSON/text as-is, images for vision models, paged with `offset` from a 5-minute cache | `plugins/NetPI.Tools.Web` |
| `web_search`: SearXNG or Brave, "auto" falls back from SearXNG to Brave; NetPI's own settings only (reading pi's config at runtime was removed on review) | `plugins/NetPI.Tools.Web` |
| `screenshot`: a URL in a headless Edge/Chrome over the DevTools protocol (no Playwright), with wait_for, full page and console errors; without a URL the NetPI window through the desktop shell's `desktop.capture` | `plugins/NetPI.Tools.Web`, `src/NetPI.Desktop` |
| `todo_write`: a checklist in the session meta, echoed to the model, shown above the composer while items are open; brought back by a notice after compaction | `plugins/NetPI.Todo`, `web/src/components/composer/TodoStrip.svelte` |
| "tools" notices: tools that appear or disappear mid-session are named, with the new tools' guidelines | `plugins/NetPI.Context/ToolNotices.cs` |
| "# Instruction files" prompt section: AGENTS.md is the lean entry point with pointers to deeper docs; durable learnings go there as one line (`agentsMd.guidance`) | `plugins/NetPI.AgentsMd` |
| File links in the chat open with the operating system (`files.open`: default app, scripts for editing, executables only revealed); "Open file" on read/write/edit rows; "Open" in the file tree | `plugins/NetPI.Tools.Files/FileOpener.cs`, `web/src/lib/openFile.js` |
| The Providers test runner honors `[filter]` like the other suites (`7ab67b5`) | `tests/NetPI.Providers.Tests` |

Follow-ups the same day: `show_image` (`plugins/NetPI.Tools.Media`: the agent shows the user an image, display only);
thinking can be opened while it streams; the chat stays pinned to the bottom when the plan strip appears (only the
user scrolling up unpins it); the todo list survives compaction; web search reads only NetPI's settings.

Decisions: light web limits only (agents have curl anyway); no separate learnings journal (AGENTS.md, kept lean, is
the place); no symbol search (would need Roslyn).

## Live check (nInfer `qwen3.8-27b`, throwaway server)

| step | prompt | cached | previous prompt + output |
|---|---|---|---|
| plan, search, fetch (4 calls) | 6,435 → 11,133 | 0 → 11,069 | each = previous |
| screenshot of svelte.dev (2 calls) | 11,357 / 12,516 | 11,323 / 11,420 | = previous |
| web plugin disabled ("tools" notice) | 12,108 | 0 | the tool definitions changed |
| web plugin enabled again ("tools" notice) | 12,933 | 12,633 | nInfer reused the state from before the change |

The model searched through SearXNG, read the `$derived` page, answered correctly, read the headline off the
screenshot and answered from both tool notices.
