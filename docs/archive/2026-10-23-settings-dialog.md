# Settings dialog: new categories, search, sub-pages and sub-dialogs

Status: done 2026-10-23, all of it in one go instead of the phases below (the owner's call), with the recommended
choices: sections open in place, the page set as proposed, and the contract fields (`Subgroup`, `Advanced`, `Keywords`).
Where it ended up differing: every list setting has **Edit…** (not only the long ones); Reset section shows when more than
one key is set; a page lists its section rows before its inline sections; Tool exposure is reached from Tools (and the
search), with a read-only `tools.exposure` RPC in the Runtime plugin. The result is described in docs/UI.md ("Settings
dialog"). Based on `web/src/components/modals/SettingsModal.svelte`, `web/src/lib/settings.js`,
`SettingField.svelte`, `src/NetPI.Abstractions/SettingsSchema.cs` and the live `settings.schema` (2026-10-23: 38 sections,
217 settings, plus 10 UI preferences hand-written in the modal).

## What is wrong today

- **Categories follow the code, not what a person looks for.** The `Agents` group is titled "Runs" and holds Schedules,
  Webhooks and Phone. Budget is declared in `Models` and moved to Agents by a special case. Retries sit under Models.
  Tools is the catch-all (15 sections, and the fallback for any unknown group): Guardrails, Ideas, Memory, Code search,
  Embeddings, Decisions and MCP limits sit next to Shell and Files. General mixes the look of the chat with
  `server.port`, `database.sqlitePath` and `logging.level`. Data & backups has the same icon as Context.
- **No search.** "Stall timeout" lives in Models → Retries (a dialog) and again in Models → Claude CLI. Finding a key
  means knowing the plugin that owns it, or reading settings.json.
- **Three layouts.** Runs and Context render every section inline, Models and Tools show rows that open a modal over
  the modal, and Agents and Profiles have their own editors. Nothing says where you are, and a section dialog cannot be
  linked to.
- **No overview of what you changed.** "N changed" exists only on rows. Nothing lists every setting that differs from
  its default.
- **Flat big sections.** Web has 16 settings in one list (search, fetch and browser mixed together), MCP 9 limits,
  Guardrails 9, Ideas 11. Rarely used limits get the same weight as the on/off switch.
- **Long help everywhere.** Every field shows its full help text, so a page is mostly prose.
- **UI preferences are hand-coded** in the modal (theme, send key, steps, thinking, tool calls, turn details, chat
  width, zoom, spellcheck, notifications), so a search or "changed" filter built on the schema would miss them.
- **Some settings are file-only:** `tools.disabled`, `tools.deferred`, the per-model overrides `providers.instances.<id>.models.<id>`.
- **Deep links stop at the page:** `modals.settings = 'agents'`, and the plugin API `openSettings(page)`.

## Target information architecture

The left nav gets headers. Each page is a list of sections; an unknown group goes to **Other**, not Tools.

| nav group | page (id) | sections |
|---|---|---|
| | **General** (`general`) | Appearance (theme, chat width, zoom) · Chat (send key, steps, thinking, tool calls, turn details, spellcheck) · Notifications · Defaults (`workspace.default`) |
| AI | **Models** (`models`) | Providers (instances, Add provider) · Model list (`defaultModel`, `models.refreshSeconds`, slots) · Connection & retries (`retry.*`) |
| | **Agents & budget** (`agents`) | Agents (editor) · Budget (view + `budget.*`) |
| | **Profiles** (`profiles`) | profiles editor |
| | **Runs** (`runs`) | Limits (`agent.*`, `agents.maxDepth`) · Goals · Plan mode · Nudge · Loops · Code mode |
| | **Context** (`context`) | System prompt · AGENTS.md · Skills · Compaction |
| Capabilities | **Tools** (`tools`) | Files · Shell · Web & browser · Windows apps · Images · SSH · Language servers · MCP · Tool exposure (new, below) |
| | **Knowledge** (`knowledge`) | Embeddings (`embed.*`, which the others need) · Memory · Code search · Ideas · Decisions |
| | **Automation** (`automation`) | Schedules · Webhooks · Phone |
| | **Safety** (`safety`) | Guardrails · Workspaces (writer isolation, branch prefix, worktree root) |
| System | **Data & backups** (`data`) | Backups |
| | **Plugins** (`plugins`) | plugin switches |
| | **Advanced** (`advanced`) | Server (`server.port`, `server.devOrigins`), Storage (`database.sqlitePath`), Logging, `plugins.quiet`, unknown keys (below) · settings.json |
| | **About** (`about`) | as today |

Page ids become lowercase and stable (they are deep-link and test targets). The host splits its `core` section into
the sections above (Defaults, Model list, Server, Storage, Logging) and each plugin changes its `Group` string; the
vocabulary is written down in `SettingsSection.Group`'s doc comment and docs/PLUGINS.md.

## Search

- A search box at the top of the nav. Ctrl+F (or typing while the dialog has focus, outside a field) focuses it; Esc
  clears it, a second Esc closes the dialog.
- The index (`web/src/lib/settingsSearch.js`, pure, unit-tested): every `SettingInfo` (label, key, help, options,
  section and page title, keywords from phase 4), the UI preferences, and the items of the custom pages: agents (id,
  model, note), profiles (name), providers (name, kind), plugins (name, tools), and actions ("Back up now",
  "Add provider", "Add agent").
- Matching: every word of the query must match somewhere; label prefix > label word > key > section title > help. A
  dotted query (`shell.timeout`) matches keys directly.
- Results replace the content pane, grouped by "Page › Section". Each result is the **live control** (change it
  right there), with its breadcrumb; the breadcrumb jumps to the section and highlights the field.
- Filter chips under the box: **Changed** (every key that is set, i.e. differs from its default: the overview that is
  missing today, with Reset per field and per section) and **Needs restart** (`applies: restart` keys that are set).
- No match: "Search settings.json for '…'" opens the JSON page with the cursor on the first occurrence.
- The command palette lists "Settings: <label>" entries; `/settings <words>` opens the dialog with that search.

## Sub-pages and sub-dialogs

Two levels, each for one job:

- **Sub-pages for sections (inside the dialog).** A small section (up to ~4 settings) renders inline on its page; a
  bigger one is a row (`SettingsRow`: help line, "N changed", "off", and its on/off switch on the row itself when the
  section has a `….enabled` key) that drills in **in place**: breadcrumb "Tools › Web", Back (Esc, Alt+Left). Nav and
  search stay visible, and the section is linkable. This replaces today's modal-over-modal for Models and Tools rows.
- **Sub-dialogs for editors** (stacked modals, as AgentDialog and ProfileDialog already are): forms on one item, or
  editors a single field cannot be.
  - **Tool exposure:** every tool by plugin, with Off (`tools.disabled`) and Deferred (`tools.deferred`, `*`, `!name`)
    as checkboxes. Today both are file-only.
  - **List editor:** for list settings that grow (`guardrails.commands`, `guardrails.paths`, `webhooks.allowFrom`,
    `phone.allowFrom`, `shell.hideEnv`, `skills.paths`): one row per item with add/remove and per-item validation (a
    CIDR check for allowFrom). The inline field stays for short lists and opens the editor from an "Edit…" button.
  - **Skills picker** for `skills.disabled`: the skills found (`skills.list`, global ones without a project), with switches.
  - **Provider models:** per provider, its models with the `models.<id>` overrides (display name, hidden, context
    window, max output, concurrency).
  - **Reset section** asks first, in a ConfirmModal.

A section with a `<x>.enabled` key that is off shows its other fields disabled with "Webhooks are off" (a UI
convention, like `sectionState` already reads it; no contract change).

## Fields

- Help shows its first sentence; "More" opens the rest (the full text still goes into search).
- Default shown as "Default: 120 s" under a changed field; Reset as today.
- A field's menu (⋯): Copy key, Show in settings.json (opens the JSON page with the key selected), Reset.
- UI preferences become a declarative list (`web/src/lib/uiPrefs.js`: key, label, help, type, options) rendered by
  the same field component, so search, Changed and Reset cover them.

## Deep links and memory

- `modals.settings` takes `true`, a page id, or `{ page, section?, key?, q? }`. `openSettings(target)` in the plugin
  context takes the same (a string still means a page). Callers updated: BudgetPill, AgentPicker, commands, plugin tabs.
- The dialog reopens on the last page and section (a per-viewer pref).

## Layout

- Width `min(940px, 94vw)`, height `min(76vh, 760px)`. Below ~640px the nav becomes a page dropdown above the
  content, so the dialog still works in a narrow window.
- Nav icons: one per page (Data & backups no longer shares Context's).

## Contract additions (phase 4, shared contract)

On `SettingInfo` (all optional):

- `Subgroup`: a heading inside a section (Web: Search, Fetch, Browser; Ideas: Commit notices, Similar ideas, Auto-refine;
  MCP: Limits; Retry: Timeouts, Backoff).
- `Advanced`: the section shows these under "Show N more settings" (MCP's limits, `retry.baseDelayMs`,
  `codesearch.maxChunkLines`, `embed.batchSize`…). Search, Changed and a set value always show them.
- `Keywords`: words for search that are not in the label ("api key", "proxy", "timeout", "port").

The defaults keep every current declaration valid. Abstractions changes, so all plugins rebuild and the change waits
in `.pending` for the next start; full gates.

## Unknown keys (Advanced page)

Keys in settings.json that no schema declares and that are not under a dynamic root (`providers.instances`,
`agents.<id>`, `profiles.<id>`, `mcp.servers`…): listed with their value and a Remove button (`settings.set null`).
Catches typos and leftovers such as `agents.queueMax`. The dynamic roots are listed in one place in `lib/settings.js`.

## Phases (one commit each)

1. **Groundwork (UI only).** Page registry with nav groups and lowercase ids in `lib/settings.js`; `uiPrefs.js`;
   `modals.settings` / `openSettings` targets; unit tests for page assignment and target parsing.
2. **Recategorize.** Host splits `core`; plugins change `Group` (Safety, Knowledge, Automation, Advanced, …); General's
   preferences grouped; docs/SETTINGS.md headings and docs/UI.md "Settings dialog" rewritten; `tests/NetPI.E2E/ui/settings.mjs`
   uses the new page ids. Plugins and host rebuild; host files wait in `.pending`.
3. **Search and Changed.** `settingsSearch.js` with unit tests; results with live controls and breadcrumbs; Changed and
   Needs restart chips; palette entries and `/settings <words>`. Journey: find a setting by search, change it from the
   result, see it under Changed, reset it.
4. **Sub-pages.** In-place drill-in with breadcrumb and Back, row switches, enabled-by greying, remembered place.
   Journey: open Tools › Web, go back with Esc, deep link to a key.
5. **Contract: Subgroup, Advanced, Keywords.** Abstractions, the dialog, annotations in the plugins with big sections,
   docs/PLUGINS.md. Full gates.
6. **Sub-dialogs.** Tool exposure, list editor, skills picker, provider models, reset section.
7. **Polish.** Help "More", field menu, show in settings.json, responsive nav, unknown keys.

Phases 1 to 4 make the visible difference; 5 to 7 can be taken in any order after them.

## Verification

- Each phase: `npm run build` (commit the bundle with its sources), `scripts/test.ps1 -Suite UI` for the pure logic
  (`settings.js`, `settingsSearch.js`, `uiPrefs.js`), and the journeys `e2e.ps1 -Changed` picks (`settings.mjs`, and
  `work.mjs`, which opens Settings).
- Phase 2: the host's settings tests (`tests/NetPI.Host.Tests/SettingsTests.cs`, `CoreRpcTests.cs`) for the split
  `core` section.
- Phase 5: full gates (shared contract).
- After each merge: `.\build.ps1 -Publish`, and report what it staged and what waits in `.pending`.

## Open decisions

- In-place sub-pages for sections (recommended) or stacked dialogs as today.
- The page set above: in particular Knowledge, Automation and Safety as their own pages, and Decisions under Knowledge.
- Whether phase 5 (a contract change that needs a restart) is worth it, or Subgroup/Advanced stay a UI-side table
  keyed by setting key. Recommended: the contract, because policy stays with the plugin that owns the setting.
