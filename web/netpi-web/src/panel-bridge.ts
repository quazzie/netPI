// astra-2 §12.3: the shell↔panel navigation bridge.
//
// The Work panel is a cross-origin iframe (its own Kestrel port). It may ask
// the shell to open a session, but ONLY the CURRENTLY MOUNTED panel iframe is
// ever trusted:
//   - the shell registers the live iframe's contentWindow + entry URL here
//     when the panel mounts (RightPanel.svelte) and clears it on unmount;
//   - App.svelte accepts an openSession message ONLY when `event.source` is
//     this registered window AND `event.origin` equals the registered panel's
//     entry-URL origin — a matching type string from any other origin, a stale
//     (unmounted) frame, or an unregistered origin is rejected;
//   - the shell's origin reaches the page through a one-time init handshake
//     (postMessage to the registered frame), never "*" and never a hardcoded
//     port; the page then echoes navigation back to that exact origin.
//
// Navigation uses the existing session.open machinery (ws.openSession); a
// deleted/unknown session surfaces a visible notice (store.panelNavNotice) —
// the shell never silently navigates elsewhere.

import { store } from "./store.svelte";
import { ws } from "./ws";

export interface ActivePanel {
  win: Window;
  entryUrl: string;
  origin: string;
}

let activePanel: ActivePanel | null = null;

/** Register (or replace) the live panel iframe; pass null when it unmounts. */
export function setActivePanel(panel: ActivePanel | null): void {
  activePanel = panel;
}

export function getActivePanel(): ActivePanel | null {
  return activePanel;
}

/**
 * Handle one window "message" from the panel world. Returns true when the
 * message was accepted (and handled). Both the current envelope
 * (netpi.panel.openSession, version 1) and the legacy Activity envelope
 * (netpi.activity.openSession — deprecated, see docs/web-panels.md) are
 * accepted, with the SAME source/origin checks.
 */
export function handlePanelMessage(e: MessageEvent): boolean {
  const d = e.data;
  if (!d || typeof d !== "object") return false;
  const type = typeof d.type === "string" ? d.type : null;
  if (type !== "netpi.panel.openSession" && type !== "netpi.activity.openSession")
    return false;

  // Shape: version must be 1 for the generic envelope; payload.sessionId a
  // bounded string (an oversized or empty id is a shape violation, rejected).
  if (type === "netpi.panel.openSession" && d.version !== 1) return false;
  const payload = d.payload;
  const sessionId =
    payload && typeof payload === "object" && typeof payload.sessionId === "string"
      ? payload.sessionId
      : null;
  if (!isValidSessionId(sessionId)) return false;

  // Frame + origin checks: only the mounted panel iframe, from its registered
  // origin. Stale/unmounted frames and unregistered origins are rejected.
  const panel = activePanel;
  if (!panel || e.source !== panel.win || e.origin !== panel.origin) return false;

  navigateToPanelSession(sessionId!);
  return true;
}

/** Bounded session-id shape check (ids are server-generated, short, no whitespace). */
export function isValidSessionId(id: string | null): boolean {
  if (!id || id.length > 128) return false;
  return !/\s/.test(id);
}

/**
 * Open the session through the existing ws.openSession machinery. A deleted or
 * unknown session must NOT silently navigate elsewhere — surface a small
 * visible notice instead.
 */
export async function navigateToPanelSession(sessionId: string): Promise<void> {
  const known = store.sessions.some((s) => s.id === sessionId) ||
    store.openTabIds.includes(sessionId) ||
    store.session?.id === sessionId;
  try {
    await ws.openSession(sessionId);
  } catch (_) {
    if (known) {
      store.setError("Could not open the session — try again.");
    } else {
      store.setPanelNavNotice(`Session "${sessionId.slice(0, 12)}…" does not exist or was deleted.`);
    }
    return;
  }
  // The open landed; if the drawer doesn't know the session yet it will be
  // upserted by the session.updated/created events. Give the tab a home.
  if (!known && !store.openTabIds.includes(sessionId)) {
    store.openTab(sessionId);
  }
  store.setPanelNavNotice(null);
}
