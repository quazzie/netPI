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

  get(sessionId) {
    let s = this.#chats.get(sessionId);
    if (!s) {
      s = new ChatRecall();
      this.#chats.set(sessionId, s);
    }
    return s;
  }
}

export const recall = new Recall();
