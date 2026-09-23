import test from "node:test";
import assert from "node:assert/strict";
import {
  openTab,
  closeTab,
  startNew,
  sessionDeleted,
  sessionNotFound,
  adoptCreated,
  restoreOrder,
  type NavState,
} from "./nav-state.ts";

const s = (openIds: string[], selected: string | null, draft = null): NavState =>
  ({ openIds, selected, draft });

// ---- restore (item 8: the cold-start race) ------------------------------------

test("restore: persisted [A,B] selected B — server's recent C is never in the order", () => {
  // The server may replay its most recent session (C) during bootstrap; the
  // restore order is built ONLY from the persisted tabs, so C can't appear.
  assert.deepEqual(restoreOrder(["A", "B"], "B"), ["B", "A"]);
});

test("restore: selected not among tabs — plain tab order", () => {
  assert.deepEqual(restoreOrder(["A", "B", "C"], null), ["A", "B", "C"]);
});

test("restore: stale selected id is first — its failed open trims it, then B follows", () => {
  // persisted selected=A (deleted since), tabs=[A,B]: order tries A first;
  // the failed open drives sessionNotFound(A) → selection moves to B.
  const order = restoreOrder(["A", "B"], "A");
  assert.deepEqual(order, ["A", "B"]);
  const [after, action] = sessionNotFound(s(["A", "B"], "A"), "A");
  assert.equal(after.selected, "B");
  assert.deepEqual(action, { kind: "open", id: "B" });
});

test("restore: all tabs stale — draft, not a server-created fallback", () => {
  let st = s(["A", "B"], null);
  let [st1] = sessionNotFound(st, "A");
  let [st2] = sessionNotFound(st1, "B");
  // no tabs left: the restore loop calls startNewSession — a LOCAL draft.
  const [st3, act] = startNew(st2, { workspace: null, projectId: null, projectName: null });
  assert.equal(st3.selected, null);
  assert.ok(st3.draft !== null);
  assert.deepEqual(act, { kind: "none" });
});

// ---- close transitions ---------------------------------------------------------

test("close selected middle tab — selects the RIGHT neighbor", () => {
  const [n, action] = closeTab(s(["A", "B", "C"], "B"), "B");
  assert.equal(n.selected, "C");
  assert.deepEqual(action, { kind: "open", id: "C" });
  assert.deepEqual(n.openIds, ["A", "C"]);
});

test("close selected rightmost tab — falls back to the LEFT neighbor", () => {
  const [n] = closeTab(s(["A", "B", "C"], "C"), "C");
  assert.equal(n.selected, "B");
});

test("close selected FIRST tab — right neighbor is selected", () => {
  const [n] = closeTab(s(["A", "B", "C"], "A"), "A");
  assert.equal(n.selected, "B");
});

test("close a background tab — selection untouched", () => {
  const [n, action] = closeTab(s(["A", "B", "C"], "B"), "A");
  assert.equal(n.selected, "B");
  assert.deepEqual(action, { kind: "none" });
  assert.deepEqual(n.openIds, ["B", "C"]);
});

test("close the last tab — enters New Session mode (draft)", () => {
  const [n, action] = closeTab(s(["A"], "A"), "A");
  assert.equal(n.selected, null);
  assert.ok(n.draft !== null);
  assert.deepEqual(action, { kind: "none" });
});

// ---- new session draft ---------------------------------------------------------

test("startNew from a session captures the APPLIED project + root", () => {
  const [n] = startNew(s(["A"], "A"), {
    workspace: "C:\\work\\netpi",
    projectId: "p1",
    projectName: "netPI",
  });
  assert.equal(n.selected, null);
  assert.deepEqual(n.draft, { workspace: "C:\\work\\netpi", projectId: "p1", projectName: "netPI" });
  assert.deepEqual(n.openIds, ["A"]); // the old tab stays open
});

test("hammering New twice — idempotent draft, nothing server-side is ever named", () => {
  const [a] = startNew(s([], null), { workspace: "w", projectId: "p", projectName: "P" });
  const [b] = startNew(a, { workspace: "w2", projectId: "p2", projectName: "P2" });
  assert.deepEqual(b.draft, a.draft); // the second click keeps the first capture
  assert.equal(b.draft!.projectId, "p");
});

test("startNew drops an active draft only via a real session open", () => {
  const [a] = startNew(s([], null), { workspace: "w", projectId: "p", projectName: "P" });
  const [b] = openTab(a, "A");
  assert.equal(b.draft, null);
  assert.equal(b.selected, "A");
});

// ---- deletions ------------------------------------------------------------------

test("sessionDeleted on a background tab — selection untouched", () => {
  const [n, action] = sessionDeleted(s(["A", "B", "C"], "B"), "A");
  assert.equal(n.selected, "B");
  assert.deepEqual(action, { kind: "none" });
});

test("sessionDeleted on the selected tab — same transition as close", () => {
  const [n, action] = sessionDeleted(s(["A", "B", "C"], "B"), "B");
  assert.equal(n.selected, "C");
  assert.deepEqual(action, { kind: "open", id: "C" });
});

test("sessionNotFound for a non-tab id — selection loss still transitions to draft", () => {
  const [n] = sessionNotFound(s(["A"], "A"), "A");
  // 'A' IS a tab here — behaves exactly like closeTab (draft on last tab).
  assert.equal(n.selected, null);
  assert.ok(n.draft !== null);
});

// ---- draft adoption -------------------------------------------------------------

test("adoptCreated replaces the local draft with the real tab", () => {
  const [a] = startNew(s([], null), { workspace: "w", projectId: "p", projectName: "P" });
  const [n, action] = adoptCreated(a, "X");
  assert.equal(n.selected, "X");
  assert.equal(n.draft, null);
  assert.deepEqual(n.openIds, ["X"]);
  assert.deepEqual(action, { kind: "none" }); // live events already flow the transcript
});

test("adoptCreated without an active draft is a no-op (another client's creation)", () => {
  const [n, action] = adoptCreated(s(["A"], "A"), "X");
  assert.equal(n.selected, "A");
  assert.deepEqual(n.openIds, ["A"]);
  assert.deepEqual(action, { kind: "none" });
});
