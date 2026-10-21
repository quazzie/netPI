# Ideas plugin (`netpi.ideas`)

A backlog of ideas, research, plans and deferred work for the user and for agents. Agents work with it through the
`ideas` tool. The user works with it in the **Ideas** tab (right panel) and through `/idea <title>`. After an agent
commits, a notice asks it to update the idea the commit finished. Nothing else runs in the background: until
2026-10-11 the plugin also judged closed chats and every commit with a decision model (save on tab close, close on
commit, a verifier); those checks were removed, and the record of how they did is
[archive/2026-10-11-slim.md](archive/2026-10-11-slim.md).

- Plugin: `plugins/NetPI.Ideas`, id `netpi.ideas`, start order 80.
- Tab: `{ id: "ideas", title: "Ideas", panel: "right", icon: "idea", order: 20, module: "ui.js" }`. The UI module goes in
  `plugins/NetPI.Ideas/wwwroot/ui.js` (source in `plugins/NetPI.Ideas/ui/`) and is served at `/plugins/netpi.ideas/ui.js`.
- Slash command: `{ name: "idea", argsHint: "<title>", rpc: "ideas.quickAdd" }`.
- Settings: `ideas.tellAgentOnCommit`, `ideas.commitNoticesPerRun`, `ideas.commitNoticeDebounceSec`, `ideas.noticeFloor`,
  `ideas.semantic`, `ideas.similarThreshold`, `ideas.autoRefine.*` (docs/SETTINGS.md).

## Where ideas are stored

**In the store's plugin data.** The backlog is this plugin's own **collections of JSON documents** under plugin id
`netpi.ideas` (schema version 2), reached through `ctx.Data` and one read/write entry point (`IdeasRepository`). The
store behind it is whatever `storage.provider` selected — `netpi.db` with the built-in `sqlite` provider, any other
provider otherwise — and no SQL is written here: a collection is a set of documents under a string key with declared
index fields, and a plugin transaction is the compare-and-set. The shapes are written down in one comment block at the
top of `IdeasRepository.cs`.

| Collection | Holds |
|---|---|
| `items` | one document per idea (its own fields) plus `revision`, `ord` (the order the user arranged), `idLower` and `projectId`, indexed on `ord`, `status`, `projectId`, `idLower` |
| `imports` | the receipts of the imports (one per snapshot taken in) |
| `vectors` | the embeddings, one document per idea: `{ model, dim, hash, at, chunks: [base64 float32…] }` (`model` indexed). Chunk 0 is the card (title, summary, tags), the rest the sections in 1,500-character pieces (at most 12). Derived data: deleting the collection only costs a re-embed |

A home that ran an earlier version still holds the collections of the removed checks (`cards`, `resolutions`, `checks`,
`repos`, `unread`, `meta`, `decisions`); nothing reads them.

Moving a backlog between machines is deliberate: `ideas.export` writes a portable snapshot document and
`ideas.import` takes one in. There is no automatic reading of files.

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

The index fields (`ord`, `status`, `projectId`, `idLower`) are properties of the document itself, written with it, so
the two cannot drift apart; and an idea is still only ever understood as the document it always was — sections, tags,
chats and every field another tool wrote by hand live in it, which is why a save never drops anything. Every repository
method is short and synchronous: a plugin transaction is held for the length of a few writes and never across a model
call, a file read or anything else that can block.

## The idea document

One idea is one JSON object — the document stored under its id, and what an import reads and an export writes:

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
  "sessions": [                        // chats that worked on it (attach, refine)
    { "sessionId": "ses_…", "title": "…", "at": "2026-09-28T10:00:00Z", "note": "…" }
  ],
  "autoRefine": {                      // absent = not refined yet; written by the plugin only (see "Automatic refine")
    "state": "running",                // running | done | failed
    "sessionId": "ses_…", "at": "…", "finishedAt": "…", "agent": "qwen",
    "manual": true,                    // set by a manual ideas.refine (counts as refined, no badge)
    "error": "…"                       // failed only
  }
}
```

Ideas from earlier versions may also carry `commits` (commits the removed commit check recorded); the card still shows
them.

`autoRefine` is bookkeeping the plugin owns: an `ideas.update` patch and an import's extra fields cannot set it. An export
carries it like any field; an idea imported with `state: running` is settled by the first sweep (no agent runs its chat).
An existing idea simply has no field and counts as not refined.

A snapshot export wraps ideas in a versioned document (`{ "version": 1, "format": "netpi.ideas.export", "ideas": [ … ],
… }`), and an export is not a live file: editing it changes nothing in the backlog, and nothing is re-imported because a
file changed.

`revision` is not a field of the idea itself: it travels beside the document, and is added to the copy a caller reads
(so the document that is stored and the document an editor submits back are the same thing without a field of its own).

- Section `kind`: `note | research | plan | requirements | design | decision | blocker | links | todo`. Common synonyms
  are mapped (`spec` → `requirements`, `tasks` → `todo`, …). Anything else becomes `note`. `title` is optional.
- Idea ids are unique within the backlog, and section ids are unique within their idea. An id that is not of our making
  is stored and found exactly as it is; only an id that *collides* with an idea already here is given a new one.
- The store is the source of truth, and there is no second copy of it in memory to go stale: a patch is read,
  validated and applied to a detached copy of the current document **inside the transaction that writes it**. A patch
  that fails validation (an empty title, an unknown status) therefore leaves nothing behind.
- A write is a transaction: either the whole change is stored or none of it is, and a patch that changes nothing is
  not written at all (no new `revision`, no event). A hot reload is a swap, so two instances of this plugin are
  briefly live against the same collections; a plugin transaction is exclusive per plugin id across reload
  generations, so they cannot interleave and no lock file or watcher is needed.
- What a save preserves: **unknown fields at every level of the idea** (the idea, its sections).
- An exported snapshot is plain JSON (LF, two-space indent).

## Event

| type | scoped | data |
|---|---|---|
| `ideas.changed` | no (broadcast) | the `ideas.list` `storage` descriptor — `{ backend, database, scope, schemaVersion, editableFile, importExport }`, with `backend` the id of the storage provider in use (`sqlite` by default) and `database` the file it keeps its data in — plus `reason?` — which write it was (`add`, `update`, `attach`, …) |

The event fires after every write that committed, whether it came from a tool, from an RPC or from another window, and
never for one that rolled back. It is a **notification, not exactly-once delivery**: a window that missed one gets the
same state from the next read, and a reconnecting window re-reads canonical state rather than trusting what it kept.

**UI:** on `ideas.changed`, call `ideas.list` again — it is the canonical read, so a missed event costs a refresh, not
a stale view.

## RPC methods

There is one backlog: `ideas.list` takes only filters, and the other methods are addressed by `id` alone (ids are unique
in the backlog). `ideas.add` keeps `sessionId`/`projectId` to decide the new idea's `project` stamp. Results are plain
JSON: an idea is returned exactly as it is stored, including unknown fields, plus the `revision` it is at.

Every idea carries an integer **`revision`**, raised by every stored change. `ideas.list`, `ideas.get`, `ideas.add` and
`ideas.update` all return it, and `ideas.update` takes it back as `expectedRevision` — that is how a stale window is
turned into a `conflict` instead of a silent overwrite.

The methods that only read are registered `readOnly`: `ideas.list`, `ideas.picks`, `ideas.get`, `ideas.toPrompt`,
`ideas.image`, `ideas.similar` and `ideas.capabilities`. A tool may call those without `--write` (`docs/PROTOCOL.md`);
everything else this plugin registers writes.

Error codes: `bad_request` (invalid input: the message says what is wrong, for example the list of valid statuses or
projects), `not_found` (idea, session or project), `conflict` (an edit whose `expectedRevision`/`expectedUpdatedAt` is
stale, or an id that is already in the backlog), `io_error` (a file write that could not complete, an export for
example).

### `ideas.list`

`{ projectId?, limit? }` →

```ts
{
  storage: {
    backend: 'sqlite';          // the storage provider in use (`sqlite` by default); there is no editable file behind the backlog
    database: string;           // the file that provider keeps its data in ("netpi.db")
    scope: 'netpi.ideas';
    schemaVersion: 2;
    editableFile: false;
    importExport: 'json';
  };
  ideas: Idea[];                // the user's manual order
}
```

`projectId` (an id or a name; `global` for the unbound ideas) and `limit` narrow the list. The tab asks for everything
and filters by project, status and tag client-side.

### `ideas.picks`

`{ projectId?, limit? }` → `{ picks: [{ id, title, summary, status, priority, projectId?, projectName?, updatedAt }] }`

The short list a screen offers where a session is about to start (the welcome screen reads it, idea-ky14bu). It is
deliberately lean — no `sections`, `images`, `commits`, `sessions` or `tags` — so a window can read it on every start
without carrying the whole backlog; the detail is one `ideas.get` away.

Only what nobody is working on yet: `open` and `planned`. Never `in-progress` (that one is being worked on in some
chat), `parked`, `done` or `rejected`. Ranked:

1. the `projectId`'s own ideas, then the unbound ("global") ones — with no `projectId`, only the global ones;
2. `open` before `planned`;
3. `high`, then `medium`, then `low` priority;
4. the more recently updated first, with the user's own order as the last tie-break (so two ideas updated in the same
   second never swap places between reads).

`limit` defaults to 5 and is clamped to 1–24. An unknown `projectId` is not an error: it simply matches no bound idea,
so the global ones are what comes back.

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
| `expectedRevision` | (a parameter, not a patch field) the revision the editor read: the update is refused with `conflict` when the idea is at a newer one, so a stale window cannot silently overwrite newer content |
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
Implement the following idea from the ideas backlog (`idea-k3x9q2`).
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

### `ideas.attach` and `ideas.refine`

`ideas.attach { sessionId, id }` → `{ noticeId, ideaId }`: adds the idea to the chat as a notice (`meta.kind: "idea"`,
`meta.ideaId`) that starts *"The user added an idea from the ideas backlog to this chat"* and holds the idea's full
markdown (as `get` renders it). The session is added to the idea's `sessionIds`. Added before the first message, the
notice stays first; added during a run, the agent reads it at its next model call.

`ideas.refine` (the card's "Refine") starts a chat on the idea's project with the idea attached and an agent tasked to
define it better, without changing any files; the chat is recorded on the idea's `sessions`.

### Automatic refine (`ideas.autoRefine.*`)

Off by default. When on, the owner's own agents refine newly filed ideas in the background through the same code as
`ideas.refine` (one method, `IdeaRefine.StartAsync`), so each idea becomes a clear, checkable piece of work without the
owner pressing Refine. Settings (docs/SETTINGS.md): `enabled` (false), `agents` (the agent keys that may run it; **empty
means nothing runs**, never "any agent", which could be a paid one), `maxConcurrent` (1, 1–4), `delayMinutes` (1),
`archiveWhenDone` (true).

- **Which ideas:** status open, filed by the user (`createdBy: "user"`, not an agent), no `autoRefine` field yet (a manual
  `ideas.refine` sets it too, so it counts), not tagged `no-refine`, and neither created nor edited for `delayMinutes`
  (`updatedAt`, so a half-written idea is left alone). Oldest first. The state writes (`SetAutoRefine`) change neither
  `updatedAt` nor any of the owner's text. An edit while a run is going leaves the run alone.
- **Pace:** it keeps going until every eligible idea is refined, oldest first, at most `maxConcurrent` at once. The chats
  run at low priority: a run keeps its slot until it ends, and the owner's chats take the next free slot before the next
  refine. Tag an idea `no-refine` (the `ideas.autoRefine.skip` RPC,
  or by hand) to keep it out.
- **The drain:** a timer (every minute) and every idea change run one pass; it does something only when enabled, with
  agents listed, fewer than `maxConcurrent` running. The agent is the
  next of `agents` in turn, preferring one with a free instance (`agents.list`; disabled/unavailable ones are not used).
  The chat is as `ideas.refine`'s, plus `meta.priority = "low"` (the owner's chats win the slots), `ask_user` switched off,
  and a prompt saying nobody is watching: questions go into the idea's "Questions" section (kind `blocker`).
- **Completion** is seen on `agent.status` events of those chats (idle/completed → `done`; failed/cancelled → `failed` with
  the error) and by a sweep on every pass, because bus events can be dropped. A run still marked running after a restart is
  settled by the sweep from its chat (an answer from the agent → done, none → failed "interrupted"; a deleted chat → failed).
  Then the chat is archived when `archiveWhenDone` (it stays linked under the card's Chats), and `ideas.changed` fires
  (`reason: "auto-refine"`). A start that fails is `failed` with the reason and leaves no chat. A failed idea is not retried
  by itself: the card's **retry** opens the normal refine.
- `ideas.autoRefine.status` (read-only) `{}` → `{ enabled, agents, maxConcurrent, running: [{ id, title,
  sessionId, at }], queued: [{ id, title, dueAt }] }`; `queued` is derived (eligible ideas, delay not counted), it is not a
  stored state. `ideas.autoRefine.skip { id }` → the idea, with the `no-refine` tag added.

### `ideas.export`, `ideas.import`

Moving the backlog between machines, or into a fresh home, is deliberate — there is no automatic reading of files:

- **`ideas.export` `{ path? }`** → `{ json, file? }`: a portable snapshot — `format: "netpi.ideas.export"`, `version`,
  `exportedAt`, the ideas with their ids and the user's order. With a `path` it is written there too and the absolute
  path comes back; without one, nothing touches the disk.
- **`ideas.import` `{ json | path, mode?: "merge" (default) | "replace" | "validate" }`** → `{ ideas, conflicts,
  unknownFields, imported?, mode }`. `validate` is the preview: the count, the ids that are already here (a conflict),
  and whatever in the document this build does not take in (the cards and check state older builds exported are listed
  there). `merge` adds what is not here yet and never overwrites what is; `replace` empties the backlog first, which is
  why it has to be named. An imported document is read once, when the call says so.

### `ideas.similar`, `ideas.reindex`, `ideas.capabilities`

`ideas.similar { id | text, limit? (5), status? (active), projectId? }` → `{ available, ideas: [{ id, title, status,
score }] }`: the ideas closest in meaning (embeddings; `available: false` without them). `ideas.reindex` brings the
embeddings in step now (normally a background pass after every write). `ideas.capabilities` → `{ embeddings }`: whether
meaning search is available, for the tab's ≈ toggle.

## Tell the run that committed

After the agent itself commits, `IdeaCommitNoticeHook` (an `IAgentHook`, order 260) injects **one notice** (kind
`git-commit`) before the next model call:

> A commit just landed in NetPI (the git command you ran succeeded). Open ideas of that project: "…", "…". If this
> commit finishes one of them, update that idea now with the ideas tool: mark it done, or leave it open and add a short
> section saying what landed. If none of them is about this commit, say so in one line and do not create an idea for it.

It is advice, not an action, exactly like a nudge: the agent decides, the user reads what it did. What it takes to get
there, all of it a reason to stay silent instead:

- the tool call is `bash`/`pwsh` and its `command` runs `commit` or `merge` (token by token, so `git -C dir commit` and
  `git add -A; git commit` are seen, and `git log`/`git push` are not), with none of `--dry-run`, `--abort`, `--quit`,
  `--no-commit` after it;
- the result is not an error and its exit code is 0 (a JSON null exit code is a background command still running);
- the directory the commit ran in belongs to the session's repository (compared by git's common directory, so a sibling
  worktree of the same repository counts and another repository does not), and the run has a project and the `ideas`
  tool;
- the project has at least one open idea (so a project without a backlog never pays an extra model call);
- the run has not had `ideas.commitNoticesPerRun` (2) of them yet, and its last notice is more than
  `ideas.commitNoticeDebounceSec` (30) old, so a burst of commits costs one notice. Two commits inside one model call
  are one notice.

The notice names the project's open ideas only when there are at most three of them (each clipped): with a backlog that
size any few of them are an arbitrary slice, so the notice says that a commit landed, offers the `ideas` tool and
obliges the agent to nothing. With embeddings on (`ideas.semantic`, an `embed.model`) a larger backlog is no longer
silent: the commit message (the `-m` texts of the command, else the `[branch hash] subject` line git prints) is
embedded, and the notice names up to three open ideas at or above `ideas.noticeFloor` (0.6) by id and title, as
candidates the agent checks ("possibly none of them is about it"). The lookup is bounded (3 s for the whole step); no
answer is the plain notice. A commit made by a provider's own tools (the Claude CLI) gets no notice: that model cannot
call the `ideas` tool. Skips: `ideas.tellAgentOnCommit` off.

## Meaning search (embeddings, idea-61wg9p)

An optional enhancement on the Embeddings plugin's `IEmbeddingService` (`plugins/NetPI.Embeddings`, settings `embed.*`),
resolved per use: without it, with `embed.model` empty, `ideas.semantic` off or the server failing, every path below is
the word search alone. `IdeaVectors` keeps the `vectors` collection in step with the backlog — a pass 2 s after a write
(a burst is one pass) embeds only the ideas whose text hash or model changed and drops the vectors of deleted ideas; a
search that meets an unindexed idea leaves it out and starts a pass. Search is a cosine scan in process (the best of an
idea's chunks). Embeddings only **shortlist**; the agent or the user decides. Where it is used:

- the commit notice (above): the open ideas nearest a commit, when there are too many to list;
- the `ideas` tool's `list` with a `query` no idea fully contains: the five closest ideas (same status and tag filters)
  under "Closest by meaning", `details.closest` = `[{id, title, score}]`;
- the tool's `add`: existing ideas whose card is at least `ideas.similarThreshold` (0.80) close to the new one's are
  named (`details.similar`), a warning, never a refusal;
- the tab's ≈ search, `ideas.similar` and `ideas.reindex` (RPC).

## The agent tool: `ideas` (category `ideas`)

One tool with an `action`, so a single schema goes with every request. It works on the one backlog in the store.
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
| `list` | `{ status?, tag?, query?, project? }` | `project` selects the scope: no argument → the session's project **plus** the unbound "global" ideas (an unbound session sees only the unbound ones); `"all"` → every project (each line carries a project label); `"global"` → the unbound ones; a project id or name → that project only. Unknown projects give an error listing the known ones. Compact lines: `- idea-… [status · priority (· project)] Title — summary #tags (n sections)`. By default done and rejected ideas are hidden, with a count of how many were hidden. `status` also takes `active` and `all`, or a comma-separated list. `query` needs every word to appear in the title, summary, tags or sections; when no idea does and embeddings are on, the five closest ideas by meaning follow ("Closest by meaning"). |
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
  says where the backlog is — *"The ideas backlog lives in the netpi.db database (netpi.ideas, version 2) — it is not a
  file you can edit"*, from `ideas.list().storage`.
- Filters: search over title, summary, tags and sections (with embeddings, a ≈ toggle ranks by meaning); the project
  filter above; a status menu (**Active** = open, planned, in-progress; **All**; or one status) with counts; and a **#**
  tag menu (multi-select). Selected tags show as removable chips below the filters.
- Automatic refine: while `ideas.autoRefine.enabled`, the header shows "Auto refine: N running, M waiting"; a card shows
  *refining…* while its run goes, *auto-refined* (a link to the chat) when it is done and *refine failed* with a **retry**
  (the normal refine dialog) when it failed.
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
- Evidence (in an open card): the chats that worked on the idea, and the commits earlier versions recorded for it, both
  with their dates.
- Actions: "Send to chat" (stages a pointer to the idea in the composer — it never changes the status), "Insert the full text"
  (`ideas.toPrompt` → `ctx.app.insertText`), "Refine", Delete (with a confirm) and drag to reorder (`ideas.reorder`).
- **Conflicts are shown, not swallowed.** An edit sends the `revision` the card had when it was opened, so a change
  made elsewhere while it was open is refused with `conflict`: the editor keeps what was typed, a toast says the idea
  changed somewhere else, and a line at the foot of the tab names the idea — reopen it to see the other version and
  apply the change again. Nothing is overwritten and nothing typed is lost.
- Refresh on the `ideas.changed` event (it arrives after every committed write, from this window too) and follow the
  active project on `ctx.app.onChange`. The list is a plain read: a missed event costs one more `ideas.list`, never a
  stale card, and the footer line says how many of the ideas are shown and which database they are in.

## Upgrading and going back

The backlog is documents in the store's plugin collections, so "an older shape" is the store's question, not this
plugin's: a provider refuses a store it cannot open, and this build reads the documents as they are, keeping every
field it does not understand.

**Going back, on purpose**, with the app **stopped**:

- **Transfer deliberately**: `ideas.export` gives a snapshot document, `ideas.import` takes one in (`merge` by
  default). This is how ideas move to another machine or into a fresh home, and the only path back that carries the
  work done since.
- **Or roll the store back, for the whole home.** The ideas collections live beside the sessions, messages, projects
  and the ledger in the same store (`<home>/netpi.db` with the `sqlite` provider), so restoring an older copy of the
  provider's files also rolls back every chat: a whole-home decision, not an Ideas one (docs/BACKUPS.md).

**A backup is enough.** A snapshot is the files the provider wrote plus `settings.json`, so the ideas travel inside the
consistent copy: a backup needs no Ideas plugin.
