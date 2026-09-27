# Project picker: from the top bar into the composer

Status: planned · 2026-09-27

## Goal

The chat's project selector moves out of the top-right of the top bar into the prompt input panel
(the composer bar), right after the profile button, with the same look as the profile button. The
chat header's duplicate project chip goes away too, and so does the model display it sat next to —
the header is left with crumb · title · (failed/cancelled status).

## Current state

- **`TopBar.svelte`** — top-right `.chip` (folder icon + name + chevron) for the active session;
  opens `modals.projectPicker = { sessionId, anchor: chipEl }` (popover places `bottom-end`).
- **`ChatHeader.svelte`** — a second, smaller project `.chip` (same picker, for that session) plus a
  `.model` span (cpu icon + model display name + `· reasoning`).
- **`Composer.svelte`** bar row: attach · `/` · `@` · sep · `ProfilePicker` · `AgentPicker` ·
  `EffortPicker` · `ToolsPicker` · spacer · key hints (while running) · `ChatCost` · `ContextRing` · send.
- **`modals/ProjectPicker.svelte`** — the shared picker popover (search, ↑↓/Enter, the Edit /
  New project / Manage footer; `sessions.setProject` for a session, `lastProjectId` in `select` mode).
  Its placement already special-cases anchors inside `[data-composer]` → `top-start` (above the
  button, left-aligned) — that branch exists for `/project`, which anchors the composer root.
- Other openers: `/project` (via `Composer.runCmd` → `ui.openProjectPicker`) and the start screen's
  "in [project ▾]" (`select: true`) — both stay as they are.

## Design

A new **`composer/ProjectPicker.svelte`** — a small button component that reuses the existing global
picker instead of duplicating it:

- Same visual design as `ProfilePicker`'s `.pick` button: 26px high, transparent, folder icon +
  ellipsized name, hover background. `max-width` ~120px (slightly under the profile's 140, see
  *Bar space*). No chevron — like the profile button.
- Label: the session's project name, or a dimmed **No project** (`class:none`, like "No profile").
  Tooltip: the project's path, or "Attach a project". `aria-label="Project"`, `aria-haspopup="dialog"`.
- Click toggles `modals.projectPicker` (`null ⇄ { sessionId, anchor: button }`). The shared popover
  does the rest: because the anchor is inside `[data-composer]` it opens **above** the button,
  left-aligned — exactly where a bar button wants it.
- Rendered for every session (subagent chats included — the old header chip was shown there too, and
  `sessions.setProject` works on any session).
- Placed in the bar immediately after `<ProfilePicker {session} />`; when no profiles exist the
  project button is simply the first after the separator.

## Changes, file by file

1. **`web/src/components/composer/ProjectPicker.svelte`** (new, ~40 lines) — the button:
   `projectOf(session)` for the label, `.pick` styles copied from `ProfilePicker` (including the
   `.none` dimming), the toggle above.
2. **`web/src/components/composer/Composer.svelte`** — import + `<ProjectPicker {session} />` after
   the `ProfilePicker` line in `.bar`. Nothing else: `runCmd`'s `openProjectPicker` and the
   `[data-composer]` fallback in `commands.js` keep working unchanged.
3. **`web/src/components/TopBar.svelte`** — remove the chip: the `{#if app.activeSession}` block,
   `chipEl`, `openProjectPicker`, the `project` derived and the `.chip` CSS. Keep the `projectOf`
   import (tab tooltips) and `modals` (settings button). The top bar keeps brand · tabs ·
   BudgetPill · connection · settings.
4. **`web/src/components/chat/ChatHeader.svelte`** — remove the project chip and the `.model` span:
   the button, `chipEl`, `project`/`model`/`modelRef` deriveds, `.chip`/`.model`/`.effort` CSS, and
   the now-unused imports (`projectOf`, `modelFor`, `sessionModelRef`, `modals`). No information is
   lost: the model shows in the agent picker (label and menu), the effort picker, the turn footer
   (with the "Model turn details" preference) and Settings. Header left: crumb, editable title,
   spacer, failed/cancelled status.
5. **`docs/UI.md`**
   - component tree: add `ProjectPicker` to the `composer/` line.
   - "New sessions and projects": "The top-bar chip, the chat-header chip and `/project` open
     `ProjectPicker`…" → "The project button in the composer bar (after the profile) and `/project`…".
   - Composer section: a bullet after the profile one — the project button (name or dimmed
     "No project"), the popover opening above it (`top-start` for anchors inside the composer),
     `sessions.setProject` and its toast; note the chat header now shows only the title (and crumb)
     and how a run ended badly.
   - fix the two stale references: the RunStatus bullet ("the chat header only shows…") still holds,
     and the baseline-rows example that cites "the chat header's model" needs a different example.
6. **`tests/NetPI.E2E/ui/smoke.mjs`** — after the prepared session opens, add checks: a composer-bar
   button labelled with the session's project ("ui-demo") is present; clicking it opens a `.popover`
   containing the "Attach project…" search input and the current project's row; Esc closes it. (The
   host-side behaviour is already covered by the Host/Agent suites; this adds the new UI path.)

## Decisions (settled)

- The chat header's project chip: **removed** (the composer bar is the one place).
- The chat header's model display: **removed** (asked for alongside it).
- Unset label: dimmed **"No project"** (same wording as the old top-bar chip, ProfilePicker dimming).

## Bar space (the one real risk)

The bar gains ~120px. Idle bar at "normal" chat width (900px) has room (spacer absorbs it); the
worst case is *running* + cost + ring: attach/slash/@ (84px) + sep + Project (≤120) + Profile (≤140) +
Agent (≤200) + Effort + Tools + key hints (~190) + cost + ring + send. Verify at 900px in that state;
if it crowds, first shrink the project label's `max-width` to ~100px, then consider dropping the
"steer/queue" words in the running hints (the kbd keys already say it).

## Verification

1. `npm run build` (UI → `web/dist`, copied to `artifacts/app/wwwroot` by the server build); with
   NetPI running use `.\build.ps1` (it stages the new wwwroot; restart the app to pick it up).
2. Manual: a chat with a project — button shows the name; click → picker above the button;
   search / arrows / Enter work; picking another project toasts "Project: …" and the Files tab root
   changes; "No project" → dimmed label + "Project detached"; top bar has no chip; the header is
   title + status only; `/project` still opens the picker; the start screen is unchanged; a subagent
   chat still shows the button.
3. `dotnet tests/NetPI.E2E/bin/Debug/NetPI.E2E.dll` (build first) — the new smoke checks pass;
   review the screenshots (the composer bar in ui-01…ui-04).
