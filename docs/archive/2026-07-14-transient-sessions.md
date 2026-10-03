# Transient sessions: don't save a session that is closed before doing anything

Status: implemented · 2026-07-14

## Problem

`sessions.create` inserts a `sessions` row immediately (`SessionStore.CreateSession`). Open a chat,
send nothing, close the tab or the app — and the row (title "New session", `message_count = 0`)
stays in the database forever. There is no "session closed" event and no cleanup, so abandoned
empty chats accumulate in the session list (`SessionsTab` searches `sessions.list`).

## Goal

A session with no messages is never persisted. It exists in host memory only while the host runs
and disappears when the tab, the app, or the host goes away. The **first message** is "doing
something": from then on the session is saved exactly as today.

## Design: materialize on first message

A session is **transient** from `CreateSession` until its first `AppendMessage`; it lives in a
small in-memory dictionary in `SessionStore`. The SQLite row is inserted ("materialized") in the
same transaction as the first message. Every `ISessionStore` method resolves transient sessions
transparently (`GetSession` checks the dictionary first), so **no interface change, no new RPCs
or events, no plugin rebuilds** — the whole change is inside `SessionStore`.

### Why not the alternatives

1. **Delete-on-close + startup prune** (UI deletes empty rows on tab close; host prunes
   `message_count = 0` rows at start): empty chats are still visible in the list while the app
   runs; a startup prune deletes empty sessions the user still has open (losing their chosen
   model/agent and the `netpi.draft.<id>` draft); and "delete on close" races the first in-flight
   message. It treats the residue, not the behavior.
2. **UI-only: call `sessions.create` lazily on first message**: the composer, agent picker
   (`agents.use` needs an existing session) and all slash commands assume a live session; other
   clients or tools could still create empty sessions; far more UI churn for a weaker guarantee.

### Contract

| Moment | What happens |
|---|---|
| `sessions.create` | Session stored in memory only. **No DB row, no `session.created` broadcast** (other clients never see an empty chat; the creating client gets the `SessionInfo` as the RPC result). The project's `last_used_at` is *not* touched yet. |
| Read while transient | `GetSession` / `sessions.get` resolve the in-memory copy (so open-tab restore via `sessions.get` keeps working while the host runs). `sessions.list` does not include it. |
| Update while transient | `UpdateSession`, `SetSessionProject` (title, model, agent, profile, tools, project, archived, meta) mutate the in-memory copy. **No `session.updated` / `session.project` broadcast** (the creating client gets the updated `SessionInfo` as the RPC result). |
| First `AppendMessage` | **Materialization**: one transaction inserts the `sessions` row (with `message_count`/title as `AppendMessageCore` computes them) and the message; if it has a project, the project's `last_used_at` is set. After commit: `session.created` (the session now has ≥ 1 message, consistent with `sessions.list`), then the usual `message.added` + `session.updated`. From here on the session behaves exactly as today. |
| `sessions.delete` while transient | Remove from memory; publish `session.deleted` (harmless: the creating client removed it locally already). No DB work. |
| `sessions.fork` | A fork copying ≥ 1 message materializes as today (`session.created` then `session.forked`). A fork of an empty session (0 copied messages) is transient and emits neither. |
| Host restart / crash | Transient sessions are gone. The UI already drops tabs whose `sessions.get` fails (`loadAll` filters `openTabs` to known ids), so closed empty tabs simply don't come back. |

What a transient session can lose on restart (by design, since nothing was done): chosen
agent/model, profile, project, archived flag, and the composer draft (localStorage
`netpi.draft.<id>` — it is orphaned today anyway).

## Changes

### `src/NetPI.Host/Sessions/SessionStore.cs` — the only production change

- `Dictionary<string, SessionInfo> _transient` plus a gate (lock).
- `CreateSession`: build the `SessionInfo` as today, store it in `_transient`; **no** insert,
  **no** `Publish(SessionCreated)`.
- `GetSession`: check `_transient` first, then the DB.
- `UpdateSession`, `SetSessionProject`: transient branch — mutate the in-memory copy, return a
  copy, no broadcast. DB branch unchanged.
- `AppendMessage`: transient branch — under the gate, one transaction: insert the sessions row +
  run the existing append core (title, counters, seq); remove from `_transient`; then
  `Publish(SessionCreated)` and the existing `PublishMessage(MessageAdded)` + `Publish(SessionUpdated)`.
  Common (DB) path unchanged.
- `ForkSession`: count the messages to copy first; 0 → transient fork (store, no events);
  > 0 → the existing transactional path.
- `DeleteSession`: transient branch — remove, publish `session.deleted`. (DB branch unchanged;
  note the recursive CTE already leaves non-existent children alone.)
- `ListSessions`: unchanged — transient is excluded by construction.
- Concurrency: only the transient path takes the gate (rare); two appends racing onto a brand-new
  session serialize there. No interface/contract changes → no plugin rebuild.

### Plugins — no code changes, behavior notes

- **Profiles** (`ProfilesPlugin`): its `session.created` handler (`EnsureDefault`) now fires at
  materialization — `MessageCount` is 1, so the existing `MessageCount > 1` guard still passes.
  The `ProfileHook` before the first model call already guarantees the default profile in time for
  the first request (it becomes the real path; the fast path just gets ~one message later).
  Net: the profile's `session.updated` arrives after the first message, not after create.
- **Work tab** (`session.created → setTitle`), **UI** (`upsertSession`): fine — the creating
  client learns of the session from the `sessions.create` result; other clients at
  materialization. `upsertSession`'s race comment ("the default profile the server gives the new
  chat right away") moves to "with the first message".
- **Runtime/subagents**: a subagent session is created and gets its task message immediately →
  it materializes right away; behavior unchanged.
- All other `AppendMessage` callers (goal, todo, skills, compaction, project/tool notices)
  happen inside a run, i.e. after the user message — no difference.

### UI — no functional changes

- `newSession`: the tab opens from the RPC result as today.
- Session list panel (`SessionsTab`): fetches `sessions.list` → an empty chat is invisible until
  its first message — the intended fix.
- Tab restore after a restart: `sessions.get` per persisted tab → empty sessions 404 → dropped
  (existing code path, `loadAll`).

### `web/mock/server.mjs` — mirror the behavior (dev tool)

`sessions.create` stops publishing `session.created` (and moves the default-profile write there
to the first message), so `npm run mock` keeps the same observable behavior as the host.

### Docs

- `docs/PROTOCOL.md`: the `sessions.create` row (not saved until the first message) and the
  `session.created` event row (published when a session gets its first message; a fork of a
  non-empty session still publishes as today).
- `docs/UI.md` (~line 693): the "profiles plugin gives the new chat its default profile on
  `session.created`" note → now with the first message / first run start.

### Tests

- `tests/NetPI.Host.Tests/SessionStoreTests.cs` (new, existing style):
  1. create → `GetSession` ok, **not** in `ListSessions`; a *fresh* `SessionStore` over the same
     DB file (simulated restart) does not know it.
  2. update a transient session (title/model/meta) → visible via `GetSession`, still not listed,
     no `session.updated` on the bus.
  3. first `AppendMessage` → materializes: in `ListSessions` with count 1; bus order
     `session.created` → `message.added` → `session.updated`; visible to a fresh store instance.
  4. delete a transient session → no error, no DB row, `session.deleted` published.
  5. fork of an empty session → transient (not listed); fork of a non-empty session → the
     existing test, updated for the new event ordering.
  6. auto-title (first user message sets the title) still works — now set at materialization.
- `tests/NetPI.Host.Tests/ServerTests.cs`:
  - `sessions.create` alone publishes **no** `session.created` (client B: not seen until after
    the first message — add the negative check before the existing positive one at ~line 272).
  - `sessions.list` excludes the empty session, includes it after a message.
- `tests/NetPI.E2E/AdvancedTests.cs`: the profiles E2E ("a new chat starts with its project's
  default profile … before its first message") — retime: the profile meta/event arrives with the
  first message; the strongest assertion is that the first model request carries the profile's
  system prompt (check what MockLlm records of requests; if requests aren't logged, assert the
  session meta after the first message).
- Plugin tests (`ProfilesTests` etc.) are **unchanged**: their fake stores publish
  `session.created` on create and the plugin code is untouched.

## Edge cases

- Archived / project-bound / agent-bound empty session: lost on host restart (by design).
- Parent session deleted while a transient subagent child is pending: its materialization inserts
  a row with a dangling `parent_session_id` (orphan). Extremely rare; acceptable, or add a parent
  check at materialization (decide when implementing — keep it simple first).
- Tab sync (`fetchRemote(TABS_KEY)`): another client restores a transient tab only while the host
  still runs (then `sessions.get` resolves it) — after a host restart both clients drop it.
- `session.created` now always carries `message_count ≥ 1`; all subscribers (profiles, work tab,
  UI) are compatible.

## Verification

- `.\build.ps1 -Test` (unit suites incl. the new SessionStore/Server tests), then
  `dotnet tests/NetPI.E2E/bin/<Config>/NetPI.E2E.dll` with the updated profile test.
- Manual: run the app, open three empty chats, restart NetPI → the session list is empty; send a
  message in one before restarting → only that one survives.

Estimate: ~150–200 lines in `SessionStore` + tests; docs and mock are small.
