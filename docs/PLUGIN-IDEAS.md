# Ideas plugin (`netpi.ideas`)

A backlog of ideas, research, plans and deferred work for the user and for agents. Agents work with it through the
`ideas` tool. The user works with it in the **Ideas** tab (right panel) and through `/idea <title>`.

- Plugin: `plugins/NetPI.Ideas`, id `netpi.ideas`, start order 80.
- Tab: `{ id: "ideas", title: "Ideas", panel: "right", icon: "idea", order: 20, module: "ui.js" }`. The UI module goes in
  `plugins/NetPI.Ideas/wwwroot/ui.js` (source in `plugins/NetPI.Ideas/ui/`) and is served at `/plugins/netpi.ideas/ui.js`.
- Slash command: `{ name: "idea", argsHint: "<title>", rpc: "ideas.quickAdd" }`.
- Setting: `ideas.fileName` (default `"ideas.json"`; only the file name part is used; the file lives in `~/.netpi`).

## Where ideas are stored

**One global file for everything:** `~/.netpi/<ideas.fileName>` (name: setting `ideas.fileName`, default `ideas.json`).
There is no per-project file anymore. Every idea carries a **`project`** property — a `{ id, name }` snapshot of the
project it belongs to, or absent when it is not bound to a project (the "global" ideas):

| Situation | File | `project` on the new idea |
|---|---|---|
| Agent tool, session with a project | `~/.netpi/ideas.json` | the session's project |
| Agent tool, session without a project | `~/.netpi/ideas.json` | absent (unbound) |
| Agent tool with a `project` argument | `~/.netpi/ideas.json` | that project (`"global"`/`"none"` → unbound) |
| RPC `ideas.add` with `projectId` | `~/.netpi/ideas.json` | that project (a project id or name, `"global"` → unbound) |
| RPC `ideas.add` with `sessionId` only | `~/.netpi/ideas.json` | the session's project (absent when the session has none) |

`projectId` wins over `sessionId`. An unknown `projectId`, or an unknown `sessionId` in `ideas.add`/`ideas.quickAdd`,
gives an RPC error `not_found`. The `~/.netpi` folder is created when needed.

### Migrating the old per-project files

Earlier versions kept one file per project (`<project.path>/.netpi/ideas.json`, and before that
`<project.path>/ideas.json`). At start, the plugin migrates them into the global file, for every project in the
session store:

- The ideas are appended to `~/.netpi/<ideas.fileName>`, stamped with `project: { id, name }` (unless the idea already
  has a `project` field) — an idea whose `id` already exists in the global file gets a new id.
- A source file is deleted only after the merge has been written. While the global file is unreadable or unwritable
  (e.g. invalid JSON), the migration is skipped (logged) and retried on the next start; the sources stay where they
  are and are never modified.
- The migration is idempotent: once the sources are gone, nothing happens.

## File format

```jsonc
{
  "version": 1,
  "ideas": [
    {
      "id": "idea-k3x9q2",                 // "idea-" + 6 chars [0-9a-z], unique in the file
      "title": "Cache model list",
      "summary": "Avoid refetching /v1/models on every session switch.",
      "status": "open",                    // open | parked | planned | in-progress | done | rejected
      "priority": "medium",                 // low | medium | high
      "tags": ["perf"],
      "project": {                          // absent = not bound to a project ("global")
        "id": "proj_abc123",
        "name": "NetPI"                    // a snapshot; refreshed when the idea is written with a known project
      },
      "createdAt": "2026-09-23T20:15:00Z", // ISO 8601 UTC, second precision
      "updatedAt": "2026-09-23T20:15:00Z",
      "createdBy": "user",                 // "user" | "agent:<agentId>"
      "sections": [
        { "id": "sec-4f0a", "kind": "research", "title": "Findings", "content": "markdown…", "updatedAt": "…" }
      ],
      "sessionIds": ["ses_…"]              // sessions that created or updated the idea
    }
  ]
}
```

- Section `kind`: `note | research | plan | requirements | design | decision | blocker | links | todo`. Common synonyms
  are mapped (`spec` → `requirements`, `tasks` → `todo`, …). Anything else becomes `note`. `title` is optional.
- Idea ids are unique within the file, and section ids are unique within their idea.
- The file is the source of truth. It is read on every request, so manual edits and `git checkout` show up right away.
- What a save preserves:
  - Unknown fields at every level (root, idea, section).
  - Line endings: CRLF when most of the file's line breaks are CRLF, else LF. New files use LF.
  - A UTF-8 BOM, if the file has one.
  - The indentation of the first indented line (tabs or 1–8 spaces). New files use 2 spaces.
  - What a save does **not** preserve: `//` comments and trailing commas. They are accepted when reading but dropped when
    the file is written.
- Writes are atomic: a temp file `.ideas.json.<rand>.tmp` in the same folder is renamed over the file. If the rename
  fails, the file is written in place. A per-file lock serializes all writes in the process.
- A file that is not valid JSON is **never overwritten**. Tools return an error, and RPCs fail with code `invalid_file`.
  A bare top-level array is accepted and wrapped into `{ version, ideas }` on the next save.

## Event

| type | scoped | data |
|---|---|---|
| `ideas.changed` | no (broadcast) | `{ file: string }`: absolute path, the same string as `ideas.list().file` |

The event fires after any change to the file, whether it came from this plugin (tools or RPC) or from an external
editor. Changes are debounced by 250 ms, so a burst of writes produces one event. The file is watched from startup.

**UI:** on `ideas.changed`, call `ideas.list` again. (There is one file, so no file comparison is needed.)

## RPC methods

There is one file, so the location parameters of earlier versions are gone: `ideas.list` takes no parameters, and the
other methods are addressed by `id` alone (ids are unique in the file). `ideas.add` keeps `sessionId`/`projectId` to
decide the new idea's `project` stamp. Results are plain JSON: ideas are returned exactly as stored, including
unknown fields.

Error codes: `bad_request` (invalid input: the message says what is wrong, for example the list of valid statuses or
projects), `not_found` (idea, session or project), `invalid_file`, `io_error`.

### `ideas.list`

`{ }` →

```ts
{
  file: string;                 // absolute path of the (global) ideas file
  fileName: string;             // "ideas.json"
  exists: boolean;              // false until the first idea is written
  ideas: Idea[];                // file order (= the user's manual order)
}
```

No filtering happens on the server. The tab filters by project, status and tag client-side.

### `ideas.get`

`{ id }` → `Idea`. The id is matched exactly, then case-insensitively, then with an `idea-` prefix added.

### `ideas.add`

`{ sessionId?, projectId?, idea: { title, summary?, status?, priority?, tags?, sections?, …extra }, prepend?: boolean }` → the created `Idea`

- `title` is required. Defaults: `status: "open"`, `priority: "medium"`, `tags: []`, `sections: []`.
- `tags` may be an array or a comma-separated string. A leading `#` is stripped and duplicates are removed
  (case-insensitive).
- `sections` is `{ kind?, title?, content }[]`. Ids and `updatedAt` are generated.
- The `project` stamp: `projectId` (a project id or name, case-insensitive) when given, else the session's project
  (via `sessionId`), else absent. `"global"`/`"none"`/`""` in `projectId` means unbound.
- `createdBy` is `"user"`. `sessionIds` is `[sessionId]` when a sessionId was passed.
- Extra fields of `idea` (for example `color`) are stored as they are.
- New ideas are appended, or inserted at the top when `prepend: true`.

### `ideas.update`

`{ id, patch }` → the updated `Idea`

`patch` fields (all optional):

| field | effect |
|---|---|
| `title` | non-empty string |
| `summary` | string |
| `status` | one of the statuses (synonyms such as `"in progress"`, `"wip"`, `"deferred"` are accepted) |
| `priority` | `low` / `medium` / `high` |
| `tags` | replaces the tags |
| `project` | rebind the idea: a project id or name, or `{ id, name? }` (a bare reference is resolved, the name snapshot refreshed); `null`, `""`, `"global"` or `"none"` unbinds it |
| `sections` | **replaces all sections**, in the given order. An entry whose `id` matches an existing section updates that section: `title`, `content` and `kind`, plus any extra fields the entry carries (`null` removes one). The section's other stored fields are kept. Entries without a known id become new sections. Use this for drag-reordering and inline editing. |
| `addSections` | `{ kind?, title?, content }[]`, appended |
| `updateSections` | `{ id, title?, content?, kind? }[]` |
| `removeSectionIds` | `string[]` |
| any other field | set to the given value. `null` **removes** the field. `id`, `createdAt`, `createdBy`, `updatedAt` and `sessionIds` are protected. |

`updatedAt` changes only when something actually changed.

### `ideas.delete`

`{ id }` → `true`

### `ideas.reorder`

`{ ids: string[] }` → `true`

The listed ideas come first, in the given order. Ideas that are not listed keep their relative order after them. Send
the full id list after a drag-and-drop (reordering a filtered view is safe: the unlisted ideas keep their order).

### `ideas.toPrompt`

`{ id }` → `string`: markdown for the composer (`ctx.app.insertText(text)`), for example:

```md
Implement the following idea from the ideas backlog (`idea-k3x9q2` in ~/.netpi/ideas.json).
Its sections contain earlier research, plans and decisions — use them. Keep the idea up to date with the ideas tool (action update): set the status to "in-progress" when you start and "done" when finished, and add a note section for anything important you learn.

# Cache model list
Priority: medium · Project: NetPI · Tags: perf

Avoid refetching /v1/models on every session switch.

## Research: Findings
models.list takes 800ms on AiProxy.

## Plan
1. Cache for 60s
```

The status does not change on its own. The agent is asked to update it.

### `ideas.quickAdd` (the `/idea` command)

`{ sessionId, args }` → `string` toast, for example `"Idea added (project NetPI): Cache model list (idea-k3x9q2)"` or
`"Idea added (global backlog): …"`. An empty `args` gives `bad_request` with `"Usage: /idea <title>"`.

### `ideas.recall` (the chip above the composer)

`{ sessionId, text }` → `{ match: { id, title, p } | null, reason, error?, ms }`: which open idea the first message of a
chat continues (docs/plans/2026-09-27-ideas-follow-the-session.md). An idea id in the text is the match (`reason:
"id"`, `p: 1`). Otherwise one decision through `decide.decision` (setting `ideas.model`, default `qwen3.8-27b`) over the
open ideas (not done or rejected) of the session's project and the global ones: title and summary as lettered options
plus "none", the list as the system prompt so repeated checks reuse NInfer's cache. A match needs p ≥
`ideas.recallThreshold` (0.8) and to beat "none" (`reason: "model"`). Otherwise `reason` is `none`, `short` (under 12
characters), `off` (`ideas.recall` is false), `unavailable` (no Decide plugin) or `error` (the decision failed or took
over 10 s; `error` holds the message, and the server log has it too).

### `ideas.attach`

`{ sessionId, id }` → `{ noticeId, ideaId }`: adds the idea to the chat as a notice (`meta.kind: "idea"`,
`meta.ideaId`) that starts *"The user added an idea from the ideas backlog to this chat"* and holds the idea's full
markdown (as `get` renders it). The session is added to the idea's `sessionIds`. Added before the first message, the
notice stays first; added during a run, the agent reads it at its next model call.

## The agent tool: `ideas` (category `ideas`)

One tool with an `action`, so a single schema goes with every request. It works on the single global file. New ideas
are stamped with the session's project by default; the `project` argument changes that (it is the agent's way to
reach other projects' backlogs, and `update` can move an idea between projects). Its prompt guideline: *"Record
research and plans that are deferred, out of scope or not feasible now in the ideas backlog (ideas, action add), and
look at the open ideas (action list) before larger work. When you finish the work an idea describes, set it to done
(action update)."*

| action | args | notes |
|---|---|---|
| `add` | `{ title, summary?, priority?, tags?, sections?: [{kind, title?, content}], project? }` | `createdBy: "agent:<id>"`. `project` (a project id/name, or `"global"`/`""` for unbound) overrides the session's project. |
| `list` | `{ status?, tag?, query?, project? }` | `project` selects the scope: no argument → the session's project **plus** the unbound "global" ideas (an unbound session sees only the unbound ones); `"all"` → every project (each line carries a project label); `"global"` → the unbound ones; a project id or name → that project only. Unknown projects give an error listing the known ones. Compact lines: `- idea-… [status · priority (· project)] Title — summary #tags (n sections)`. By default done and rejected ideas are hidden, with a count of how many were hidden. `status` also takes `active` and `all`, or a comma-separated list. `query` needs every word to appear in the title, summary, tags or sections. |
| `get` | `{ id }` | Full markdown (the meta line carries the project: `… · project: NetPI · …` or `… · project: global · …`). Section headings carry the section ids: `## Plan: Rollout [sec-4f0a]`, and an idea with sections ends with a line on how to change or add one (`updateSections` / `addSections`). |
| `update` | `{ id, title?, summary?, status?, priority?, tags?, project?, addSections?: [{kind, title?, content}], updateSections?: [{id, title?, content?, kind?}], removeSectionIds? }` (the section shapes are in the tool's schema, not only in its manual) | `project` (id/name, `"global"`, or `null`) rebinds/unbinds the idea. A `sections` argument is treated as `addSections`, and unknown fields are ignored. The session id is added to `sessionIds`. |

Deleting is left to the user (the tab): `delete` returns an error that suggests `done` or `rejected` instead. The action
is read leniently: synonyms (`create`, `show`, `edit`, `search`…), `close` / `done` / `complete` set the status to done,
and without an action the arguments decide (an id with changes: update; an id alone: get; a title: add; else list).
Every result has `details: { file, project?, idea? }`. Invalid input comes back as an `isError` result with a hint (for
example `Unknown project 'nope'. Known projects: NetPI, aiproxy.`).

## UI

- Header: the project filter (the active project's name by default, following the active project until the user picks
  another; *All projects*, *Global (unbound)*, and every known project), the file path and a **+** button.
- Filters: search over title, summary, tags and sections; the project filter above; a status menu (**Active** = open,
  planned, in-progress; **All**; or one status) with counts; and a **#** tag menu (multi-select). Selected tags show as
  removable chips below the filters.
- Cards: a project badge (folder + project name, a globe for the unbound "Global"), a status pill (with a
  click-to-cycle menu), priority, tags and the summary. Expanding a card shows the sections as rendered markdown, each
  with an edit button (`ideas.update` with `patch.sections` or `updateSections`).
- New idea: title, summary, priority, a project picker (default: the active project; *Global* for unbound; any other
  project) and tags.
- Actions: "Send to agent" (`ideas.toPrompt` → `ctx.app.insertText`), Delete (with a confirm) and drag to reorder
  (`ideas.reorder`).
- Refresh on the `ideas.changed` event (the single file, so always) and follow the active project on
  `ctx.app.onChange`.
