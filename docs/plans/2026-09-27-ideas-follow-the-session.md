# Ideas follow the session: recall, save on close, close on commit

Status: phases 1 (recall) and 2 (save on close) built 2026-09-28; phase 3 (close on commit) next · idea-c7xyem

## Goal

Plans made with an agent get lost when the user moves to another chat. Three checks keep the ideas backlog (the
only place the user looks: not `docs/plans`) in step with the sessions, without filling it with noise:

1. **Recall, on the first message.** While the first message of an empty chat is typed (and once more on the sent
   text), a decision finds the open idea it continues. A chip above the composer offers to add that idea to the
   conversation. Never delays the send.
2. **Save, when a session tab closes.** A reasoning-low call decides whether the chat leaves a plan, feature idea or
   research question that was neither built nor saved, and drafts it. Work on an existing idea is attached to that
   idea without asking; a new plan gets a card (save, edit, discard).
3. **Close, on a commit.** New commits in a project's repository are linked to the idea they work on; after each
   linked commit a decision asks whether the idea is done, and a card offers to mark it done.

Nothing new reaches the backlog without a click. Measured first (decisions-lab `ideas_*.mjs`, results in
`docs/DECISION-MODELS.md`, "Ideas recall" and "Ideas: save on tab close and close on commit"):

| check | how | result |
|---|---|---|
| recall | `/v1/decision` pick-one over the open ideas (title + summary, the list as the cached state) | p ≥ 0.8: 0/56 false chips, 42/46 written and 3/6 real matches, 95 ms |
| save | one chat call, reasoning low, that decides and drafts (prompt v3) | 8/11 caught, 1/32 false, 1.6 s |
| which idea a session worked on | pick-one on the session digest | 5/6 at 0.8 |
| close | link by pick-one (p ≥ 0.7), then "done?" on the idea's full text + its linked commits (p ≥ 0.8) | no wrong link, 4/5 finished ideas offered, no false offer |

## Design

Everything lives in the **Ideas plugin**; the models are reached through the **Decide plugin** (RPC, no reference).

- **`decide.decision`** (new, Decide): `{ messages, branches, model?, share_state? }` → NInfer's `/v1/decision`
  answer, posted through the same server as `decide.ask` (AiGateway). `decide.ask` goes through `/v1/systemone`,
  which cannot keep the ideas list as a cached shared state; recall needs that for 95 ms per check.
- **Settings** (`ideas.*`): `ideas.recall` (on), `ideas.saveCheck` (on), `ideas.commitCheck` (on),
  `ideas.model` (the decision and side-call model, default `qwen3.8-27b`: measured, local, no paid tokens),
  thresholds `ideas.recallThreshold` 0.8, `ideas.attachThreshold` 0.8, `ideas.linkThreshold` 0.7,
  `ideas.doneThreshold` 0.8.
- **Idea data** (additive; unknown fields already survive): `sessions: [{ sessionId, title, at, seq?, note?, seen? }]`
  (the session's note from the save check; `sessionIds` stays as it is) and `commits: [{ repo, hash, at, subject }]`.

### Phase 1: recall

- `ideas.recall { sessionId, text }` → `{ match: { id, title, p } | null, reason, ms }`. An idea id in the text is the
  match without a model. Otherwise one decision over the open ideas of the session's project plus the global ones
  (title + summary per option, plus "none"; the list is the system prompt so repeated checks reuse NInfer's cache).
  A match needs p ≥ `ideas.recallThreshold` and to beat "none". `reason`: `id`, `model`, `none`, `off`,
  `unavailable` (no Decide plugin), `error` (with the message; logged).
- `ideas.attach { sessionId, id }` → appends a notice (kind `idea`) with the idea (title, summary, sections) to the
  session and records the session on the idea. Before the first message the notice stays first; during a run the
  runner reads it at its next model call (the context is reloaded per call).
- UI: `composer/IdeaChip.svelte` in the composer dock (next to the Todo strip; core UI, because a plugin tab is only
  loaded once opened). While the chat has no messages: `ideas.recall` ~1 s after typing pauses (text ≥ 12
  characters, again when it changed a lot); after the first send, once more on the sent text. One line: the idea's
  title, **Add**, dismiss. Added or dismissed: not shown again in that chat. The notice row gets an `idea` style.

### Phase 2: save on tab close

Built 2026-09-28 as designed, with these changes to the plan above:

- The check runs on the **message count**, not on time: the pending file holds one mark per chat (`checked: { n, at }`,
  kept 30 days), so a tab closed, reopened and closed again does not ask twice, but new turns earn a new check.
- The **card UI is in core** (`composer/IdeaCards.svelte`), not the plugin tab: a plugin tab is only loaded once opened,
  and the chat that made the card is closed. The Ideas tab carries an "unsaved" line for the same cards.
- The **ideas file gains `sessions`** (`{ sessionId, title, at, seq?, note?, seen? }`, one entry per session), kept apart
  from the sections: attaching a chat never edits the idea's text. A saved card's new idea starts with its own entry.
- The pending file is `~/.netpi/ideas-pending.json` next to the ideas, atomic, and is never an idea: a card is an offer.
  A file that cannot be read after three tries is left alone rather than overwritten with an empty backlog.
- Skips: fewer than two user turns, subagents, a turn that ended `aborted` or `error`, an unknown session, off.
- The digest is capped at 12k characters (user turns whole, answers and tool names clipped), not the raw transcript.

`ideas.closed` → `{ checked, reason }`; `ideas.suggestions` → the cards; `ideas.resolve { id, action, edit? }` answers one.
Tests: `tests/NetPI.Aux.Tests/IdeasTests.cs` (six cases: attach, card and save, NOTHING/edit, discard and the skips, one
check per message count, the parser).

### Phase 3: close on commit

- A watcher per project with a git repository (`.git/logs/HEAD`, debounced like the ideas file watcher) reads the new
  commits (`git log` since the last hash seen, kept per repository in the pending file).
- Each commit: link decision over the ideas open then (p ≥ `ideas.linkThreshold`) → a `commits` entry on the idea;
  then the done decision on the idea's full text and all its linked commits (p ≥ `ideas.doneThreshold`) → a pending
  suggestion `{ kind: "done", ideaId, commits }` → "mark done?" card. One offer per idea.

## Tests and docs

- `tests/NetPI.Aux.Tests/IdeasTests.cs`: recall (id match, model match above and below the threshold, "none" wins,
  off, no Decide plugin), attach (notice, session recorded), the save check and its cards (fake `decide.*` and a fake
  model responder), the skip rules and the one-check-per-message-count rule. Decide: `decide.decision` against a fake
  server.
- Mock UI (`web/mock/ideas.mjs`) handlers for every new RPC; `web/mock/e2e.mjs` checks for the chip and the card.
- `docs/PROTOCOL.md` (RPC, event), `docs/SETTINGS.md`, `docs/PLUGIN-IDEAS.md`, `docs/UI.md`.
