// Pure session-navigation state (no Svelte, no DOM, no WebSocket) so the tab
// transitions can be unit-tested directly (see nav-state.test.ts).
//
// The ONE rule this enforces: the UI owns navigation. Server events never open
// or select a tab unless they are the direct result of an explicit UI
// navigation/create operation. The store delegates to these functions and
// applies the result to its $state fields; ws.ts only performs the network
// `session.open` the resulting action asks for.
//
// Model:
//   open session IDs (ordered)
//   selected = existing session ID | new-session draft (draft != null)

export interface NewDraftMeta {
  /** The session workspace a first prompt should be created in. */
  workspace: string | null;
  /** The APPLIED project of the session the draft started from (a pending,
   *  not-yet-effective project switch is deliberately NOT inherited). */
  projectId: string | null;
  projectName: string | null;
}

export interface NavState {
  openIds: string[];
  /** null = no tab selected: either a new-session draft is active (draft !=
   *  null) or the viewport is empty. */
  selected: string | null;
  /** Set exactly when the UI is in "New Session" mode. */
  draft: NewDraftMeta | null;
}

/** What the caller (ws.ts) must do after applying the new state: fetch the
 *  transcript of `id` via an explicit session.open, or nothing. */
export type NavAction = { kind: "open"; id: string } | { kind: "none" };

function clone(s: NavState): NavState {
  return { openIds: [...s.openIds], selected: s.selected, draft: s.draft ? { ...s.draft } : null };
}

function dropTab(s: NavState, id: string): void {
  const i = s.openIds.indexOf(id);
  if (i >= 0) s.openIds.splice(i, 1);
  if (s.selected === id) {
    s.selected = null;
    s.draft = null; // resolved by the caller's transition
  }
}

/**
 * Explicit navigation to an existing session (tab click, picker row,
 * restore-after-load). Registers the id as an open tab, selects it, and drops
 * any draft. Returns the open action so the caller fetches the transcript.
 */
export function openTab(s: NavState, id: string): [NavState, NavAction] {
  const n = clone(s);
  if (!n.openIds.includes(id)) n.openIds.push(id);
  n.draft = null;
  n.selected = id;
  return [n, { kind: "open", id }];
}

/**
 * Close a tab. The run and the stored session are untouched (closing a tab is
 * a view operation, not a cancel/delete). When the closed tab was selected,
 * select the NEAREST remaining tab — the one to its right when there is one,
 * otherwise the one to its left — and only enter New Session mode when no
 * tabs remain. Returns the session to open (or none, including for the
 * just-entered draft).
 */
export function closeTab(s: NavState, id: string): [NavState, NavAction] {
  const n = clone(s);
  const idx = n.openIds.indexOf(id);
  if (idx < 0) return [n, { kind: "none" }];
  n.openIds.splice(idx, 1);
  if (n.selected === id) {
    if (n.openIds.length > 0) {
      const i = Math.min(idx, n.openIds.length - 1);
      n.selected = n.openIds[i] ?? n.openIds[Math.max(0, idx - 1)];
      n.draft = null;
      return [n, { kind: "open", id: n.selected }];
    }
    n.selected = null;
    n.draft = { workspace: null, projectId: null, projectName: null }; // caller enriches
    return [n, { kind: "none" }];
  }
  return [n, { kind: "none" }];
}

/**
 * Explicit "New Session": a LOCAL draft — no database session exists yet.
 * The first real prompt creates the server session (chat.send with no
 * sessionId), so clicking New is instant and abandoned conversations never
 * leave empty rows. Idempotent: while a draft is active, the draft's own
 * captured context is kept (hammering New never resets it, never creates
 * anything server-side).
 */
export function startNew(s: NavState, from: NewDraftMeta): [NavState, NavAction] {
  const n = clone(s);
  n.draft = s.draft ? s.draft : { ...from };
  n.selected = null;
  return [n, { kind: "none" }];
}

/**
 * The server reported a session (id) that no longer exists (delete or a stale
 * persisted tab id). Removes it from the open tabs; the selected-session
 * transition is exactly the closeTab one (nearest tab, else draft).
 */
export function sessionNotFound(s: NavState, id: string): [NavState, NavAction] {
  if (!s.openIds.includes(id)) {
    const n = clone(s);
    // Still record a selected-session loss when the visible session id is gone.
    if (s.selected === id) {
      n.selected = null;
      n.draft = { workspace: null, projectId: null, projectName: null };
    }
    return [n, { kind: "none" }];
  }
  return closeTab(s, id);
}

/** A session was deleted server-side while open: same transition as closeTab. */
export function sessionDeleted(s: NavState, id: string): [NavState, NavAction] {
  return closeTab(s, id);
}

/**
 * The client's own draft first-prompt just created the server session.
 * Replaces the local draft placeholder with the real tab — the ONLY place a
 * server-created session may become visible. (Another client's creation is
 * NOT adopted: the pending-send flag distinguishes it, ws-side.)
 */
export function adoptCreated(s: NavState, id: string): [NavState, NavAction] {
  if (!s.draft) return [clone(s), { kind: "none" }];
  const n = clone(s);
  n.draft = null;
  if (!n.openIds.includes(id)) n.openIds.push(id);
  n.selected = id;
  return [n, { kind: "none" }]; // transcript already flows in from the live events
}

/**
 * Cold-start restore order: the persisted selected tab first (when it is a
 * persisted open tab), then the remaining open tabs. Stale ids are trimmed by
 * sessionNotFound as each open attempt fails server-side.
 */
export function restoreOrder(tabs: string[], selected: string | null): string[] {
  const out: string[] = [];
  const seen = new Set<string>();
  const push = (id: string | null | undefined) => {
    if (id && !seen.has(id)) {
      seen.add(id);
      out.push(id);
    }
  };
  if (selected && tabs.includes(selected)) push(selected);
  for (const t of tabs) push(t);
  return out;
}
