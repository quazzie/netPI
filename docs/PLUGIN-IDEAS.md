# Ideas plugin (`netpi.ideas`)

A backlog of ideas, research, plans and deferred work for the user and for agents. Agents work with it through the
`ideas` tool. The user works with it in the **Ideas** tab (right panel) and through `/idea <title>`.

- Plugin: `plugins/NetPI.Ideas`, id `netpi.ideas`, start order 80.
- Tab: `{ id: "ideas", title: "Ideas", panel: "right", icon: "idea", order: 20, module: "ui.js" }`. The UI module goes in
  `plugins/NetPI.Ideas/wwwroot/ui.js` (source in `plugins/NetPI.Ideas/ui/`) and is served at `/plugins/netpi.ideas/ui.js`.
- Slash command: `{ name: "idea", argsHint: "<title>", rpc: "ideas.quickAdd" }`.
- Setting: `ideas.fileName` (default `"ideas.json"`; only the file name part is used; in a project it lives in `.netpi/`).

## Where ideas are stored

| Situation | File | `scope` |
|---|---|---|
| RPC with `projectId` | `<project.path>/.netpi/ideas.json` | `"project"` |
| RPC with `sessionId` of a session that has a project | `<project.path>/.netpi/ideas.json` | `"project"` |
| RPC with `sessionId` of a session without a project, or no ids | `~/.netpi/ideas.json` (`NetPiPaths.Home`) | `"global"` |
| Agent tool | the call's project (`ToolContext.Project`, else the session's project), else the global file | – |

`projectId` wins over `sessionId`. An unknown `projectId`/`sessionId` gives an RPC error `not_found`. Writing to a project
whose folder no longer exists also gives `not_found`. The `.netpi` folder (like `.netpi/skills`) and the global file's
folder are created when needed. Earlier versions kept the file in the project folder itself: the first time a project's
ideas are used, a `<project.path>/ideas.json` moves into `.netpi/` (unless a `.netpi/ideas.json` exists already; then the
old file stays where it is).

## File format

```jsonc
{
  "version": 1,
  "ideas": [
    {
      "id": "idea-k3x9q2",                 // "idea-" + 6 chars [0-9a-z]
      "title": "Cache model list",
      "summary": "Avoid refetching /v1/models on every session switch.",
      "status": "open",                    // open | parked | planned | in-progress | done | rejected
      "priority": "medium",                // low | medium | high
      "tags": ["perf"],
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

The event fires after any change to a watched file, whether it came from this plugin (tools or RPC) or from an external
editor. Changes are debounced by 250 ms, so a burst of writes produces one event. A file is watched from the first time
any tool or RPC touches it, and the global file is watched from startup. At most 32 files are watched.

**UI:** on `ideas.changed`, if `data.file === currentList.file`, call `ideas.list` again. Also re-list when the active
session or project changes (`ctx.app.onChange`).

## RPC methods

Every method accepts the location parameters `sessionId?` and `projectId?` (see the table above). Pass the active
session id, or the active project id when there is no session. Results are plain JSON: ideas are returned exactly as
stored, including unknown fields.

Error codes: `bad_request` (invalid input: the message says what is wrong, for example the list of valid statuses),
`not_found` (idea, session, project or project folder), `invalid_file`, `io_error`.

### `ideas.list`

`{ sessionId?, projectId? }` →

```ts
{
  file: string;                 // absolute path of the ideas file
  fileName: string;             // "ideas.json"
  scope: 'project' | 'global';
  projectId?: string;           // only for scope "project"
  projectName?: string;         // only for scope "project"
  exists: boolean;              // false until the first idea is written
  ideas: Idea[];                // file order (= the user's manual order)
}
```

No filtering happens on the server. The tab filters and searches client-side.

### `ideas.get`

`{ sessionId?, projectId?, id }` → `Idea`. The id is matched exactly, then case-insensitively, then with an `idea-` prefix
added.

### `ideas.add`

`{ sessionId?, projectId?, idea: { title, summary?, status?, priority?, tags?, sections?, …extra }, prepend?: boolean }` → the created `Idea`

- `title` is required. Defaults: `status: "open"`, `priority: "medium"`, `tags: []`, `sections: []`.
- `tags` may be an array or a comma-separated string. A leading `#` is stripped and duplicates are removed
  (case-insensitive).
- `sections` is `{ kind?, title?, content }[]`. Ids and `updatedAt` are generated.
- `createdBy` is `"user"`. `sessionIds` is `[sessionId]` when a sessionId was passed.
- Extra fields of `idea` (for example `color`) are stored as they are.
- New ideas are appended, or inserted at the top when `prepend: true`.

### `ideas.update`

`{ sessionId?, projectId?, id, patch }` → the updated `Idea`

`patch` fields (all optional):

| field | effect |
|---|---|
| `title` | non-empty string |
| `summary` | string |
| `status` | one of the statuses (synonyms such as `"in progress"`, `"wip"`, `"deferred"` are accepted) |
| `priority` | `low` / `medium` / `high` |
| `tags` | replaces the tags |
| `sections` | **replaces all sections**, in the given order. An entry whose `id` matches an existing section updates that section: `title`, `content` and `kind`, plus any extra fields the entry carries (`null` removes one). The section's other stored fields are kept. Entries without a known id become new sections. Use this for drag-reordering and inline editing. |
| `addSections` | `{ kind?, title?, content }[]`, appended |
| `updateSections` | `{ id, title?, content?, kind? }[]` |
| `removeSectionIds` | `string[]` |
| any other field | set to the given value. `null` **removes** the field. `id`, `createdAt`, `createdBy`, `updatedAt` and `sessionIds` are protected. |

`updatedAt` changes only when something actually changed.

### `ideas.delete`

`{ sessionId?, projectId?, id }` → `true`

### `ideas.reorder`

`{ sessionId?, projectId?, ids: string[] }` → `true`

The listed ideas come first, in the given order. Ideas that are not listed keep their relative order after them. Send the
full id list after a drag-and-drop.

### `ideas.toPrompt`

`{ sessionId?, projectId?, id }` → `string`: markdown for the composer (`ctx.app.insertText(text)`), for example:

```md
Implement the following idea from the ideas backlog (`idea-k3x9q2` in .netpi/ideas.json).
Its sections contain earlier research, plans and decisions — use them. Keep the idea up to date with the ideas tool (action update): set the status to "in-progress" when you start and "done" when finished, and add a note section for anything important you learn.

# Cache model list
Priority: medium · Tags: perf

Avoid refetching /v1/models on every session switch.

## Research: Findings
models.list takes 800ms on AiProxy.

## Plan
1. Cache for 60s
```

The status does not change on its own. The agent is asked to update it.

### `ideas.quickAdd` (the `/idea` command)

`{ sessionId, args }` → `string` toast, for example `"Idea added (project Demo): Cache model list (idea-k3x9q2)"`. An
empty `args` gives `bad_request` with `"Usage: /idea <title>"`.

## The agent tool: `ideas` (category `ideas`)

One tool with an `action`, so a single schema goes with every request. Its prompt guideline: *"Record research and
plans that are deferred, out of scope or not feasible now in the ideas backlog (ideas, action add), and look at the open
ideas (action list) before larger work. When you finish the work an idea describes, set it to done (action update)."*

| action | args | notes |
|---|---|---|
| `add` | `{ title, summary?, priority?, tags?, sections?: [{kind, title?, content}] }` | `createdBy: "agent:<id>"` |
| `list` | `{ status?, tag?, query? }` | Compact lines: `- idea-… [status · priority] Title — summary #tags (n sections)`. By default done and rejected ideas are hidden, with a count of how many were hidden. `status` also takes `active` and `all`, or a comma-separated list. `query` needs every word to appear in the title, summary, tags or sections. |
| `get` | `{ id }` | Full markdown. Section headings carry the section ids: `## Plan: Rollout [sec-4f0a]`. |
| `update` | `{ id, title?, summary?, status?, priority?, tags?, addSections?, updateSections?, removeSectionIds? }` | A `sections` argument is treated as `addSections`, and unknown fields are ignored. The session id is added to `sessionIds`. |

Deleting is left to the user (the tab): `delete` returns an error that suggests `done` or `rejected` instead. The action
is read leniently: synonyms (`create`, `show`, `edit`, `search`…), `close` / `done` / `complete` set the status to done,
and without an action the arguments decide (an id with changes: update; an id alone: get; a title: add; else list).
Every result has `details: { file, scope, idea? }`. Invalid input comes back as an `isError` result with a hint (for
example `The list action shows the ids.`).

## UI suggestions

- Header: the scope badge (the project name or "Global"), then filter chips for status and tag, a search box, and a
  "+" button (`ideas.add`).
- Cards: title, a status pill (with a click-to-cycle menu), priority, tags and the summary. Expanding a card shows the
  sections as rendered markdown, each with an edit button (`ideas.update` with `patch.sections` or `updateSections`).
- Actions: "Send to agent" (`ideas.toPrompt` → `ctx.app.insertText`), Delete (with a confirm) and drag to reorder
  (`ideas.reorder`).
- Refresh on the `ideas.changed` event and on active session/project changes.
