# Ideas plugin (`netpi.ideas`)

Agent rework update (2026-10-01): automatic save/completion proposals require a separate low-priority verifier. Work and `ideas.work` expose waiting, dropped, rejected and verified outcomes with reasons. Completion evidence includes bounded complete patches; revision and project activity are checked again before writing. `ideas.applyVerifiedUpdates` defaults true, applying verified unchanged completion without a card; false retains a verified review card. `ideas.verifyUpdate` checks explicit proposed patches against supplied evidence and rejects stale revisions. Basic backlog CRUD remains independent of optional model/decision/history capabilities. See SETTINGS.md and PROTOCOL.md for the new contracts.

A backlog of ideas, research, plans and deferred work for the user and for agents. Agents work with it through the
`ideas` tool. The user works with it in the **Ideas** tab (right panel) and through `/idea <title>`.

- Plugin: `plugins/NetPI.Ideas`, id `netpi.ideas`, start order 80.
- Tab: `{ id: "ideas", title: "Ideas", panel: "right", icon: "idea", order: 20, module: "ui.js" }`. The UI module goes in
  `plugins/NetPI.Ideas/wwwroot/ui.js` (source in `plugins/NetPI.Ideas/ui/`) and is served at `/plugins/netpi.ideas/ui.js`.
- Slash command: `{ name: "idea", argsHint: "<title>", rpc: "ideas.quickAdd" }`.
- Settings: `ideas.fileName` (default `"ideas.json"`; the name the JSON backlog had — read once by the cutover and the
  default name an export is written under, no longer a live file), `ideas.recall`, `ideas.recallThreshold`,
  `ideas.saveCheck`, `ideas.attachThreshold`, `ideas.model`, `ideas.allowPaidModel`, `ideas.checkWaitSeconds`,
  `ideas.closeOnCommit`,
  `ideas.linkThreshold`, `ideas.doneThreshold`, `ideas.tellAgentOnCommit`, `ideas.commitNoticesPerRun`
  (docs/SETTINGS.md).

## Where ideas are stored

**In NetPI's own database.** The backlog is plugin-owned SQLite tables in `<home>/netpi.db`, in the migration scope
`netpi.ideas` (storage version 1), reached through `ctx.Db` and one read/write entry point (`IdeasRepository`). There
is no ideas file: the JSON files of the earlier versions are read once by the cutover and are never written again.

| Table | Holds |
|---|---|
| `ideas_items` | one row per idea: its id, `ord` (the order the user arranged), `revision`, the columns the queries read (title, status, priority, project, created/updated) and `doc` — the complete idea JSON |
| `ideas_suggestions` | the cards waiting for an answer |
| `ideas_resolutions` | how a card was answered, kept after the card is gone |
| `ideas_checks` | the per-conversation check marks, with the claim token and expiry of a running check |
| `ideas_repos` | the last commit read per repository |
| `ideas_imports` | the receipts of the imports (the cutover writes one per source) |
| `ideas_metadata` | the backend marker, and the root-level fields the JSON file had |

Every idea carries a **`project`** property — a `{ id, name }` snapshot of the project it belongs to, or absent when it
is not bound to a project (the "global" ideas):

| Situation | `project` on the new idea |
|---|---|
| Agent tool, session with a project | the session's project |
| Agent tool, session without a project | absent (unbound) |
| Agent tool with a `project` argument | that project (`"global"`/`"none"` → unbound) |
| RPC `ideas.add` with `projectId` | that project (a project id or name, `"global"` → unbound) |
| RPC `ideas.add` with `sessionId` only | the session's project (absent when the session has none) |

`projectId` wins over `sessionId`. An unknown `projectId`, or an unknown `sessionId` in `ideas.add`/`ideas.quickAdd`,
gives an RPC error `not_found`.

The columns are projections of `doc` written in the same statement, so the two cannot drift apart; and an idea is still
only ever understood as the document it always was — sections, tags, chats, commits and every field another tool wrote
by hand live in `doc`, which is why a save never drops anything. Every repository method is short and synchronous: the
host serializes one connection, so a transaction is held for the length of a few statements and never across a model
call, a file read or anything else that can block.

### The one-time cutover

The earlier versions kept the backlog in `<home>/<ideas.fileName>` with a `ideas-pending.json` beside it, and before
that one file per project (`<project.path>/.netpi/ideas.json`, and before that `<project.path>/ideas.json`). Those files
are read once, at the plugin start, and only when at least one of them exists:

- **Every source is read**: the global backlog, `ideas-pending.json`, the per-project receipt `ideas-migration.json`
  and the per-project files of every project in the session store. A source the old build's own receipt says it had
  already merged is recorded, not merged a second time. Reading a legacy file still takes the OS lock the old build
  used, so an import never sees half of a write by a plugin that is still running.
- **A malformed source fails the whole import** (`invalid_file`, naming the file and what is wrong with it): an empty
  backlog is never substituted for ideas that could not be read, and the files are left exactly as they were. A source
  that is merely *missing* is not a problem — that is what an unused optional file looks like.
- **Everything is written in ONE transaction**, ending with the receipts and the durable backend marker: the ideas (with
  their ids, their order and every field they carry, stamped with the project a per-project file did not have, and given
  a new id on a collision rather than merged into somebody's idea), the cards, the check marks, the commit cursors, the
  root-level fields of the file, the answers that were in flight when NetPI stopped, and the record that accounts for
  it all. A failure before that commit leaves the old files authoritative and the backlog empty; a failure after it
  leaves SQLite authoritative, whatever housekeeping failed afterwards.
- **Then the originals are kept**: every source is copied byte for byte into `<home>/ideas-archive/<timestamp>/` — a
  `manifest.json` with each file's sha256, and the file itself under `originals/` — and moved out of the place the old
  build read it from. An older NetPI started afterwards therefore starts with an empty backlog instead of quietly
  writing JSON that nothing reads. Nothing is deleted.
- **Restarting does not import again**: the marker in `ideas_metadata` says the backend is SQLite. A file that
  reappears afterwards is reported as `orphanedSources` by `ideas.migration`, and is only taken in on purpose, with
  `ideas.migrate { confirm: true, force: true }` — never merged silently into a backlog that has moved on.

## The idea document

One idea is one JSON object — the `doc` column of its `ideas_items` row, and what an import reads and an export writes:

```jsonc
{
  "id": "idea-k3x9q2",                 // "idea-" + 6 chars [0-9a-z], unique in the backlog
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
  "sessionIds": ["ses_…"],            // sessions that created or updated the idea
  "sessions": [                        // sessions that worked on the idea (phase 2, below)
    { "sessionId": "ses_…", "title": "…", "at": "2026-09-28T10:00:00Z", "seq": 42, "note": "…", "seen": false }
  ],
  "commits": [                         // commits recorded on the idea (phase 3, below)
    { "hash": "…", "short": "abc1234", "subject": "…", "at": "2026-09-28T11:00:00Z" }
  ]
}
```

A snapshot export wraps that in a versioned document (`{ "version": 1, "format": "netpi.ideas.export", "ideas": [ … ],
"cards": [ … ], … }`), and an export is not a live file: editing it changes nothing in the backlog, and nothing is
re-imported because a file changed.

This is the `doc` of an `ideas_items` row, and it is the format an import reads and an export writes — a document this
build does not fully understand can be carried through it. `revision` is **not** in it: the revision is a column, and
it is added to the copy a caller reads (so the document that is stored and the document an editor submits back are the
same thing without a field of its own).

- Section `kind`: `note | research | plan | requirements | design | decision | blocker | links | todo`. Common synonyms
  are mapped (`spec` → `requirements`, `tasks` → `todo`, …). Anything else becomes `note`. `title` is optional.
- Idea ids are unique within the backlog, and section ids are unique within their idea. A legacy id that is not of our
  making is stored and found exactly as it is; only an id that *collides* with an idea already here is given a new one.
- The database is the source of truth, and there is no second copy of it in memory to go stale: a patch is read,
  validated and applied to a detached copy of the current row **inside the transaction that writes it**. A patch that
  fails validation (an empty title, an unknown status) therefore leaves nothing behind — not in the database, and not in
  the copy a later write starts from.
- A write is a transaction: either the whole change is stored or none of it is, and a patch that changes nothing is not
  written at all (no new `revision`, no event). A hot reload is a swap, so two instances of this plugin are briefly live
  against the same tables; the database serializes them, which is why there is no lock file and no watcher any more.
- What a save preserves: **unknown fields at every level of the idea** (the idea, its sections, a card, an imported
  state). The root-level fields of the old JSON file are kept whole in `ideas_metadata` and come back in an export's
  `root`; the import does not have to understand them to preserve them.
- `//` comments and trailing commas are accepted when a legacy file is read and not carried into the database. An
  exported file is plain JSON (LF, two-space indent).

## Event

| type | scoped | data |
|---|---|---|
| `ideas.changed` | no (broadcast) | the `ideas.list` `storage` descriptor — `{ backend: "sqlite", database, scope, schemaVersion, editableFile, importExport }` — plus `file` (the legacy name, kept for older listeners) and `reason?` — which write it was (`add`, `update`, `answer`, …) |
| `ideas.suggested` | no (broadcast) | `{ suggestion }`: a card a closed chat left waiting. Fires when the save check makes one; the cards also survive a restart, so a window reads them with `ideas.suggestions` on start |

The event fires after every write that committed, whether it came from a tool, from an RPC or from another window, and
never for one that rolled back. It is a **notification, not exactly-once delivery**: a window that missed one (it was
closed, or it was reconnecting) gets the same state from the next read, and a reconnecting window re-reads canonical
state rather than trusting what it kept.

**UI:** on `ideas.changed`, call `ideas.list` again — it is the canonical read, so a missed event costs a refresh, not
a stale view.

## RPC methods

There is one backlog, so the location parameters of earlier versions are gone: `ideas.list` takes no parameters, and
the other methods are addressed by `id` alone (ids are unique in the backlog). `ideas.add` keeps `sessionId`/`projectId`
to decide the new idea's `project` stamp. Results are plain JSON: an idea is returned exactly as it is stored, including
unknown fields, plus the `revision` it is at.

Every idea carries an integer **`revision`**, raised by every stored change. `ideas.list`, `ideas.get`, `ideas.add` and
`ideas.update` all return it, and `ideas.update` takes it back as `expectedRevision` — that is how a stale window is
turned into a `conflict` instead of a silent overwrite.

Error codes: `bad_request` (invalid input: the message says what is wrong, for example the list of valid statuses or
projects), `not_found` (idea, session or project — also a card that is gone), `conflict` (an edit whose
`expectedRevision`/`expectedUpdatedAt` is stale, an id that is already in the backlog, or a cutover that already
happened), `invalid_file` (a legacy file we cannot read or do not understand — it is left alone, never replaced),
`io_error` (a file write that could not complete, an export for example).

### `ideas.list`

`{ }` →

```ts
{
  storage: {
    backend: 'sqlite';          // there is no editable file behind the backlog
    database: string;           // "netpi.db"
    scope: 'netpi.ideas';
    schemaVersion: 1;
    editableFile: false;
    importExport: 'json';
  };
  ideas: Idea[];                // the user's manual order
  file: string;                 // deprecated: where the legacy file is, not a live file
  fileName: string;             // deprecated: the legacy name ("ideas.json")
  exists: boolean;              // deprecated: true once the backlog holds an idea
}
```

`file`, `fileName` and `exists` are kept only so an older UI has something to show; `storage` is the answer now, and
nothing in the plugin writes the file they name. No filtering happens on the server. The tab filters by project, status
and tag client-side.

### `ideas.get`

`{ id }` → `Idea` (with its `revision`). The id is matched exactly, then case-insensitively, then with an `idea-`
prefix added.

### `ideas.add`

`{ sessionId?, projectId?, idea: { title, summary?, status?, priority?, tags?, sections?, …extra }, prepend?: boolean }` → the created `Idea` (at `revision: 1`)

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

`{ id, patch, expectedRevision?, expectedUpdatedAt? }` → the updated `Idea` (at the next `revision`)

`patch` fields (all optional):

| field | effect |
|---|---|
| `title` | non-empty string |
| `summary` | string |
| `status` | one of the statuses (synonyms such as `"in progress"`, `"wip"`, `"deferred"` are accepted) |
| `priority` | `low` / `medium` / `high` |
| `tags` | replaces the tags |
| `project` | rebind the idea: a project id or name, or `{ id, name? }` (a bare reference is resolved, the name snapshot refreshed); `null`, `""`, `"global"` or `"none"` unbinds it |
| `expectedRevision` | (a parameter, not a patch field) the revision the editor read: the update is refused with `conflict` when the idea is at a newer one, so a stale window or a card left open cannot silently overwrite newer content |
| `expectedUpdatedAt` | (a parameter, deprecated) the older form of the same check, still accepted for callers that only have the timestamp. It compares a second-precision value, so two changes in the same second are not a conflict — send `expectedRevision` wherever you can |
| `sections` | **replaces all sections**, in the given order. An entry whose `id` matches an existing section updates that section: `title`, `content` and `kind`, plus any extra fields the entry carries (`null` removes one). The section's other stored fields are kept. Entries without a known id become new sections. Use this for drag-reordering and inline editing. |
| `addSections` | `{ kind?, title?, content }[]`, appended |
| `updateSections` | `{ id, title?, content?, kind? }[]` |
| `removeSectionIds` | `string[]` |
| any other field | set to the given value. `null` **removes** the field. `id`, `createdAt`, `createdBy`, `updatedAt` and `sessionIds` are protected. |

`updatedAt` changes only when something actually changed, and so does `revision`: a patch that changes nothing is
neither written nor announced.

### `ideas.delete`

`{ id }` → `true`

### `ideas.reorder`

`{ ids: string[] }` → `true`

The listed ideas come first, in the given order. Ideas that are not listed keep their relative order after them. Send
the full id list after a drag-and-drop (reordering a filtered view is safe: the unlisted ideas keep their order).

### `ideas.toPrompt`

`{ id }` → `string`: markdown for the composer (`ctx.app.insertText(text)`), for example:

```md
Implement the following idea from the ideas backlog (`idea-k3x9q2` in the ideas backlog).
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

**The card's "Send to chat" does not use it** (idea-b7himr): it stages a pointer in the composer —
`Work on idea <id> (<title>) — read it, then tell me what you plan to do.` — and the agent reads the idea itself
with `ideas.get`, so the text is not spent twice (once in the prompt, once in the tool result) and cannot go stale.
`ideas.toPrompt` is one menu item away on the card ("Insert the full text") for pasting the whole thing.

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

### `ideas.closed` (save on tab close)

`{ sessionId }` → `{ checked: bool, reason }`. The UI calls it from `closeTab`, fire and forget: the tab closes at once
and the check runs behind it. `reason` is `started`, `running` (the tab was closed while the agent was still working;
the check waits for the run), `already` (this conversation revision was claimed and is running or finished), or one of
the skips: `no_session`, `subagent`, `short` (fewer than two user turns), `unfinished` (the last turn ended `aborted`,
`error`, `length` or `content_filter`), `off` (`ideas.saveCheck` is false).

Whether the check may run is recorded in `ideas_checks`, per **conversation revision** (the messages the chat has now),
with a state: `running` is a claim in flight — it carries a token and an expiry, so a second close of an unchanged chat
sees the claim instead of starting a second check, and a claim nobody owns any more (NetPI was stopped mid-check)
becomes retryable at the next start. `done` is the only permanent state, and `failed` — with the reason — may be tried
again a few times. A check that *ran* and found nothing is `done`, not failed: "nothing worth keeping" is an answer. An
old aborted turn does not exclude the chat, and a check whose claim was taken over cannot report over the newer
outcome.

The runtime's running, queued and yielded states also hold the check, even if the transcript already has a final
assistant message. After twenty 30-second waits a still-active run leaves a retryable failed check; it is never
judged mid-run. Before offering a save card, the check suppresses a draft with the same title already saved by that
session.

What runs in the background, in this order:

1. **Attach.** One pick-one decision over the open ideas of the chat's project plus the global ones, the conversation
   digest as the state. At p ≥ `ideas.attachThreshold` (0.8) and beating "none", a `sessions` entry is added to that
   idea — `{ sessionId, title, at, seq?, seen: false }`, kept apart from the sections, so the idea's text never changes.
   One entry per session.
2. **Save check.** One generative call through `ideas.model` at the model's cheapest reasoning effort it has (never a
   step up, and the model's own default when it has none), which answers `NOTHING`, or `SAVE` with a title and a
   summary. The conversation is a digest (user turns whole, answers and tool names clipped, 12k characters), not the
   raw transcript — and a long chat keeps its **beginning and its ending**, because a check that only sees the
   discussion mistakes an implemented or cancelled plan for an unsaved one. Measured 8/11 caught, 1/32 false
   (docs/DECISION-MODELS.md).

Both calls wait for a slot on the backend the chats use (through the agents' scheduler, behind them), and neither runs
on a paid model unless `ideas.allowPaidModel` says so. A backlog larger than the 51 letters a decision can offer is
ranked by what the conversation shares with each idea and asked about in bounded windows, so later ideas stay eligible.
   A `SAVE` becomes a card in `ideas_suggestions` and the `ideas.suggested` event fires.

**Admission is a bound, not a courtesy.** A check that cannot get a slot within `ideas.checkWaitSeconds` (30) is
**dropped**, never run without one — running it anyway is what let a commit sweep, a save check and a recall put three
calls on a two-slot model. A drop is not silent and it is not lost:

| Dropped work | What happens | Where you see it |
|---|---|---|
| save check | the check's mark stays `failed` (retryable) with the reason on it, so the next close of that conversation runs it again | `ideas.suggestions` / the checks RPC, and the log |
| commit sweep (`close on commit`) | the cursor does not move past the commit, so the next sweep reads it again | the log, and the commit is offered later |
| recall | no suggestion for that keystroke — the composer moves on | nothing; the answer was for a keystroke |

The recall is the one interactive caller and waits 2 s, not `ideas.checkWaitSeconds`: a longer wait there is a spinner in
the composer, not a card. A paid model is never called on a check's own initiative whatever the queue does — that is
`ideas.allowPaidModel`, and it is a skip rather than a drop, because a retry would skip it again.

Nothing reaches the backlog without a click, and a card is never an idea: it is an offer, and it stays one until it is
answered.

### `ideas.suggestions` and `ideas.resolve`

`ideas.suggestions` takes no arguments → `{ suggestions: [{ id, kind, … }] }`, oldest first. It is a plain read:
listing the cards writes nothing. Two kinds: `save` (a plan a closed chat left unsaved — `{ kind: "save", sessionId,
sessionTitle, title, summary, at, project }`) and `done` (an idea a commit may have finished — `{ kind: "done", ideaId,
ideaRevision, title, commits: string[], at, project }`). The cards are rows in `ideas_suggestions`; the check marks are in
`ideas_checks` (kept 30 days) and the last commit read per repository in `ideas_repos` (kept 60 days). The same chat and
the same plan make one card however often the check runs, and a `done` card is one per idea.

`ideas.resolve { id, action: "save" | "done" | "discard", edit?: { title?, summary? } }` → `{ saved: idea | null,
discarded, action, alreadyResolved }`.
`save` writes the idea (stamped with the card's project, its own `sessions` entry for the chat it came from, and the
user's `edit` when given); `done` marks the existing idea the card named as `done` and leaves its `commits` where they
are; `discard` drops the card. The action is checked against the card's kind first, so answering a `save` card with
`done` is a `bad_request` and the card stays.

**Answering a card is one transaction**: check the action against the card, write the idea (or mark it done), record the
resolution in `ideas_resolutions` and take the card out — all of it or none of it. A second window answering the same
card, or the same call retried after a timeout, reads the recorded answer back with `alreadyResolved: true` and the idea
it produced, instead of saving a second idea. A card that is gone and has no recorded answer is `not_found`. There is **no
answer journal any more**: the resolution row is the journal, and it is in the same transaction as its effect, so
nothing has to be replayed at the next start. `ideas.resolved { id, action, card }` tells every other window to drop the
card. The UI shows the cards above the composer and as a "waiting for you" line in the Ideas tab; both re-read them
with `ideas.suggestions` on reconnect and when they become visible.

### `ideas.migration`, `ideas.migrate`, `ideas.export`, `ideas.import`

The cutover runs by itself at the plugin start when there is a legacy file to read. These four are for looking at that,
doing it on purpose, and moving the backlog between machines.

- **`ideas.migration` `{ }`** → `{ backend: "sqlite", storage, cutover?, sources: [...], imports: [...],
  orphanedSources? }`. Read-only, and it writes nothing: every resolved source path with its sha256 and its counts
  (ideas, cards, check marks, cursors, answers in flight), the duplicate or unnamed ids of a source, a source that could
  not be parsed (`error`, and the file is not touched), the receipts already written, the backend marker, and — after
  the cutover — a legacy file that reappeared afterwards (`orphanedSources`).
- **`ideas.migrate` `{ confirm: true, force? }`** → `{ backend, cutover, counts, diagnostics, archive }`. Deliberate:
  without `confirm: true` it is a `bad_request`, because an automatic import would be the one operation that must never
  happen because nobody asked. It refuses with `conflict` when the cutover already happened — unless `force: true`,
  which is how a file an older build wrote afterwards is taken in (merged into what is here, not over it). Everything is
  written in one transaction or not at all; the archive follows the commit, and a housekeeping failure does not unmake
  it.
- **`ideas.export` `{ path?, json? }`** → `{ json, file? }`: a portable snapshot — `format: "netpi.ideas.export"`,
  `version`, `exportedAt`, the ideas with their ids and the user's order, the cards, the answers already given, the
  check marks, the repository cursors, and the preserved root-level fields under `root`. With a `path` it is written
  there too and the absolute path comes back; without one, nothing touches the disk.
- **`ideas.import` `{ json | path, mode?: "merge" (default) | "replace" | "validate" }`** → `{ ideas, cards, conflicts,
  unknownFields, imported?, mode }`. `validate` is the preview: the counts, the ids that are already here (a conflict),
  and whatever in the document this build does not understand. `merge` adds what is not here yet and never overwrites
  what is; `replace` empties the backlog and the cards first, which is why it has to be named. This is the deliberate
  transfer, separate from the cutover: an exported file is never re-read because it changed on disk.

### Close on commit (phase 3)

Every project with a git repository is watched (the git directories the Files plugin resolves — in a worktree `.git` is
a *file* — debounced 250 ms, and swept every two minutes whether or not a watcher could be created). The last commit
read per repository is kept in `ideas_repos`; a repository is anchored at HEAD only when nothing is remembered
for it, so commits made while NetPI was closed are read rather than skipped. The commits themselves are read through the
Files plugin's `files.commits` — a plugin cannot run `git`.

Unseen history is read in bounded pages **oldest first** (`since..until`, then the pages in the opposite order, because
git answers a range with its newest commits), so a burst of more than 20 is read whole: none skipped, none twice. The
cursor moves past a commit only when it was handled — a decision that failed leaves it unread and the next sweep starts
there — and a history that was rewritten under the cursor re-anchors at HEAD with a line in the log.

A project with a running, queued or yielded agent defers its sweep without moving the cursor. Run completion triggers
another sweep, which reads the current open ideas so an agent can finish its own backlog updates first. A done
judgment carries the revision it actually read: if the idea changes or work resumes while the decision is in flight,
the result is discarded and the commit stays unread for a later sweep.

Each new commit is read twice over, in the order that measured best (docs/DECISION-MODELS.md):

1. **Which idea is it about?** A commit message that names an idea id is the match with no model at all. Otherwise one
   pick-one decision over the ideas open at that moment, linking *every* option at p ≥ `ideas.linkThreshold` (0.7 linked
   no wrong idea in 187 commits) — a commit can finish two ideas, so it is not forced to pick one. The commit is recorded
   on the idea as a `commits` entry `{ hash, short, subject, at }`, beside the idea's text, never inside it, and the row
   shows a mark.
2. **Is the idea finished?** Asked with the idea's full text (its plan and what is left) **and all its linked commits**,
   because asking from one commit and the summary alone offered only 5/12. At p ≥ `ideas.doneThreshold` (0.8) and beating
   "MORE", a `done` card asks the user — one per idea, never an action. A commit that only advances an idea is recorded
   and nothing is offered.

Skips: no repository, `ideas.closeOnCommit` off, no open idea in the project, no Decide plugin. Without the Files plugin
there is nothing to read and the check does nothing at all.

### Tell the run that committed (phase 4)

The check above is the right shape for a commit made in a terminal: no conversation is watching it, so the model is asked
twice and the **user** gets a card. A commit the agent made itself is the opposite case — the run that wrote it knows what
it was for and is usually still going. So `IdeaCommitNoticeHook` (an `IAgentHook`, order 260) watches the run's own tool
calls and injects **one notice** (kind `git-commit`) before the next model call:

> A commit just landed in NetPI (the git command you ran succeeded). Open ideas of that project: "…", "…". If this
> commit finishes one of them, update that idea now with the ideas tool: mark it done, or leave it open and add a short
> section saying what landed. If none of them is about this commit, say so in one line and do not create an idea for it.

It is advice, not an action, exactly like a nudge: the agent decides, the user reads what it did. What it takes to get
there, all of it a reason to stay silent instead:

- the tool call is `bash`/`pwsh` and its `command` runs `commit` or `merge` (token by token, so `git -C dir commit` and
  `git add -A; git commit` are seen, and `git log`/`git push` are not), with none of `--dry-run`, `--abort`, `--quit`,
  `--no-commit` after it;
- the result is not an error and its exit code is 0 (a JSON null exit code is a background command still running);
- the working directory (the call's `cwd`, else the run's) is inside the session's **project**, and the run has a project
  and the `ideas` tool;
- the project has at least one open idea (so a project without a backlog never pays an extra model call);
- the run has not had `ideas.commitNoticesPerRun` (2) of them yet. Two commits inside one model call are one notice.

The card flow is unchanged and still needed: a commit the agent closed is no longer open, so it is not offered twice,
and a commit nobody made in a chat is exactly what the watcher is for. Skips: `ideas.tellAgentOnCommit` off.

## The agent tool: `ideas` (category `ideas`)

One tool with an `action`, so a single schema goes with every request. It works on the one backlog in the database.
New ideas are stamped with the session's project by default; the `project` argument changes that (it is the agent's way
to reach other projects' backlogs, and `update` can move an idea between projects). Its prompt guideline: *"Record
research and plans that are deferred, out of scope or not feasible now in the ideas backlog (ideas, action add), and
look at the open ideas (action list) before larger work. When you finish the work an idea describes, set it to done
(action update). Title the idea so it makes sense on its own on one line: the user scans titles, not summaries."* The
help it is asked for on demand starts the same way: the backlog is *"kept in NetPI's own database"*, and deleting is
up to the user.

| action | args | notes |
|---|---|---|
| `add` | `{ title, summary?, priority?, tags?, sections?: [{kind, title?, content}], project? }` | `createdBy: "agent:<id>"`. `project` (a project id/name, or `"global"`/`""` for unbound) overrides the session's project. |
| `list` | `{ status?, tag?, query?, project? }` | `project` selects the scope: no argument → the session's project **plus** the unbound "global" ideas (an unbound session sees only the unbound ones); `"all"` → every project (each line carries a project label); `"global"` → the unbound ones; a project id or name → that project only. Unknown projects give an error listing the known ones. Compact lines: `- idea-… [status · priority (· project)] Title — summary #tags (n sections)`. By default done and rejected ideas are hidden, with a count of how many were hidden. `status` also takes `active` and `all`, or a comma-separated list. `query` needs every word to appear in the title, summary, tags or sections. |
| `get` | `{ id }` | Full markdown (the meta line carries the project: `… · project: NetPI · …` or `… · project: global · …`). Section headings carry the section ids: `## Plan: Rollout [sec-4f0a]`, and an idea with sections ends with a line on how to change or add one (`updateSections` / `addSections`). |
| `update` | `{ id, title?, summary?, status?, priority?, tags?, project?, addSections?: [{kind, title?, content}], updateSections?: [{id, title?, content?, kind?}], removeSectionIds? }` (the section shapes are in the tool's schema, not only in its manual) | `project` (id/name, `"global"`, or `null`) rebinds/unbinds the idea. A `sections` argument is treated as `addSections`, and unknown fields are ignored. The session id is added to `sessionIds`. |

Deleting is left to the user (the tab): `delete` returns an error that suggests `done` or `rejected` instead. There is
no parent or merge field, so consolidating ideas means folding each one's text into **sections** of one keeper idea
(`addSections`, the absorbed idea named in the section title so its id still finds its content) and setting the
absorbed ones to `done` with a short "Merged into idea-…" note; the backlog keeps one flat list. The action
is read leniently: synonyms (`create`, `show`, `edit`, `search`…), `close` / `done` / `complete` set the status to done,
and without an action the arguments decide (an id with changes: update; an id alone: get; a title: add; else list).
Every result has `details: { project, idea }`; the `idea` is the whole document, `revision` included. A tool update
sends no `expectedRevision`, so the agent's write is last-writer-wins by design: a chat that means to be careful reads
the idea (`get`) and works from what it read. Invalid input comes back as an `isError` result with a hint (for example
`Unknown project 'nope'. Known projects: NetPI, aiproxy.`).

## UI

- Header: the project filter (the active project's name by default, following the active project until the user picks
  another; *All projects*, *Global (unbound)*, and every known project), a storage mark and a **+** button. The mark
  says where the backlog is — *"The ideas backlog lives in the netpi.db database (netpi.ideas, version 1) — it is not a
  file you can edit"*, from `ideas.list().storage` — because there is no path to open any more.
- Filters: search over title, summary, tags and sections; the project filter above; a status menu (**Active** = open,
  planned, in-progress; **All**; or one status) with counts; and a **#** tag menu (multi-select). Selected tags show as
  removable chips below the filters.
- Cards: a calm overview — **one line per idea, the title only**, plus a small priority mark (high ▲ / low ▼, medium
  none) and the section count. A low-priority title is dimmed. Nothing else is on the line, so the list reads as a list
  of titles; the summary, the status pill, the project badge, priority, tags and the time all appear when the card is
  opened, together with the sections as rendered markdown, each with an edit button (`ideas.update` with
  `patch.sections` or `updateSections`).
- The list is grouped by status: **in-progress, planned, open** (the order the work wants them read) each under a
  header with a count, then **parked, done, rejected** collapsed to one line per status (`name  count`, click to
  expand). Grouping is presentation only — the stored order is still the user's, and a status change moves the card
  (a drag or *Move up/down* orders within the rendered list).
- New idea: title, summary, priority, a project picker (default: the active project; *Global* for unbound; any other
  project) and tags. The title has to carry the idea on its own — the `ideas` tool help and prompt guideline say so.
- Evidence (in an open card): the chats that worked on the idea — with a dot for one you have not opened the idea since —
  and the commits recorded for it, both with their dates.
- Actions: "Send to chat" (stages a pointer to the idea in the composer — it never changes the status), "Insert the full text"
  (`ideas.toPrompt` → `ctx.app.insertText`), Delete (with a confirm) and drag to reorder
  (`ideas.reorder`).
- **Conflicts are shown, not swallowed.** An edit sends the `revision` the card had when it was opened, so a change
  made elsewhere while it was open is refused with `conflict`: the editor keeps what was typed, a toast says the idea
  changed somewhere else, and a line at the foot of the tab names the idea — reopen it to see the other version and
  apply the change again. Nothing is overwritten and nothing typed is lost.
- Refresh on the `ideas.changed` event (it arrives after every committed write, from this window too) and follow the
  active project on `ctx.app.onChange`. The list is a plain read: a missed event costs one more `ideas.list`, never a
  stale card, and the footer line says how many of the ideas are shown and which database they are in.

## Upgrading and going back

The backlog moved out of JSON files once, and the files are still readable afterwards. What each side does with them is
deliberate, because a storage version is not something to find out by trying to write it.

**The cutover happens on the first start, and the safe way to reach it is a restart.** Nothing has to be run by hand
(see "The one-time cutover" above), but the first start of the new build is the moment a *still-running* older build
could be writing the same JSON file — which is exactly what a plugin reload is. So: after the upgrade, stop NetPI and
start it again rather than reloading the plugin into the running instance. If an old build did get a write in, the file
reappears where the new build no longer looks, and `ideas.migration` reports it as `orphanedSources`; nothing merges it
silently, and `ideas.migrate { confirm: true, force: true }` takes it in on purpose (with the ids and the order it had).

**A database from a newer NetPI is refused.** The tables carry a storage version (1 now). A build that finds a higher
one logs an error and registers neither the tab nor the tool, rather than reading a shape it would mangle on the next
write. Nothing is written, the backlog stays exactly as the newer build left it, and the answer is "update NetPI" —
that is the only supported direction of travel.

**The marker cannot stop an old build.** The backend marker in `ideas_metadata` is a promise *this* build keeps. A
NetPI that has already shipped does not read it: started against the same home, an old build finds an empty backlog and
would write `ideas.json` that nothing reads. What actually protects you is the archive — after the cutover the
originals are moved out of the place the old build read them from, so it has nothing to read and starts empty rather
than half-right. There is no bridge release, and nothing here claims an old binary is safe against a database it does
not know about: a pre-cutover build is protected from the *files*, not from the tables.

**Going back, on purpose**, with the app **stopped**:

- **Move the archived originals back.** Every cutover left a copy of each source and, under `originals/`, the file
  itself; `<home>/ideas-archive/<timestamp>/manifest.json` says which path each one came from. Put them back and the
  old build sees the backlog as it was at the cutover — and only that: everything changed since lives in the database
  and nowhere else, so this loses it. Take an `ideas.export` first if that matters.
- **Or transfer deliberately**: `ideas.export` gives a snapshot document, `ideas.import` takes one in (`merge` by
  default). This is how ideas move to another machine, and it is the only way back that can carry the work done since
  the cutover.

**Rolling the database back is a different thing.** The ideas tables live in the shared `netpi.db` beside the sessions,
messages, projects, agents and usage. Restoring a copy of that database from before the cutover therefore also rolls
back every chat, and it drops the ideas tables with their data — a whole-home decision, not an Ideas one. The Ideas-only
paths are the export and the archive above.

**A backup is enough.** A snapshot is now `netpi.db` and `settings.json`, so the ideas, their cards, the answers given,
the check marks and the cursors travel inside the consistent database copy: a backup needs no Ideas plugin and cannot
be a snapshot that quietly left the backlog out (docs/BACKUPS.md). An older snapshot that still carries the JSON files
verifies and restores too — restore it into a new home and the plugin imports those files at the first start, which is
the cutover again, from a copy of the originals rather than from your live ones.
