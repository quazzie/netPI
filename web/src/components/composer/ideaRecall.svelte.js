/**
 * Per-chat state of the idea chip (IdeaChip.svelte), kept across tab switches: the match, whether the user added or
 * dismissed it (then the chat is not asked about again), the last text checked, and whether this window saw the
 * chat empty (only then is the sent first message checked: opening an old chat never asks).
 */
class ChatRecall {
  match = $state(null); // { id, title, p }
  done = $state(null); // 'added' | 'dismissed'
  seq = 0;
  lastText = null;
  sawEmpty = false;
  finalAsked = false;
}

class Recall {
  enabled = $state(true); // false after "off" from the server, or without an Ideas plugin
  #chats = new Map();
  #at = new Map(); // sessionId -> last touch (ms): the age used by the bounds below

  // A ChatRecall is small, but one per session ever opened is still a leak for a window left open: the map is
  // capped, and entries untouched for a week are dropped on the next touch. Trade-off: in that case reopening
  // an old chat can re-ask its idea chip (a match it had dismissed), instead of the map growing without bound.
  static MAX = 48;
  static WEEK = 7 * 24 * 3600 * 1000;

  get(sessionId) {
    const now = Date.now();
    this.#at.set(sessionId, now);
    let s = this.#chats.get(sessionId);
    if (!s) {
      s = new ChatRecall();
      this.#chats.set(sessionId, s);
      for (const id of [...this.#at.keys()]) if (now - this.#at.get(id) > Recall.WEEK) this.#drop(id);
      while (this.#chats.size > Recall.MAX) {
        let oldest = null;
        for (const [id, t] of this.#at) if (oldest === null || t < oldest[1]) oldest = [id, t];
        if (!oldest || oldest[0] === sessionId) break; // the session just opened is never evicted on its own touch
        this.#drop(oldest[0]);
      }
    }
    return s;
  }

  #drop(id) {
    this.#chats.delete(id);
    this.#at.delete(id);
  }

  /** The session went: forget its state (the map would otherwise keep one ChatRecall per session ever opened). */
  prune(sessionId) {
    this.#drop(sessionId);
  }

  get size() {
    return this.#chats.size;
  }

  keys() {
    return this.#chats.keys();
  }
}

export const recall = new Recall();
