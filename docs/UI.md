# NetPI web UI

The UI is a Svelte 5 (runes) app built with Vite. The host serves it from `artifacts/app/wwwroot`, and the
desktop shell shows it in WebView2. It also runs in a normal browser. It speaks the protocol in
[PROTOCOL.md](PROTOCOL.md) and renders the tool `details` described in [TOOLS.md](TOOLS.md).

Runtime dependencies: `svelte`, `marked`, `dompurify` and `highlight.js`. highlight.js loads its core and each
language only when a code block of that language scrolls into view. The UI uses no component framework.
Styling is plain CSS with design tokens.

## Dev workflow

```sh
npm install
npm run build            # build:web (→ artifacts/app/wwwroot) + build:plugins (plugins/*/ui → wwwroot/ui.js)
npm run dev              # Vite dev server on :5173, proxies /ws /api /plugins → http://127.0.0.1:7431
npm run mock             # mock host on :7431 (serves the built UI + the sample plugin), token "dev"
npm run e2e              # Playwright walkthrough (starts its own mock on :7432), screenshots → web/mock/screenshots
                         # (uses a local or global `playwright` install; see web/mock/pw.mjs)
node web/scripts/build-plugins.mjs --watch            # rebuild plugin UIs on change
node web/scripts/build-plugins.mjs web/mock/sample-plugin   # plus extra plugin dirs
node web/mock/fake-openai.mjs [port]  # scripted OpenAI-compatible model server for exercising the real host
```

- **Against the real host:** start NetPI, then `npm run dev` and open `http://localhost:5173/?token=<token>`.
  The host prints the token. The proxy target comes from `NETPI_URL` (default `http://127.0.0.1:7431`), and
  `VITE_NETPI_TOKEN` in `web/.env.local` saves typing the token. The proxy sets `Origin` to the target, so the
  host's origin check passes.
- **Against the mock:** `npm run build && npm run mock`, then open `http://127.0.0.1:7431/?token=dev`.
  `npm run dev` also works against the mock, because it listens on the same port. `MOCK_SPEED=3` runs the fake
  agent three times faster. `--no-auth` and `--port N` are also available.
- **Plugin UI hot reload:** `build:plugins` also copies each bundle to `artifacts/app/plugins/<P>/wwwroot/ui.js`.
  The host then bumps the tab `version` and emits `ui.changed`, and the tab remounts without a .NET build.
- **Real host without a model:** start `node web/mock/fake-openai.mjs 7468`, then point the host at it with
  `settings.set { path: "providers.aiproxy", value: { baseUrl: "http://127.0.0.1:7468", transport: "chat" } }`.
  Sending `[demo] …` to a session produces real subagents (2 lane slots, so one queues), a background and a
  foreground `bash`, usage and an `agent_wait`, which is enough to exercise every section of the Work tab.
- In a normal browser, `Ctrl+T`, `Ctrl+W`, `Ctrl+Tab` and `Ctrl+1…9` are taken by the browser. In WebView2 they
  reach the app. The command palette (`Ctrl+K`) and the `+` button do the same things.

## File map

```
package.json                   root scripts, single node_modules (plugin sources resolve svelte from here)
web/
  index.html  vite.config.js  svelte.config.js
  scripts/build-plugins.mjs    plugin UI bundler
  src/
    main.js                    styles, token, host services for plugins (__netpiHost), mount
    App.svelte                 shell layout + global shortcuts
    styles/tokens.css          design tokens (dark default, [data-theme=light])
    styles/kit.css             np-* utility classes (documented at the top of the file)
    styles/app.css             reset, markdown, highlight.js token colors
    lib/
      rpc.svelte.js            WebSocket RPC client, reconnect, subscriptions, HTTP fallback
      auth.js  bus.js  persist.js  frame.js  format.js  icons.js
      markdown.js              marked + DOMPurify (memoized), code blocks, lazy highlight, clipboard
      chatItems.js             ChatMessage[] → render items (steps groups, tool pairing)
      tools.js  diff.js        tool labels/icons/summaries, unified diff parser
      commands.js              slash commands (built-ins + ui.commands)
      pluginCtx.js             ctx object for plugin tabs
      folderPicker.js          WebView2 native picker or fs.dirs modal
      openFile.js              open a path with the OS (files.open): chat file links, "Open file", the file tree
      state/app.svelte.js      sessions, projects, models, agents, tabs, event routing, actions
      state/chat.svelte.js     ChatStore (per session), StreamState, LiveTool, LRU cache
      state/ui.svelte.js       layout, prefs, modals, toasts, composer bridge
      state/tabs.svelte.js     side-panel tab registry (core + plugin tabs)
      kit/                     @netpi/kit: Svelte components shared with plugin tabs
    components/
      TopBar.svelte  Welcome.svelte  Toasts.svelte  Popover.svelte
      panels/  SidePanel, PluginTabHost, SessionsTab, ProjectsTab
      chat/    ChatView, ChatHeader, MessageList, UserMessage, AssistantText, StepsGroup, ThinkingRow,
               ToolRow, NoticeRow, StatusRow, StreamingBlock, TodoList, ShownImage,
               tools/{Shell,Diff,Read,Search,Agent,Web,Todo,Generic}View
      composer/ Composer, ModelPicker, EffortPicker, ContextRing, QueueChips, TodoStrip
      modals/  Modals, Modal, Settings, FolderPicker, Confirm, Prompt, Help, CommandPalette, ProjectPicker,
               Projects (the projects dialog), Lightbox
  mock/
    server.mjs  store.mjs  agent.mjs  content.mjs    mock host (HTTP + WS + scripted agent)
    work.mjs  ideas.mjs  diag.mjs                     mock lanes/processes/usage, ideas backlog, plugin manager
    fake-openai.mjs                                  scripted model server for the real host
    sample-plugin/ui/{main.js,SampleTab.svelte}      example plugin UI (Svelte tab + vanilla tab)
    e2e.mjs  pw.mjs  screenshots/                    Playwright walkthrough
plugins/<P>/ui/                  built-in plugin tab sources → plugins/<P>/wwwroot/ui.js
  NetPI.Work/ui/                 WorkTab, LanePool, AgentNode, RecentAgent, ProcessRow, util.js
  NetPI.Ideas/ui/                IdeasTab, IdeaCard, NewIdea, SectionEditor, model.js
  NetPI.Diagnostics/ui/          DiagTab + Plugins/Tools/Rpc/Events/Logs/Context views
  NetPI.Tools.Files/ui/          FilesTab (left panel)
```

## Architecture

### RPC client (`lib/rpc.svelte.js`)

- There is one WebSocket to `/ws`, with `?token=` added when the app has an explicit token. Messages use the
  `{t:'rpc'|'sub'|'ping'}` envelope.
- Each call gets an id and waits for the reply with the same id, with a 30s timeout. A call made while the
  socket is down waits in an outbox. If the socket is not back within 3s, the call goes to
  `POST /api/rpc/{method}` instead (`http()`, which is also exported). Calls already on the wire when the socket
  drops reject with `code: 'disconnected'`.
- **Reconnect:** backoff is 0.4s, 1s, 2s, 4s, 7s, then 10s, with jitter. The client also retries right away on
  `online`, on `visibilitychange`, or when the user clicks the indicator. A ping goes out every 15s, and a
  socket that has been silent for 45s is treated as dead.
- On every (re)connect the client re-sends the `sub` set (the open tabs), reloads every list, and reloads the
  active chat's messages. Cached background chats are marked stale and reload when shown.
- `conn` (`$state`) holds `{ status: connecting|open|reconnecting, retryAt, clientId, version }`. The top bar
  reads it.

**Auth (`lib/auth.js`):** the host's HttpOnly cookie covers the normal case. A `?token=` in the page URL, or
`VITE_NETPI_TOKEN`, is stored in sessionStorage and sent as `X-NetPI-Token` (HTTP) and `?token=` (WS). The token
is removed from the address bar. On the dev origin it is also written as a `netpi_token` cookie, so proxied
module imports under `/plugins/*` authenticate too. The browser ignores that cookie when the host has already
set an HttpOnly one.

### Event routing

Every `ev` envelope goes to `bus.emit`. Handlers subscribe with `bus.on(pattern, fn)`, where the pattern is an
exact type, `prefix.*` or `*`, and are called as `fn(data, envelope)`. `state/app.svelte.js` routes each event
by type:

- **Session-scoped types** go to that session's cached `ChatStore.handle(type, data)`. These are
  `message.*`, `messages.compacted`, `stream.*`, `tool.*`, `agent.queue` and `agent.notice`. Events for
  sessions with no cached store are dropped; an assistant `message.added` for an open background tab marks it
  unread.
- **Broadcast types** update global state. `session.*` and `project.*` upsert the lists. `agent.status` updates
  a `SvelteMap` keyed by session, so only that session's dots re-render. When an agent goes from busy to idle
  in a background tab, the tab becomes *unread*. A background run that ends in an error (an `error` notice or
  `stopReason: 'error'`) gives the tab the *error* dot. `session.context` feeds the context ring.
  `models.changed`, `ui.changed` and `plugins.changed` refetch.

Plugin tabs attach to the same bus through `ctx.on`.

### Stores

| store | contents |
|---|---|
| `app` (`state/app.svelte.js`) | `sessions`/`projects`/`models` (`$state.raw` arrays, replaced on change) plus derived `…ById` maps, `agents`, `context`, `unread`, `openTabs`, `activeId`, `uiTabs`, `commands`. Actions: `openSession`, `newSession`, `closeTab`, `moveTab`, `updateSession`, `setSessionProject`, `sendMessage`, `abortAgent`, `dequeue`, … |
| `ChatStore` (`state/chat.svelte.js`) | per session: `messages` window, `hasMore`, `hasNewer`, `stream` (StreamState), `live` (callId → LiveTool), `queue`, `notice`, `pendingUser`, `expanded` (UI memory), `draft`, `images`, scroll memory. The 5 most recently used stores stay cached (LRU); a store that is still streaming is never evicted. |
| `ui` (`state/ui.svelte.js`) | `layout` (width, collapsed and active tab per side), `prefs` (theme, collapse steps, Enter behaviour, expand thinking), `modals`, `toasts`, and the `composer` bridge (`insertText`, `focus`, `setText`). |
| `tabs` (`state/tabs.svelte.js`) | the side-panel tab registry: core tabs (`registerCoreTab`) plus plugin tabs (`ui.tabs`), sorted by `order`. |

**Persistence** uses localStorage (read synchronously at startup, every access in try/catch) plus a debounced
`ui.state.set` as the durable copy. The keys are `netpi.layout`, `netpi.prefs` and `netpi.openTabs`. The UI
reads the host copies with `ui.state.get` only when localStorage is empty, for example in a new WebView
profile. Drafts (`netpi.draft.<sessionId>`) and the last project used (`netpi.lastProject`) are local only.

**New sessions and projects.** `newSession({ projectId? })` uses the explicit `projectId` (`null` means no
project). Without one, it uses the active session's project; with no session open, it uses the project last
worked in (`app.lastProjectId`: the last active session's project, or the one last chosen in a picker). The start
screen shows that project as "New session in [project ▾]" with its folder underneath; its chip opens `ProjectPicker`
in select mode (`{ select: true }`), which only changes `lastProjectId` and creates nothing, so the button, Ctrl+T and
the + tab all start in the project shown. The top-bar chip, the chat-header chip and `/project` open `ProjectPicker`
for a session, which calls `sessions.setProject`; the context plugin then appends a `project` notice, and the
AGENTS.md plugin an `instructions` notice if other instruction files apply.

**Projects dialog** (`ProjectsModal`, opened with `openProjects({ view, id?, sessionId?, select? })`). One dialog with
three views: `list` (filter, new session, edit, remove), `new` (folder with Browse…, name, create-folder) and `edit`
(name and folder, the project's sessions, the instruction files for its folder from `agentsmd.list { projectId }`,
remove). The pickers' footer opens it (Edit "current"…, New project…, Manage projects…), as do the Projects tab (+,
row click, the edit button) and the command palette. A project created from a picker is attached to that session, or
in select mode becomes the project new sessions start in. Dialogs can stack (a confirm or the folder picker over the
projects dialog): `Modal` keeps a stack, and Esc closes only the top one.
Components that close themselves (`onclose()`) or `await` a dialog read their props
into locals first, because after the parent clears the modal state or the row re-renders, the props are gone.

### Chat rendering pipeline

1. **Window.** `sessions.messages` loads the last 60 messages. **Load earlier** prepends the next page with
   `beforeSeq`. After a prepend, the list is scrolled so the first previously visible item stays where it was
   on screen. The window holds at most 200 messages. When new messages push it past 220, the oldest are
   dropped and **Load earlier** comes back. When loading earlier pushes it past 200, the newest are dropped
   instead: new messages then only increase a counter, and **Jump to latest** reloads the tail.
2. **Items** (`lib/chatItems.js`). The messages become render items: `user`, `text` (assistant markdown),
   `steps`, `notice`, `status` (error/aborted/length) and `images`. Consecutive thinking and tool_call parts,
   even across several assistant messages, form one **steps** group. Tool results from `tool` messages are
   paired with their calls by `callId`. Messages are immutable objects, and an item keeps its identity while
   its inputs are unchanged, so the keyed `{#each}` only touches the items that changed.
3. **Components.** `ToolRow` renders one row: icon, label, summary argument, badge, duration and status. It
   expands to a specialised view: `DiffView` (edit/write), `ShellView` (bash/pwsh: command, live output, exit
   code), `ReadView` (path, line range, highlighted content), `SearchView` (grep/find/ls), `AgentView`
   (`agent_*`, link to the subagent session), `WebView` (`web_fetch` title and text, `web_search` results as links,
   `screenshot` image and console errors), `TodoView` (the `todo_write` checklist) and `GenericView` (anything
   else). `read`/`write`/`edit` rows get an "Open file" button (`lib/openFile.js` → `files.open`).
   - **File links.** `renderMarkdown` marks links whose target is a local path (relative, `C:\…`, `file://`) as
     `a.file-link[data-path]` with `href="#"`; one delegated click handler opens them with the operating system
     through `files.open`, so a link never navigates the app. Web links keep `target="_blank"` (the desktop shell
     opens them in the default browser).
   - **Plan strip.** `TodoStrip` (in the composer dock) shows the session's `meta.todo` while any item is open:
     `done/total` and the current item, expanding to the checklist.
   - **Shown images.** A successful `show_image` result becomes its own `shown` item (`ShownImage`: the image, the
     caption, name · size), never folded into a steps group; a click opens the lightbox.
   - **Live thinking.** The streaming block's thinking line is a toggle: open, it shows the thinking so far (markdown
     at most every 150 ms, following its end). The choice is `chat.liveThinkingOpen`: later answers in that chat start
     open, and their finished thinking rows are marked expanded when the message arrives.
   - **Pinned to the bottom.** The list follows new content while it is pinned; only the user scrolling up unpins it.
     A view that shrinks (the plan strip or queue chips appearing) or content that grows never does: the resize
     observer re-pins.
   - A **steps** group with more than 3 steps collapses to `▸ N steps · time · tool counts`, but only after its
     run has finished. Steering input does not end a run. The user's choice to expand or collapse is kept per
     group in `chat.expanded`.
   - Thinking rows are collapsed by default.
4. **Streaming.** `stream.delta` appends to plain strings inside `StreamState`; they reach `$state` at most once
   per animation frame (`lib/frame.js`, with a 120ms timer fallback when the window is hidden). The streaming
   block re-renders its markdown at most every 100ms and never highlights it. The final `message.added`
   replaces the block. `tool.output` chunks go to `LiveTool` buffers, which keep the last 200KB, flushed the
   same way.
5. **Markdown** (`lib/markdown.js`). Parsing is marked (GFM) followed by DOMPurify, memoized by source text
   with an LRU of 600 entries. Code blocks get a header with the language and a copy button; one delegated
   click handler serves every copy button. The `use:highlight` action highlights a block when it comes within
   400px of the viewport (IntersectionObserver).
6. **Scrolling.** While the list is pinned to the bottom, a `ResizeObserver` on both the content and the
   scroller keeps it pinned. Scrolling up unpins it and shows **Latest**. Each session remembers its scroll
   position. Only the active session's chat is in the DOM, because `App` keys `ChatView` on the session id.

Timings from `npm run e2e` against the mock (headless Chromium):

| action | time |
|---|---|
| re-render the capped window (200 messages, 100 items) on a tab switch | ~60–70ms |
| **Load earlier** (RPC and render, 60 messages) | ~35–55ms |
| open the 320-message session (first page) | ~200ms, including the Playwright round trip |

### Composer

- The textarea grows with its content.
- **Enter** sends. While the agent runs, Enter **steers** and **Alt+Enter** **queues**. **Shift+Enter** inserts
  a newline. **Esc** stops the run (`agent.abort`). Settings can switch sending to **Ctrl+Enter**.
- `/` opens the commands popup: the built-ins `/new /rename /model /project /settings /help /abort`, plus
  commands from `ui.commands`. A command with `rpc` is called with `{ sessionId, args }`, and a string result
  is shown as a toast. A command with `clientAction` is handled by the client:
  `openTab:<pluginId/tabId>`, `insert:<text>` or `settings`.
- `@` opens a file popup backed by `files.search`; picking a file inserts `@rel/path`.
- Images can be attached with the button, pasted or dropped. They show as thumbnails and are sent as
  `{ mediaType, data }`.
- The model picker groups models by provider and shows each one's status dot, context window and concurrency;
  choosing one calls `sessions.update { model }`. The effort picker offers "default" plus
  `model.reasoning.efforts` and calls `sessions.update { reasoning }`; `''` means the model default.
- The context ring shows `used / window`, taken from `session.context` or `SessionInfo.contextTokens`.
- Queued inputs (`agent.queue`) appear as chips; the × on a chip calls `agent.dequeue`. `agent.notice` shows
  as a transient banner, which clears when the model streams again or the run ends.

## Plugin tabs

A plugin registers a tab (`context.Ui.AddTab`) whose `module` is an ES module in the plugin's `wwwroot`. The
host's `PluginTabHost`:

1. On first show, runs `import('/plugins/<pluginId>/<module>?v=<version>')`. Tabs render only once visited.
2. Calls `module[export]` when the tab declares `export`, otherwise `mount`, as `fn(el, ctx)`. `el` is a fresh
   `div.plugin-root`: a flex column that fills the panel height.
3. Keeps the tab mounted while it is hidden (another tab is active, or the panel is collapsed), and calls
   `onHide()` and `onShow()` from the returned handle.
4. Refetches `ui.tabs` on `ui.changed` and `plugins.changed`. When a tab's `version` (or its module or export)
   changes, it calls `unmount()` and imports the new URL (hot reload).
5. Shows an error state with **Retry** when the import or the mount throws. Each retry uses a fresh URL,
   because the browser caches a failed import per URL.
6. When the tab unmounts or reloads, releases every `ctx.on` and `ctx.app.onChange` subscription the plugin
   made, even if the plugin did not release it.

```ts
mount(el: HTMLElement, ctx): void | { unmount?(): void; onShow?(): void; onHide?(): void }
ctx = {
  pluginId, tabId,
  rpc(method, params?): Promise<any>,
  on(pattern, (data, evt) => void): () => void,          // 'agent.status', 'agent.*', '*'
  app: {
    activeSessionId, activeSession, activeProject,        // plain snapshots (getters)
    onChange(cb): () => void,                             // active session / its project changed
    openSession(id), newSession({ projectId? }), insertText(text), openTab('pluginId/tabId'),
    toast(text, level?: 'info'|'warn'|'error'),
  }
}
```

### Narrow panels

Side panels are often narrow: a user's layout might be 230–340px wide, and the minimum is 200px. Every panel
tab (Sessions, Projects, Files, Work, Ideas, Diagnostics) is designed for 220px first, looks right at a typical
280–320px, and uses wider containers for extra detail. The rules:

- **Never scroll sideways.** Plugin tab hosts clip `overflow-x`, and e2e checks that no panel tab overflows at
  230px.
- **Titles** go on their own line with ellipsis (or a 2-line clamp for idea titles) and a `title` tooltip.
- **Metadata** is one muted line (`.np-meta`, items joined with `·`) that ellipsizes and never wraps
  word-by-word. Details move into a tooltip or the expanded row.
- **Right-hand columns** (elapsed time, size, time ago) are `flex: none`. A name keeps its width up to about
  55% of the line (`flex: none; max-width`) and the secondary text shrinks first.
- **Row actions** are icon buttons in `.np-hover-actions`. They appear on hover or keyboard focus, sit over the
  right end of the first line with a fade (`--row-bg`), and stay visible on the selected or failed row.
- **Stats lines** use `.np-fit`, which drops whole items that do not fit (least important last) instead of
  clipping text. Examples: the Work summary and the Diagnostics runtime line.
- **Segmented controls** fit their container: icons + labels, then labels only, then icons + the selected
  label, then icons only, with the labels moved into tooltips (see `Segmented`).
- **Wide variants** use container queries. `.plugin-root`, `.tab-root` (Sessions, Projects) are
  `container-type: inline-size`, so `@container (max-width: 279px)` / `(min-width: …)` react to the panel
  width, not the window. Examples: the Files indent shrinks, the idea meta hides tags and priority labels, and
  "Send to chat" becomes "Send".
- **Filters that would wrap** (for example idea tags) become a menu, and selected values show as removable
  chips.

Per-tab narrow layouts:

- **Sessions:** search box and **+** share one row. Line 2 shows the project · message count · `› N`
  subagents (the word "subagents" only when the panel is ≥ 260px). Subagent rows are one line.
- **Work:** a pool reads `model provider` (so truncation eats the provider), with a status pill. Line 2 shows
  `busy/capacity`, pips and `+queued`, plus the models only when they differ from the pool. Agents use three
  lines: name + activity + elapsed, title or task, and meta. A process shows its command + elapsed/exit, then
  pid · bg · size · cwd … time ago.
- **Ideas:** a full-width title, a 2-line summary, then one meta line: status pill · priority · tags ·
  sections · time. In an expanded card the actions are **Send to chat**, **+ Section**, edit, and ⋯ (move
  up/down, copy id, delete).
- **Diagnostics:** the runtime facts drop out as the panel narrows. The view switcher collapses to icons. A
  plugin row has the name and state pill, one meta line (id · version · load ms · count), hover reload and ⋯,
  and a failed plugin's error clamped to 3 lines (click for all). Log messages clamp to 3 lines, and clicking
  opens the message and its exception.

### Built-in plugin tabs

Each one is a Svelte module in `plugins/<P>/ui/`, built by `build:plugins` like any other plugin tab. Bundle
sizes (minified; Svelte runtime and kit included): Work 88KB, Ideas 88KB, Diagnostics 99KB, Files 67KB.

**Work** (`netpi.work`, right). One `work.snapshot` feeds four collapsible sections, each with a count. The
open or closed state of each section is remembered (`storageKey`).

- A summary line shows active agents, busy slots (+ queued), running processes and today's tokens.
- **Lanes:** one row per pool with capacity pips (a bar when capacity is over 8), `busy/capacity`, a status pill
  (`queued` `full` `busy` `idle` `offline` `stopped`), the pool's models with their load-status dot from
  `models.list`, and the owners and waiters with elapsed time. Clicking an owner opens its session.
- **Agents:** the active agents as a tree (subagents nest under their parent). Each row shows a status dot,
  activity, elapsed time, the session title (or the task, for a subagent) and a one-line meta: model · turns ·
  tools · tokens · lane. Clicking a row opens the session, and the stop button calls `agent.abort { sessionId }`.
  Finished agents are listed below under **Recent** (result or error, TimeAgo), with **Show all** past 6.
- **Processes:** running processes first, then **Recent**. Expanding a row fetches `processes.output` (tail
  300 lines) and appends live `process.output` chunks. It falls back to polling every 2s when no chunk has
  arrived for 3s, and fetches once more on `process.exited`. The kill button is a two-step `ConfirmButton`
  that calls `processes.kill`.
- **Usage today:** `usage.summary` per provider (input ↑, output ↓, cache read), plus a budget bar when
  `budgetTokens` is set.
- Updates: `agent.status`, `lanes.changed` and `process.started/exited` are applied in place, and a debounced
  `work.snapshot` (250ms; 400ms after `usage.recorded`) reconciles them. A 30s timer refreshes the snapshot
  while the tab is visible; while it is hidden, events only mark it dirty and it refreshes on show.

**Ideas** (`netpi.ideas`, right). Shows the backlog of the active session's project (or the global file) with
`ideas.list { sessionId }`.

- The header shows the scope badge (project name or *global*), the file path and a **+** button.
- Filters: search over title, summary, tags and sections; a status menu (**Active** = open, planned,
  in-progress; **All**; or one status), each with counts; and a **#** tag menu (multi-select; an idea matches
  when it has any of the selected tags). Selected tags show as removable chips below the filters.
- Cards show a status pill (a menu that calls `ideas.update { patch: { status } }`), priority, tags, section
  count, an agent icon for agent-created ideas, and the update time. Expanding a card renders the summary and
  its sections as markdown.
- Editing: title, summary, priority and tags inline; add, edit (kind, title, markdown; Ctrl+Enter saves) and
  remove sections through `addSections`, `updateSections` and `removeSectionIds`; delete with the host confirm
  dialog.
- Reordering uses the grip (shown on hover; HTML5 drag with a drop indicator) or **Move up / Move down** in the
  ⋯ menu, and sends the full id order to `ideas.reorder`. Delete is in the ⋯ menu too.
- **Send to chat** calls `ideas.toPrompt` and `ctx.app.insertText`.
- The list refetches on `ideas.changed` for the shown file and on `ctx.app.onChange`.

**Diagnostics** (`netpi.diagnostics`, right). `diag.snapshot { events: 300 }` plus a runtime line (pid, working
set, uptime, threads, framework; the full details are in its tooltip). A segmented control (buttons carry
`data-value`) switches views, and the last view is remembered.

- **Plugins:** filter, state counts, failed plugins first with their error, a reload button
  (`diag.reload { args: id }`), and a ⋯ menu with Reload, Enable/Disable (`plugins.setEnabled`), Copy id, Copy
  folder and Reveal (desktop only). Expanding a row shows its folder, assembly, load time and count.
- **Tools:** grouped by category, with read-only, shadowed, disabled and priority badges. A chip shows or
  hides shadowed registrations. The owning plugin is shown under a tool only when several plugins register
  that name, and in the tooltip otherwise.
- **RPC:** methods grouped by prefix with the owning plugin; clicking copies the name.
- **Events:** the snapshot's recent events, then live events from `ctx.on('*')`, batched per animation frame and
  capped at 1000. Controls: type or session filter (`agent.*` prefixes work), hide `stream.delta`,
  `tool.output` and `process.output` (on by default), pause and clear. Clicking a row shows its payload (live
  events) or fetches it with `diag.event { seq }`.
- **Logs:** `logs.recent { max: 400 }`, polled every 3s while visible, newest first, with a level filter
  (All, Info+, Warn, Error, with counts). Exceptions expand.
- **Context:** for the active session, `context.preview` (estimated tokens, system prompt size, tool count,
  the system prompt with copy and expand, the tool list) and `agentsmd.list` (instruction files with scope
  badges, copy path and reveal).

**Files** (`netpi.tools.files`, left). A lazy tree of the active session's working folder.

- `files.list { sessionId, dir }` runs per expanded folder. Ignored entries are dimmed and can be hidden.
- The header shows the project name, the root path (shortened from the left), **collapse all** and **refresh**.
- The filter box calls `files.search` (debounced 140ms, 200 results) and shows a flat list.
- Clicking a file inserts `@rel/path ` into the composer (quoted when the path has spaces; folders end in `/`).
- The context menu (right-click or ⋯) has Insert @mention, Insert path, Copy relative path, Copy absolute path,
  Reveal in Explorer (desktop only) and Refresh folder.
- Keyboard: ↑ ↓ move, → ← expand and collapse, Enter inserts, and the context-menu key opens the menu.
- The tree reloads when the active session's project changes (`ctx.app.onChange`).

### Writing a tab in Svelte

Layout: `plugins/<P>/ui/main.js` (or `main.ts`) plus components. The bundle is built to
`plugins/<P>/wwwroot/ui.js` (see the `wwwroot/**` content item in `plugins/Directory.Build.props`).

```js
// plugins/NetPI.Work/ui/main.js
import { mount as svelteMount, unmount } from 'svelte';
import WorkTab from './WorkTab.svelte';

export function mount(el, ctx) {
  const view = svelteMount(WorkTab, { target: el, props: { ctx } });
  return { unmount: () => unmount(view), onShow: () => view.refresh?.() };
}
```

```svelte
<!-- WorkTab.svelte -->
<script>
  import { onMount } from 'svelte';
  import { Section, StatusDot, TimeAgo, Empty } from '@netpi/kit';
  let { ctx } = $props();
  let agents = $state([]);
  export async function refresh() { agents = await ctx.rpc('agents.list', {}); }
  onMount(() => {
    refresh();
    return ctx.on('agent.status', ({ agent }) => {
      const i = agents.findIndex((a) => a.id === agent.id);
      i >= 0 ? (agents[i] = agent) : agents.unshift(agent);
    });
  });
</script>

<Section title="Agents">
  {#each agents as a (a.id)}
    <div class="np-row" onclick={() => ctx.app.openSession(a.sessionId)}>
      <StatusDot status={a.status} /><span class="np-row-title">{a.name}</span><TimeAgo time={a.startedAt} />
    </div>
  {:else}<Empty icon="bot">No agents</Empty>{/each}
</Section>
```

`npm run build:plugins` builds every `plugins/*/ui/main.{js,ts}` into **one** minified ES module.
Component CSS is injected at runtime (`css: 'injected'`), and `svelte` and `@netpi/kit` are bundled in: the
sample plugin comes to about 61KB. Styling should use the host tokens and `np-*` classes, which keep the tab
working in both themes. A plugin should not import `.css` files; component `<style>` is the way to style it.

### Writing a tab in vanilla JS

```js
export function mountLog(el, ctx) {      // UiTabInfo { module: 'ui.js', export: 'mountLog' }
  el.innerHTML = `<div class="np-toolbar"><span class="np-section-title">Events</span></div>
                  <div class="np-scroll" style="flex:1"></div>`;
  const list = el.lastElementChild;
  const off = ctx.on('*', (d, evt) => list.insertAdjacentHTML('beforeend',
    `<div class="np-row np-mono np-small">${evt.type}</div>`));
  return { unmount: () => { off(); el.innerHTML = ''; } };
}
```

The sample plugin (`web/mock/sample-plugin/ui/main.js`) has one tab of each kind.

### `@netpi/kit`

Import from `@netpi/kit`. The build aliases it to `web/src/lib/kit/index.js`, and the host uses the same files.

| component | props |
|---|---|
| `Section` | `title`, `count`, `actions` snippet, children, `collapsible`, `bind:open`, `storageKey` (remembers open/closed), `flush` |
| `Badge` | `tone` (`ok` `warn` `err` `info` `accent`) |
| `StatusDot` | `status` (see `.np-dot`) |
| `TimeAgo` | `time` (ISO string or ms); updates every 30s |
| `Elapsed` | `since`, `until?`; ticks every second while running |
| `Empty` | `icon`, children |
| `Button` | `variant` (`default` `primary` `ghost` `danger`), `size` (`md` `sm`), `icon`, `onclick`, `disabled`, `title` |
| `IconButton` | `icon`, `title`, `size`, `pressed`, `onclick` |
| `ConfirmButton` | `icon`, `label`, `confirmLabel`, `title`, `onconfirm`: the first click arms it for 2.5s, the second confirms |
| `SearchInput` | `bind:value`, `placeholder`; Esc clears |
| `Segmented` | `options` (`{ value, label, icon?, count?, tone?, title? }[]`), `bind:value`, `onchange`, `fit` (default `true`: steps down to labels only → icons + selected label → icons only to stay inside its container; the icon steps need an `icon` on every option). Buttons carry `data-value`. |
| `Menu` | `items` (`{ label, icon?, hint?, checked?, danger?, disabled?, keepOpen?, onclick }`, `{ divider }`, `{ header }`; `keepOpen` for multi-select toggles), `trigger` snippet `({ toggle, open })`, `placement` (`bottom-end` / `bottom-start`); exported `openAt(x, y, items?)`, `openFor(el, items?)`, `close()` for context menus |
| `Pips` | `busy`, `capacity`, `queued`, `max` (a bar instead of pips above `max`) |
| `Collapsible` | `title` or `header` snippet, `bind:open` |
| `Markdown` | `text`, `highlight` (default `true`) |
| `Icon` | `name` (host icon set) or an inline `<svg>` string, `size`, `stroke` |
| `Spinner` | `size` |

The kit also exports the helpers `timeAgo`, `duration`, `tokens`, `bytes`, `relPath`, `basename`, `truncate`,
`stamp`, `renderMarkdown` and `host`, plus:

- `confirm({ title, message, confirmLabel, danger })`: the host's confirm dialog (a `Promise<boolean>`), or
  `window.confirm` outside the host.
- `copyText(text)`: clipboard with a textarea fallback; resolves to `true` on success.
- `desktop`: `available` (running in WebView2), `revealPath(path)` and `openExternal(url)`.
- `clockNow()` and `secondNow()`: shared reactive clocks (30s and 1s) for relative times.

`Markdown`, `Icon`, `confirm` and `copyText` call **host services** on `globalThis.__netpiHost`, which the host
sets in `main.js`: `renderMarkdown`, `highlight`, `icon`, `confirm` and `copyText`. Plugin bundles therefore do
not include marked, DOMPurify, highlight.js or the icon set, and they share the host's caches.

Icon names: `sessions folder folder-open plus x chevron-* arrow-* stop image paperclip at slash settings search
terminal file file-text file-plus files pencil rename brain list-tree bot copy check alert alert-circle info
refresh trash archive more external cpu branch clock sun moon puzzle work activity idea bug list zap layers
message-circle steer queue panel-left panel-right command home corner-up drive sliders circle-check circle-x ban
globe wrench kill history expand process sparkle keyboard link grip play pause`. A `UiTabInfo.icon` can use any of these
names or an inline `<svg …>` string.

### CSS

**Tokens** (on `:root`, overridden by `[data-theme=light]`):

- colors: `--bg --bg-1 --bg-2 --bg-3 --border --border-strong --fg --fg-muted --fg-dim --accent --accent-fg
  --ok --warn --err --info`, plus the `-soft` variants `--accent-soft --ok-soft --warn-soft --err-soft --info-soft`
- fonts: `--font-ui --font-mono --fs --fs-sm --fs-xs --fs-chat --fs-mono`
- shape and motion: `--radius --radius-sm --radius-lg --shadow --shadow-sm --t --t-fast --ease`
- other: `--code-bg`, and `--diff-*` / `--hl-*` for diffs and syntax colors

**Utility classes** (documented in detail at the top of `web/src/styles/kit.css`):

| group | classes |
|---|---|
| layout | `np-scroll np-stack np-stack-sm np-hstack np-hstack-sm np-spacer np-toolbar np-divider` |
| sections | `np-section np-section-title np-section-actions np-section-count np-section-toggle np-card` |
| lists | `np-list np-row np-row-title np-row-sub np-kv np-table` |
| controls | `np-btn np-btn-primary np-btn-ghost np-btn-danger np-btn-sm np-icon-btn np-input np-check np-seg np-search np-chip[aria-pressed]` |
| narrow rows | `np-line` (+ `np-grow`), `np-meta` (+ `np-meta-plain`), `np-fit`, `np-hover-row` + `np-hover-actions` (`--row-bg`) |
| text | `np-mono np-muted np-dim np-small np-strong np-ellipsis np-kbd` |
| status | `np-badge[data-tone] np-dot[data-status] np-empty np-spinner np-progress[style=--value]` |

## Desktop integration (WebView2)

- **Folder picker.** The page posts `window.chrome.webview.postMessage({ type: 'pickFolder', id, initial })`.
  The shell replies with a web message `{ type: 'pickFolderResult', id, path | null }`, as an object or as a
  JSON string. Without `chrome.webview`, a modal browses the host's folders with `fs.dirs`.
- **External links.** Markdown links (`http(s)`) open with `target=_blank rel=noopener`. The shell opens them in
  the default browser. Plugins can also post `{ type: 'openExternal', url }` (kit `desktop.openExternal`).
- **Reveal.** `{ type: 'revealPath', path }` (kit `desktop.revealPath`) shows a file or folder in Explorer. The
  Files and Diagnostics tabs offer it only when `window.chrome.webview` exists.

## What the UI expects from the host (beyond PROTOCOL.md)

- `sessions.list` accepts `includeArchived: true`. It is in `SessionQuery` but not in the protocol table; the
  archived list and unarchiving depend on it.
- `sessions.update { model: '' }` and `{ reasoning: '' }` must reset to the default. The UI sends an empty
  string for "default".
- `agents.list { includeFinished: true }` is called at startup to seed the status dots, including failed and
  completed subagents.
- Scoped events carry a non-null `sid`. The UI also falls back to `d.sessionId`.
- `agent.status` is broadcast whenever `status` **or** `activity` changes. The header shows
  `activity · elapsed`, computed from `startedAt`.
- `message.added` for a **steering** input has `meta.kind: 'steer'` (and `'queued'` for a queued follow-up),
  so the UI can tag the input and keep the run's steps grouped. `meta.agentName` and `meta.sessionId` on
  `agent-result` and `agent-message` notices enable the "open" link.
- Assistant messages that stop with `stopReason: 'error'` may carry `meta.error`, which is shown in the error
  row.
- `agent_*` tool results carry `details.sessionId` (and `name`, `status`) for the subagent link, or
  `details.agents[]` for multi-agent tools such as `agent_wait`.
- `/plugins/*` is served without auth, as the host does today, or with the `netpi_token` cookie. A module
  import cannot send headers, so in dev the UI supplies the cookie through the proxy.
- Slash commands with `rpc` receive `{ sessionId, args }` and may return a string, which is shown as a toast.
  The `clientAction` values the UI understands are `openTab:<pluginId>/<tabId>`, `insert:<text>` and
  `settings`.
- `sessions.messages` has only `beforeSeq`. An `afterSeq` parameter would let the pruned window page forward;
  today it reloads the tail instead.
- A `UiTabInfo.panel` sent as a number (enum without a string converter) is accepted: `0` is left, `1` is right.
- The Work tab reads `usage.summary` providers' `budgetTokens` and `budgetUsed` (input + output + cache write),
  `LanePool.status`, `LaneOwner.label`/`since`, and `ProcessInfo.outputBytes`/`background`/`agentId`.
- The Ideas tab expects `ideas.changed { file }` after every write, including writes made by agents.
