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
npm run e2e              # the whole Playwright walkthrough (starts its own mock on :7432; ~2.5 min): the gate before a merge,
                         # not a loop. Screenshots → web/mock/screenshots. (playwright-core devDependency, or a local/global
                         # `playwright`; see web/mock/pw.mjs)
npm run e2e -- --list    # its sections (one per log('…') header)
npm run e2e -- --only "plugin tab: Work"   # just that section, 8-20 s: a part of a name is enough ("Work" is exact-or-substring),
                         # several with |; everything else is skipped with its setup. --with <section> adds a silent prerequisite,
                         # --port N runs next to another run. A name that matches nothing exits 2 before anything starts.
npm run e2e:sections     # every section alone, 4 at a time (web/mock/sections.mjs): a section that cannot stand alone must
                         # list what it builds on in NEEDS (web/mock/e2e.mjs) or set that state up itself
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
  Sending `[demo] …` to a session produces real subagents (a model with 2 slots, so one queues), a background and a
  foreground `bash`, usage and an `agent` `wait`, which is enough to exercise every section of the Work tab.
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
      skills.js                the session's skills as /skill:name entries of the / popup
      pluginCtx.js             ctx object for plugin tabs
      folderPicker.js          WebView2 native picker or fs.dirs modal
      openFile.js              open a path with the OS (files.open): chat file links, "Open file", the file tree
      goal.js                  goals: /goal and the goal strip (goal.* RPCs)
      state/app.svelte.js      sessions, projects, models, agents, tabs, event routing, actions
      state/chat.svelte.js     ChatStore (per session), StreamState, LiveTool, LRU cache
      state/ui.svelte.js       layout, prefs, modals, toasts, composer bridge
      state/tabs.svelte.js     side-panel tab registry (core + plugin tabs)
      kit/                     @netpi/kit: Svelte components shared with plugin tabs
    components/
      TopBar.svelte  BudgetPill.svelte  Welcome.svelte  Toasts.svelte  Popover.svelte
      ModelMenu.svelte (the searchable model list)  ModelSelect.svelte (a model field that opens it)
      panels/  SidePanel, PluginTabHost, SessionsTab, ProjectsTab
      chat/    ChatView, ChatHeader, MessageList, UserMessage, AssistantText, StepsGroup, ThinkingRow,
               ToolRow, NoticeRow, PromptRow, SentBlock, StatusRow, TodoList, ShownImage,
               tools/{Shell,Diff,Read,Search,Agent,Web,Todo,Generic}View
      composer/ Composer, ProfilePicker, ProjectPicker, AgentPicker, EffortPicker, ToolsPicker, ChatCost, ContextRing,
                QueueChips, GoalStrip, TodoStrip, IdeaChip, RunStatus
      modals/  Modals, Modal, Settings, FolderPicker, Confirm, Prompt, Help, CommandPalette, ProjectPicker,
               Projects (the projects dialog), Lightbox; the settings pages: SettingField (one control per
               SettingInfo), SettingsRow, AgentsEditor + AgentDialog, BudgetView, ProfilesEditor + ProfileDialog,
               PluginSwitches
  mock/
    server.mjs  store.mjs  agent.mjs  content.mjs    mock host (HTTP + WS + scripted agent)
    work.mjs  ideas.mjs  diag.mjs                     mock running calls/processes/usage, ideas backlog, plugin manager
    fake-openai.mjs                                  scripted model server for the real host
    sample-plugin/ui/{main.js,SampleTab.svelte}      example plugin UI (Svelte tab + vanilla tab)
    e2e.mjs  pw.mjs  screenshots/                    Playwright walkthrough
plugins/<P>/ui/                  built-in plugin tab sources → plugins/<P>/wwwroot/ui.js
  NetPI.Work/ui/                 WorkTab, AgentPool, AgentNode, RecentAgent, ProcessRow, util.js
  NetPI.Ideas/ui/                IdeasTab, IdeaCard, NewIdea, SectionEditor, model.js
  NetPI.Diagnostics/ui/          DiagTab + Plugins/Tools/Rpc/Calls/Events/Logs/Context views
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

**Tab bar.** Tabs scroll sideways with the wheel, drag to reorder, middle-click or the × to close. The `+` starts a
new session in the project new sessions start in; next to it a chevron (`TopBar.svelte`) opens the **favorites**
menu — one entry per starred project, a new session in it (idea-xzsy6z; they used to be buttons beside the `+`, and
took the space of the tab bar). The chevron only appears when there is at least one favorite; starring happens in the
Projects tab (`prefs.favorites`).

**New sessions and projects.** `newSession({ projectId? })` uses the explicit `projectId` (`null` means no
project). Without one, it uses the active session's project; with no session open, it uses the project last
worked in (`app.lastProjectId`: the last active session's project, or the one last chosen in a picker). The start
screen shows that project as "New session in [project ▾]" with its folder underneath; its chip opens `ProjectPicker`
in select mode (`{ select: true }`), which only changes `lastProjectId` and creates nothing, so the button, Ctrl+T and
the + tab all start in the project shown. The project button in the composer bar (after the profile picker) and
`/project` open `ProjectPicker` for a session, which calls `sessions.setProject`; the context plugin then appends
a `project` notice, and the AGENTS.md plugin an `instructions` notice if other instruction files apply.

**Projects dialog** (`ProjectsModal`, opened with `openProjects({ view, id?, sessionId?, select? })`). One dialog with
three views: `list` (filter, new session, edit, remove), `new` (folder with Browse…, name, create-folder) and `edit`
(name and folder, the project's sessions, the instruction files for its folder from `agentsmd.list { projectId }`,
its skills and their problems from `skills.list { projectId }`, remove). The pickers' footer opens it (Edit "current"…, New project…, Manage projects…), as do the Projects tab (+,
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
   `steps`, `notice`, `prompt` (a system prompt the chat was sent), `status` (error/aborted/length) and `images`. Consecutive thinking and tool_call parts,
   even across several assistant messages, form one **steps** group. Tool results from `tool` messages are
   paired with their calls by `callId`. Messages are immutable objects, and an item keeps its identity while
   its inputs are unchanged, so the keyed `{#each}` only touches the items that changed. The message being
   streamed joins them through `withStream`, laid out the way it will be when it arrives: its thinking and tool
   calls are steps in the open group (tool steps already carry their final key, `c<callId>`), its text is a text
   item. So nothing changes height when `message.added` replaces the stream.
3. **Components.** `ToolRow` renders one row: icon, label, summary argument, badge, duration and status. It
   expands to a specialised view: `DiffView` (edit/write), `ShellView` (bash/pwsh: command, live output, exit
   code), `ReadView` (path, line range, highlighted content), `SearchView` (grep/find/ls), `AgentView`
   (`agent_*`, link to the subagent session), `WebView` (`web_fetch` title and text, `web_search` results as links,
   `screenshot` image and console errors), `TodoView` (the `todo_write` checklist) and `GenericView` (anything
   else). `read`/`write`/`edit` rows get an "Open file" button (`lib/openFile.js` → `files.open`). The SSH tools reuse
   these views: `ssh` `run` the shell view (prompt `host$`), `read` the read view, `write`/`edit` the diff view. A call with an `action` is shown as `<tool>_<action>` when the UI has a view for that name (`viewName` in `web/src/lib/tools.js`, one rule and no list of tools: `ssh` + `run` as `ssh_run`, `process` + `output` as `process_output`), so a tool with actions reuses the views of the tools it merged and older chats with the old names look the same.
   - **File links.** `renderMarkdown` marks links whose target is a local path (relative, `C:\…`, `file://`) as
     `a.file-link[data-path]` with `href="#"`; one delegated click handler opens them with the operating system
     through `files.open`, so a link never navigates the app. Web links keep `target="_blank"` (the desktop shell
     opens them in the default browser).
   - **Plan strip.** `TodoStrip` (in the composer dock) shows the session's `meta.todo` while any item is open:
     `done/total` and the current item, expanding to the checklist.
   - **Idea chip.** `IdeaChip` (in the composer dock, after the plan strip) looks for the open idea the first message
     continues: `ideas.recall` about 1 s after typing pauses (12 characters or more, again when the text changed by
     20), and once more on the sent text of a chat this window saw empty (opening an old chat never asks). A match
     shows one line: the idea's title, **Add** (`ideas.attach`: the idea joins the chat as an `idea` notice) and ✕.
     Added or dismissed, that chat is not asked again; the state is per chat in `composer/ideaRecall.svelte.js`.
     The send never waits for it.
   - **Idea cards.** `IdeaCards` (in the composer dock) shows the offers `ideas.suggestions` returns, for the window
     rather than a chat — the chat a plan came from is closed. Two kinds: a **plan a closed chat left unsaved** (title,
     summary, **Save** / **Edit** / **Discard**) and an **idea a commit may have finished** ("may be done", the idea's
     title, the commits linked to it, **Mark done** / **Dismiss**, a green rule). Both are offers: nothing reaches the
     backlog and nothing is marked done without a click. They arrive by the unscoped `ideas.suggested` event; a new
     card after a restart is read once from `ideas.suggestions`. The Ideas tab carries the same offers as one
     "waiting for you" line.
   - **Shown images.** A successful `show_image` result becomes its own `shown` item (`ShownImage`: the image, the
     caption, name · size), never folded into a steps group; a click opens the lightbox.
   - **Goal strip.** `GoalStrip` shows `meta.goal` (`plugins/NetPI.Goal`) unless it was cleared: the state (Goal,
     Paused, Needs you, Achieved), the objective, automatic runs and tokens, and pause / resume / edit / clear; an
     active goal with no run for 1.5 s also offers "continue". Why it paused, or the completion summary, shows
     under it. `/goal [text]` sets it (asking before it replaces an open goal); a bare `/goal` edits it.
   - **Live thinking.** A streaming thinking step is a `ThinkingRow` too: spinner, time so far and the latest line;
     open, it shows the thinking so far (markdown at most every 150 ms, following its end). The choice is
     `chat.liveThinkingOpen` (null: the "Expand thinking" preference): later answers in that chat start that way,
     and their finished thinking rows keep it when the message arrives.
   - **Pinned to the bottom.** The list follows new content while it is pinned; only the user scrolling up unpins it.
     A view that shrinks (the plan strip or queue chips appearing) or content that grows never does: the resize
     observer re-pins.
   - **Steps** groups fold by the Steps preference (`prefs.steps`): `done` (default) folds a group of more than 3
     steps into `▸ N steps · time · tool counts` once its run has finished; `folded` folds from the first step on,
     also while the agent works (adding a second step cannot shrink an open row into a folded head line), and the
     group the agent is adding to shows its latest step on that line; `open` never folds. Steering input does not
     end a run. The user's choice to expand or collapse is kept per group in `chat.expanded`.
   - **Fork.** A user message's hover actions have **Fork** (`sessions.fork` up to the message before it): a new chat
     with the conversation before it opens in a tab, with the message's text in its box to change and send again. An
     answer's footer has **Fork** too (up to the answer). Not in a subagent's chat. A fork's header has a crumb back to
     the chat it came from (`meta.forkedFrom`), while that one exists.
   - **Questions.** An `ask_user` call is an item of its own (`ask`, `AskCard`), below the message that gives its
     context; nothing covers the chat. While it waits (`lib/state/asks.svelte.js`: `ask.pending` on connect,
     `ask.asked` / `ask.closed`) the card is open: the questions and their options (radio buttons, or checkboxes when
     several may be picked). A click answers a single question; otherwise Send sends the picks. The message box
     answers too, in the user's own words (with any picks): its placeholder says so, Enter answers, and Alt+Enter (or a
     message with images) sends a new message instead, which ends the question unanswered. Once it stops waiting the
     card is one line, `Question · question → answer` (the answer keeps its room on a narrow chat), which opens to every
     question and its answer; a stopped run or a new message instead reads "not answered". The run status line says
     "Waiting for your answer", and the chat's tab and session row get an `asking` dot (warn colour).
   - **Guardrails.** A tool call that waits for the user's OK (a guardrails `ask:` rule; `asks.approvals` from
     `guard.pending`, `guard.asked`, `guard.closed`) shows "needs your OK" on its row, with a bar under it: why (the
     rule; for a path, the path) and **No** / **Allow in this chat** / **Allow** (`guard.answer`, scope `session` or
     `once`); a call that runs because its rule was allowed in this chat reads "allowed in this chat" (`guard.cleared`
     `by: 'session'`), one the second opinion cleared reads "checked". Its steps group stays open while it waits. A
     call that a hook blocked reads "blocked" (the ban icon), like a skipped one: it never ran. The tab dot is `asking`,
     the run status line says "Waiting for your OK", and a Windows toast says what it wants to run or change.
   - **Model turns.** Under the last step of each model turn (an assistant message that ended in tool calls; under its
     question card when asking was the last thing it did), a dim line (`TurnLine`, `lib/turn.js`) gives that turn's
     numbers: the time to its first token (`meta.ttftMs`), how
     much of the prompt the backend reused from its cache, the output speed (output tokens over the time after the
     first token), and the tokens in (the whole prompt) and out. On a narrow chat the items at the end are dropped
     whole; the tooltip has the exact numbers. A turn that ends in an answer has the same numbers in the answer's
     footer (on hover: model, duration, then these, then the time), which covers the top of the next item while it
     shows. The "Model turn details" preference (`prefs.turnDetails`, on by default) switches the lines off.
   - A running shell command shows its last output lines under its row once it has run for a second, so quick
     commands don't flash open and shut.
   - **Run status.** `RunStatus` is the one line that says the agent is busy, just above the composer: an animated
     glyph, a word and the run's time. The word is `Thinking` or `Writing` while the model does that, the activity
     for waits (`Waiting for agent qwen`, `Waiting for 2 agents`, `Waiting for a free instance`), otherwise a playful
     verb chosen once per run (Tinkering, Pondering, …). The running tool shows in its steps group, not here. The line
     floats over the chat's bottom room, so coming and going moves nothing; the chat header only shows how a run
     ended badly (failed, cancelled).
   - **What the model got.** Everything NetPI adds to a request can be read where it happened. A `prompt` item
     (`PromptRow`, from `context.prompts`, refreshed on `context.prompt`) is the system prompt: the first above the
     first message (when the window starts there), one after a profile switch where it was rendered again; it opens
     to the prompt as sent and the tool definitions that went with it (each opens to its JSON). Every notice and
     summary (`NoticeRow`) opens to exactly what was sent: the text wrapped in `<system-notice kind="…">` (or
     `<conversation-summary>`), with its size and a copy button (`SentBlock`); a subagent's report, an agent's
     message and a summary open rendered, with a toggle to see them as sent.
   - Thinking rows are collapsed by default.
4. **Streaming.** `stream.delta` appends to plain strings inside `StreamState`; they reach `$state` at most once
   per animation frame (`lib/frame.js`, with a 120ms timer fallback when the window is hidden). The streamed text
   re-renders its markdown at most every 100ms, is never highlighted, and its caret takes no width. The final
   `message.added` replaces the stream items with the message's own, which are laid out the same. `tool.output` chunks go to `LiveTool` buffers, which keep the last 200KB, flushed the
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
  a newline. **Esc** stops the run (`agent.abort`); Esc in another field (a title rename, the session search) only
  leaves that field. Settings can switch sending to **Ctrl+Enter**; the hints follow.
- `/` opens the commands popup: the built-ins `/new /rename /agent /project /goal /settings /help /abort`, plus
  commands from `ui.commands`. A command with `rpc` is called with `{ sessionId, args }`, and a string result
  is shown as a toast. A command with `clientAction` is handled by the client:
  `openTab:<pluginId/tabId>`, `insert:<text>` or `settings`. The session's skills are listed too, as `/skill:name`
  (`lib/skills.js`: `skills.list`, cached for 10 s per session); choosing one inserts `/skill:name ` and the message is
  sent as typed (the skills plugin loads the skill for it).
- `@` opens a file popup backed by `files.search`; picking a file inserts `@rel/path`.
- Images can be attached with the button, pasted or dropped. They show as thumbnails and are sent as
  `{ mediaType, data }`.
- The agent picker (`AgentPicker`, from `agents.list` / `agents.changed`: `app.slots`) shows the chat's agent with its
  state dot; a chat without one shows the agent it would take (the first on its model), or "Choose an agent". The menu
  lists the agents (state: ready, busy, N waiting, not loaded, switched off; busy/instances; price), filters past three,
  and ends with **New agent…** (`ModelMenu`: the model list grouped by provider with status, context and slots, also
  behind every model field in the settings as `ModelSelect`; the new agent's id is a slug of the model, and the chat runs
  on it) and **Manage agents** (Settings → Agents & budget). Choosing one calls `agents.use` (the chat's `meta.agent` and
  model). New chats start on the agent chosen last while it is active, else the first active one. The effort picker offers "default" plus
  `model.reasoning.efforts` and calls `sessions.update { reasoning }`; `''` means the model default.
- The profile picker (shown once there are profiles) sets the chat's profile (`profiles.apply`): free before the first
  message; in a started chat it says the system prompt and tools change and the chat is read again (with its size).
- The project button (folder icon + name, dimmed “No project” while none is attached) sets the session's project
  (`sessions.setProject`); a toast confirms the change. Its picker popover opens above the button (anchors inside
  the composer place `top-start`), with the search box and the Edit / New project / Manage footer. The chat header
  shows only the title (and a crumb) and how a run ended badly.
- The tools button (a wrench; "N off" when tools are switched off) lists the chat's tools by category with a switch each
  (`agent.tools`, `agent.setTools`): free before the first message; in a started chat it says that the next model call
  re-reads the chat (with its size). **All on** undoes every switch.
- What the chat cost on paid models (`usage.session`, with its subagents) shows next to the context ring once it is more
  than nothing, refreshed on `usage.changed`; the tooltip splits the chat from its subagents.
- The context ring shows `used / window`, taken from `session.context` or `SessionInfo.contextTokens`. Hover reads
  it in one line; **pressing it opens a popout** (idea-kp4fq5) with used / window / free and what the prompt is made
  of — `context.preview { sessionId }` gives the system prompt (≈ tokens, chars / 3.6) and the tools, with a line
  when the prompt is frozen. It fails soft: no Context plugin, and the rows say so. The popout's last line opens
  Diagnostics on the context view (`localStorage netpi.diag.view`, then `openPanelTab`), where the full prompt,
  tools, AGENTS.md and skills are.
- The person's own queued inputs (`agent.queue`, `source: "user"`) appear as chips; the × on a chip calls `agent.dequeue`.
  Internal ones (a subagent's report, a harness notice) are never shown: they are for the agent. `agent.notice` shows
  as a transient banner, which clears when the model streams again or the run ends. A compaction's banner is kept
  instead: "Compacting context…" while the summary is written, then its result until the model answers again (on a
  local model the first token after a compaction can take a while); a `/compact` in an idle chat shows its result like
  any notice.

### Settings dialog

The pages: **General**, **Agents & budget**, **Profiles**, **Models**, **Runs**, **Context**, **Tools**, **Plugins**, then
**settings.json** and **About**. Host settings are controls rendered from `settings.schema` (the host's and each
plugin's `SettingsSection`, placed on the page of its group; a plugin's section comes and goes with the plugin).
`SettingField` renders one control per type (switch, number with unit and range, text, secret with an eye, choice,
list, model with the searchable list, folder, file); a change saves that key alone (`settings.set`, on change or blur),
**Reset** removes it so the default applies again, a bad value is refused in place, and a badge says when a change
applies (`restart`, `new sessions`). Defaults are shown as they are, never as "built in": a text setting with a built-in
text (the opening of the system prompt, the AGENTS.md guidance) shows that text to edit, and stays unset while it is
unchanged; a path that is found at runtime (bash, pwsh, ssh, the browser) shows the path found.

The Agents, Profiles, Models and Tools pages list their items as rows (`SettingsRow`: a title, a line under it, badges);
each row opens its own dialog over the settings. Esc closes only the top one (popovers such as the model list are on
the same Esc stack, `escLayer()` in `Modal.svelte`).

- **Agents & budget:** `AgentsEditor` has one row per agent (`agents.<id>`: id, model, the note, badges for off / not
  loaded / busy, instances, price); `AgentDialog` edits one: its name (a new name moves it), on/off (`input.np-switch`,
  with its state), the model with the searchable list, instances (default: the model's slots, 1 on a cloud model; a
  warning when the agents on one local model have more instances than it serves), the note on when to use it, price
  overrides and a daily cap; the price, context, local/cloud and today's spend come from `agents.list`. "Add agent" opens
  the model list and then the new agent's dialog (the id is a slug of the model). `BudgetView` shows this period against the monthly
  and daily budgets and what each model cost (`usage.summary`), above the budget settings.
- **Profiles:** `ProfilesEditor` has "New chats start with" (`profiles.defaultProfile`) and one row per profile (its name,
  the first line of its instructions, its tools); `ProfileDialog` edits one: the name, the instructions that replace the
  opening of the system prompt (they start as the current opening; unchanged they stay unset), whether new chats
  start with it, and the tools as checkboxes by category (with all on / all off per category).
- **Models** and **Tools:** one row per provider or tool section (its help, "N changed", "off"), each opening
  a dialog with that section's settings. The Projects dialog sets a project's default (`projects.update { meta: { profile } }`).
- **Plugins:** `PluginSwitches` lists every plugin with a switch (`plugins.setEnabled`, confirmed) and the
  tools it brings, and the names in `tools.disabled` when there are any (× shows one again). Single tools are switched
  per chat (the composer's tools button).

**General** holds the UI preferences (`prefs` in `lib/state/ui.svelte.js`, kept in localStorage and the host's
`ui.state`): theme, send key, Steps (expanded / fold when done / folded), expand thinking, model turn details, chat
width (normal 900px, wide 1200px, full; the `--chat-max` token), zoom and spellcheck in the message box. Zoom: in the
desktop app the buttons call `desktop.zoom` and the shell remembers the factor (Ctrl + wheel and Ctrl + / − / 0 too);
in a browser its own per-site zoom does that. **settings.json** edits the host settings as JSON and reloads when the
file changes elsewhere while it has no unsaved edits.

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
- **One baseline per line.** Centering a row's items does not line up texts of different sizes or fonts: an 11px
  mono time next to a 12px name sits about 1px high. Texts share a baseline (`.np-baseline` on the `.np-line` or
  `.np-fit`); in a row with a dot, icon or button, only the texts go into a baseline group
  (`<span class="np-line np-baseline np-grow">`), and the row centers the rest. Examples: the Work tab's agent,
  instance and process rows, the folded steps line in the chat.
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
- **Work:** an agent reads its name, a status pill and its switch; line 2 shows `busy/instances` and the model (a model
  call without an agent: the model, then `busy/slots` and the provider). Runs use three lines: name + activity +
  elapsed, title or task, and meta. A process shows its command + elapsed/exit, then
  pid · bg · size · cwd … time ago.
- **Ideas:** a full-width title, a 2-line summary, then one meta line: status pill · project badge · priority · tags ·
  sections · time. In an expanded card the actions are **Send to chat**, **+ Section**, edit, and ⋯ (move
  up/down, copy id, delete).
- **Diagnostics:** the runtime facts drop out as the panel narrows. The view switcher collapses to icons. A
  plugin row has the name and state pill, one meta line (id · version · load ms · count), hover reload and ⋯,
  and a failed plugin's error clamped to 3 lines (click for all). Log messages clamp to 3 lines, and clicking
  opens the message and its exception.

### Built-in plugin tabs

Each one is a Svelte module in `plugins/<P>/ui/`, built by `build:plugins` like any other plugin tab. Bundle
sizes (minified; Svelte runtime and kit included): Work 88KB, Ideas 90KB, Diagnostics 99KB, Files 67KB.

**Work** (`netpi.work`, right). One `work.snapshot` feeds four collapsible sections, each with a count. The
open or closed state of each section is remembered (`storageKey`).

- A summary line shows active runs, busy instances (+ queued), running processes and today's tokens.
- **Agents** (`AgentPool`): every agent the user set up, always: a state dot and pill (`ready` `busy` `full` `queued`
  `not loaded` `off`), the model, an on/off switch (`agents.setEnabled`), why an inactive one can't take work, capacity
  pips (a bar when over 8) with `busy/instances`, and the runs on it and the ones waiting, with elapsed time (clicking
  one opens its session). Model calls without an agent follow under "Other model calls" while they run. Without agents:
  "No agents set up" and a link to the settings (`ctx.app.openSettings('agents')`).
- **Runs:** the active runs as a tree (subagents nest under their parent). Each row shows a status dot,
  activity, elapsed time, the session title (or the task, for a subagent) and a one-line meta: model · turns ·
  tools · tokens · agent. Clicking a row opens the session, and the stop button calls `agent.abort { sessionId }`.
  Finished runs are listed below under **Recent** (result or error, TimeAgo), with **Show all** past 6.
- **Processes:** running processes first, then **Recent**. Expanding a row fetches `processes.output` (tail
  300 lines) and appends live `process.output` chunks. It falls back to polling every 2s when no chunk has
  arrived for 3s, and fetches once more on `process.exited`. The kill button is a two-step `ConfirmButton`
  that calls `processes.kill`.
- **Usage today:** this month's spend on paid models against the budget (`usage.summary.budget`: a bar, today's spend,
  "spent" when it is), then per provider input ↑, output ↓, cache read, plus a token budget bar when `budgetTokens` is set.
- Updates: `agent.status`, `agents.changed` and `process.started/exited` are applied in place, and a debounced
  `work.snapshot` (250ms; 400ms after `usage.recorded` and `usage.changed`) reconciles them. A 30s timer refreshes the snapshot
  while the tab is visible; while it is hidden, events only mark it dirty and it refreshes on show.

**Ideas** (`netpi.ideas`, right). Shows the one backlog, which lives in NetPI's own database (every idea carrying a
`project`), with `ideas.list` (no parameters) and `projects.list` for the filter.

- The header shows the project filter (the active project by default, following the active project until the user
  picks *All projects*, *Global (unbound)* or another project), a **storage mark** and a **+** button. The mark comes
  from `ideas.list().storage` and says which database and scope the backlog is in ("… lives in the netpi.db database
  (netpi.ideas, version 1) — it is not a file you can edit"): there is no path to open any more, so it is a fact, not
  a link. The footer line says how many of the ideas are shown and names the database.
- Filters: search over title, summary, tags and sections; the project filter above; a status menu (**Active** = open,
  planned, in-progress; **All**; or one status), each with counts; and a **#** tag menu (multi-select; an idea matches
  when it has any of the selected tags). Selected tags show as removable chips below the filters.
- Cards show a project badge (folder + name, a globe for the unbound *Global*), a status pill (a menu that calls
  `ideas.update { patch: { status } }`), priority, tags, section count, an agent icon for agent-created ideas, and the
  update time. Expanding a card renders the summary and its sections as markdown.
- Editing: title, summary, priority and tags inline; add, edit (kind, title, markdown; Ctrl+Enter saves) and
  remove sections through `addSections`, `updateSections` and `removeSectionIds`; delete with the host confirm
  dialog. New idea also has a project picker (default: the active project; *Global* for unbound).
- Reordering uses the grip (shown on hover; HTML5 drag with a drop indicator) or **Move up / Move down** in the
  ⋯ menu, and sends the full id order to `ideas.reorder`. Delete is in the ⋯ menu too.
- **Send to chat** calls `ideas.toPrompt` and `ctx.app.insertText`.
- **Edits carry a revision.** An update sends the `revision` the idea had when its card was opened
  (`expectedRevision`), so a change made elsewhere in the meantime comes back as a `conflict` instead of overwriting
  it. The editor keeps what was typed, a toast says the idea changed somewhere else, and a line at the foot of the tab
  names it — reopen the card, see the other version, apply the change again. Nothing typed is thrown away.
- The list refetches on `ideas.changed`, which now arrives after every committed write (from this window too, and from
  the agent). It is a notification rather than a guarantee, so the tab treats `ideas.list` as the canonical read: a
  missed event costs one more refetch, never a stale card. The default filter follows the active project on
  `ctx.app.onChange`.

**Diagnostics** (`netpi.diagnostics`, right). `diag.snapshot { events: 300 }` plus a runtime line (pid, working
set, uptime, threads, framework; the full details are in its tooltip). A segmented control (buttons carry
`data-value`) switches views, and the last view is remembered.

- **Problems:** `diag.problems`, polled every 5s while visible; when there are errors or warnings a strip above the views
  shows how many and the worst one, and a click lists them all with their hints.
- **Calls:** `diag.calls { limit: 150 }`, polled every 2s while visible, newest first, with All / Running / Errors: the
  state, the model, time to the first token, the duration, the agent, tokens and the error. A click shows `diag.call`
  (the request's size, the response, retries, the error's status) and opens the chat.

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
  the system prompt with copy and expand, the tool list), `agentsmd.list` (instruction files with scope
  badges, copy path and reveal) and `skills.list` (the skills with scope, name and description, then their problems).

**Files** (`netpi.tools.files`, left). A lazy tree of the active session's working folder.

- `files.list { sessionId, dir }` runs per expanded folder. Ignored entries are dimmed; the eye button hides them.
- The header shows the project name, the root path (shortened from the left), the eye (when there are ignored
  entries), **collapse all** and **refresh**.
- The filter box calls `files.search` (debounced 140ms, 200 results) and shows a flat list.
- Clicking a file opens it with the operating system (`files.open`: its default app). Clicking a folder expands it;
  a folder in the filtered list is shown in the tree. The `@` button of a row (on hover or focus) inserts
  `@rel/path ` into the composer (quoted when the path has spaces; folders end in `/`).
- The context menu (right-click or ⋯) has Open, Insert @mention, Insert path, Copy relative path, Copy absolute
  path, Reveal in Explorer (desktop only) and Refresh folder.
- Keyboard: ↑ ↓ move, → ← expand and collapse, Enter opens, and the context-menu key opens the menu.
- The git line at the bottom (`files.git`; only in a git repository): the branch, the commits not pushed (↑) or not
  pulled (↓), and the uncommitted changes since the last commit, staged or not, in files and lines
  (`4 files +60 −400`). Clicking it lists the changed files in place of the tree, with a status letter (M modified,
  A added, U new, D deleted, R renamed, C copied, ! conflict) and their lines; they open and take `@` like the
  tree's files (a deleted one only takes `@`). It reloads with the tree, 0.8s after a tool call ends (`tool.end`),
  and when the window gets the focus back (a commit made in a terminal); while the tab is hidden, a tool call only
  marks it for a reload when the tab is shown.
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
  export async function refresh() { agents = await ctx.rpc('runs.list', {}); }
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

The kit also exports the helpers `timeAgo`, `duration`, `tokens`, `usd`, `bytes`, `relPath`, `basename`, `truncate`,
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
| narrow rows | `np-line` (+ `np-grow`), `np-meta` (+ `np-meta-plain`), `np-fit`, `np-baseline` (on `np-line`/`np-fit`), `np-hover-row` + `np-hover-actions` (`--row-bg`) |
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
- **Zoom.** The WebView zooms with Ctrl + wheel and Ctrl + / − / 0; the shell saves the factor in `window.json`
  (with the window placement) and restores it. `desktop.zoom { factor? }` reads or sets it (the Settings dialog).
- **Notifications.** When a chat needs the user, the page posts `{ type: 'notify', sessionId, title, body }`
  (`lib/notify.js`): a top-level chat's run finished or failed (unless it goes on by itself within 1.5 s or its goal
  is active), its goal was completed or is blocked, the budget asks, an agent asks a question (`Asks: …`), a tool call
  waits for the user's OK (`Wants to run: …`, `Wants to change …`); after one
  of these no "Finished" follows for that chat. The shell shows it
  only while its window is not the active one: a tray icon's balloon, which Windows shows as a toast, and a flashing
  taskbar button. Clicking the toast brings the window up and replies `{ type: 'openSession', sessionId }`. The
  preference `notifications` (Settings → General, desktop app only) turns them off.

## What the UI expects from the host (beyond PROTOCOL.md)

- `sessions.list` accepts `includeArchived: true` (a mixed newest-first result) and `archivedOnly: true` (archives only, which the
  sessions panel's "Show archived" section fetches as a separate, paged query so the newest active sessions cannot push an
  old archive out of the window). Both are in the protocol table.
- `sessions.update { model: '' }` and `{ reasoning: '' }` must reset to the default. The UI sends an empty
  string for "default".
- `runs.list { includeFinished: true }` is called at startup to seed the status dots, including failed and
  completed subagents.
- Scoped events carry a non-null `sid`. The UI also falls back to `d.sessionId`.
- `agent.status` is broadcast whenever `status` **or** `activity` changes. The run status line above the composer
  shows it in a word with the time since `startedAt`.
- A new chat is **transient** until its first message: not saved, not listed, no `session.created`. The first message
  materializes it (`session.created` → `message.added` → `session.updated`), and the profiles plugin gives it its
  default profile there (the `session.updated` that follows carries it; the hook still guarantees it is set before the
  first model call). The app never replaces a session with an older copy (`updatedAt`), so an RPC result that arrives
  after that event can't take the profile away again.
- `message.added` for a **steering** input has `meta.kind: 'steer'` (and `'queued'` for a queued follow-up),
  so the UI can tag the input and keep the run's steps grouped. `meta.agentName` and `meta.sessionId` on
  `agent-result` and `agent-message` notices enable the "open" link. A `budget` notice with `meta.canOverride` (the
  budget stopped a paid call and `budget.onLimit` is `ask`) offers "Let this chat go over" while it is the chat's
  latest budget notice; after a confirm it calls `budget.allow`. The top bar shows the budget (`BudgetPill`:
  `budget.status`, then `usage.changed`) only once it needs attention, amber from `budget.warnPercent`, red when spent;
  a click opens Settings on Agents & budget (`modals.settings = 'agents'`).
- Assistant messages that stop with `stopReason: 'error'` may carry `meta.error`, which is shown in the error
  row.
- `agent_*` tool results carry `details.sessionId` (and `name`, `status`) for the subagent link, or
  `details.agents[]` for multi-agent tools such as `agent` `wait`.
- `/plugins/*` is served without auth, as the host does today, or with the `netpi_token` cookie. A module
  import cannot send headers, so in dev the UI supplies the cookie through the proxy.
- Slash commands with `rpc` receive `{ sessionId, args }` and may return a string, which is shown as a toast.
  The `clientAction` values the UI understands are `openTab:<pluginId>/<tabId>`, `insert:<text>` and
  `settings`.
- `sessions.messages` has only `beforeSeq`. An `afterSeq` parameter would let the pruned window page forward;
  today it reloads the tail instead.
- A `UiTabInfo.panel` sent as a number (enum without a string converter) is accepted: `0` is left, `1` is right.
- The Work tab reads `usage.summary` providers' `budgetTokens` and `budgetUsed` (input + output + cache write),
  `AgentSlots.status`/`available`/`unavailable`/`disabled`, `SlotHolder.label`/`since`, and `ProcessInfo.outputBytes`/`background`/`agentId`.
- The Ideas tab expects `ideas.changed { backend: 'sqlite', database, scope, schemaVersion, file, reason? }` after every
  committed write, including writes made by agents, and reads `ideas.list().storage` for where the backlog lives. It
  sends `expectedRevision` on an edit and shows a refused one as a conflict instead of losing the text.
