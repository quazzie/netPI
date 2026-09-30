/**
 * The cards a closed chat leaves behind (plugins/NetPI.Ideas, phase 2 of docs/plans/2026-09-27-ideas-follow-the-session.md):
 * a plan, feature idea or research question that the check found nobody built or wrote down. They wait in
 * ~/.netpi/ideas-pending.json, so they are here after a restart too, and they are shown above the composer of whatever
 * chat is open — the chat they came from is closed, and unscoped events reach every window.
 */
import { SvelteMap } from 'svelte/reactivity';
import { rpc } from '../../lib/rpc.svelte.js';

class Suggestions {
  /** id → { id, kind, sessionId, sessionTitle, title, summary, at, project } */
  items = new SvelteMap();
  enabled = $state(true);
  loaded = $state(false);
  busy = $state(null); // the card being answered

  /** Read once per window; a later card arrives by the event (app.svelte.js routes ideas.suggested here). */
  async load() {
    if (this.loaded || !this.enabled) return;
    this.loaded = true;
    try {
      const res = await rpc('ideas.suggestions', {});
      for (const s of res?.suggestions ?? []) this.items.set(s.id, s);
    } catch (e) {
      if (/unknown method/i.test(e.message ?? '')) this.enabled = false;
      this.loaded = false; // a transport failure is worth one more try
    }
  }

  /** ideas.suggested: a card was made by a chat that closed in some window. */
  event(d) {
    const s = d?.suggestion;
    if (s?.id) this.items.set(s.id, s);
  }

  /**
   * ideas.resolved: a card is answered (in this or another window). Dropping it
   * here is what keeps a second window from showing a card that cannot be answered any more.
   */
  resolved(d) {
    if (d?.id) this.items.delete(d.id);
  }

  /** Re-read the cards: after a reconnect, or when this window becomes visible again. */
  async refresh() {
    if (!this.enabled) return;
    try {
      const res = await rpc('ideas.suggestions', {});
      const fresh = res?.suggestions ?? [];
      const seen = new Set(fresh.map((s) => s.id));
      for (const id of [...this.items.keys()]) if (!seen.has(id)) this.items.delete(id);
      for (const s of fresh) this.items.set(s.id, s);
      this.loaded = true;
    } catch { /* no Ideas plugin, or a storage that cannot be read; the next event or the next visibility tries again */ }
  }

  /** Save (with the user's edit) or discard. Discard is final for that card: it is answered and gone either way. */
  async resolve(id, action, edit) {
    if (this.busy) return;
    this.busy = id;
    try {
      const res = await rpc('ideas.resolve', { id, action, ...(edit ? { edit } : {}) });
      this.items.delete(id);
      return res?.saved ?? null;
    } finally {
      this.busy = null;
    }
  }
}

export const suggestions = new Suggestions();
