# What the harness misses (decided 2026-09-25)

The user asked what NetPI lacks compared with other agent harnesses. This is the list we agreed on, in order, with
the design points settled so far, and what was dropped and why. Each item is marked done here with its commit; when
all are, this plan moves to `docs/archive/`.

## To build, in this order

1. **Retry-After** (done). A 429 or 529 says how long to wait (`Retry-After`), but the retry plugin kept its own
   backoff and got refused again. `ModelException.RetryAfter` (additive) is set by every provider from the response
   header (seconds or a date), the wrappers that rebuild the exception keep it, and the retry middleware waits at
   least that long, within `retry.maxTotalSeconds` (a longer wait gives up at once).
2. **Windows toasts** (done) from the desktop app when the NetPI window is not in front: a run finished or failed, a
   goal was completed or is blocked, the budget asks (`ask_user` adds its own). The web UI decides and posts `notify`
   to the shell; a tray balloon (shown as a toast) and a flashing taskbar button, no new dependency. See `docs/UI.md`
   (Desktop integration).
3. **Files tab** (done; asked for on 2026-09-25): a click on a file opens it; the `@` button of a row inserts it into
   the chat as a mention; a git line at the bottom (`files.git`) shows the branch and the uncommitted changes since
   the last commit (e.g. `4 files +13 −400`), and clicking it lists the changed files. See `docs/UI.md` (Files).
4. **Cache reuse and time to first token per turn** (done), shown in the chat: the runner keeps the time to the
   first token in the message meta (`ttftMs`); a dim line under each model turn's steps shows it with the cache reuse,
   the output speed and the tokens, and an answer's footer has the same. A preference switches the lines off. See
   `docs/UI.md` (Model turns).
5. **`ask_user`**: the agent asks the user questions with options. The questions sit **inline in the chat** as its
   newest item, below the agent's message that gives their context; nothing overlays the chat. The card is expanded
   while it waits and collapses to one line (question and answer) once answered; to read the context the user scrolls
   up. Answer by clicking an option or typing in the composer. The run waits with its instance freed (like
   `agent_wait`).
6. **Guardrails**: fast checks before a tool runs (blocked command patterns, protected paths such as `~/.netpi`,
   optional "ask first" rules). Pattern and path checks only: no model call and no prompt change, so tokens per second
   stay the same. Blocking suits unattended runs better than asking.
7. **Ideas plugin**:
   - the file moves from the project root to `.netpi/ideas.json` (like `.netpi/skills`), and an existing
     `ideas.json` is moved there;
   - one `ideas` tool with an action instead of five (deleting stays in the tab);
   - the agent marks an idea done when it finishes that work, and the list shows open ideas by default.
   It stays JSON in the project: it travels with the repository, stays readable without NetPI or with the plugin off,
   and a backlog is too small to need a database. NetPI's own backlog stays in the docs (`docs/STATUS.md`), since the
   plugin can be disabled.
8. **Forking a chat**: a new chat with the conversation up to a message; the original stays. Conversation only (files
   exist once).

Later: a language server behind a tool (errors in the changed files in about a second instead of a build; where a
symbol is defined and used), reading PDF and Office files, scheduled runs.

## Dropped

- **MCP**: the harness is built for easy plugins, so focused plugins are the extension story, not MCP servers.
- **Checkpoints and rollback of the agent's changes**: never complete (large files; the agent can change files
  through bash and commands), and a rollback that covers only some changes is confusing.
- **Git worktrees for parallel subagents**: the subagent flow is being rethought first.
- **A verifier for goal completion**, **`!command` in the composer**, **phone access**.
- **Formatter hooks**: a formatter rewriting a file after the agent's edit breaks the agent's picture of it (its next
  `edit` no longer matches), so it re-reads; format once at the end instead.
