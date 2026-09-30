// Unsent composer state that must outlive the chat cache.
//
// chat.svelte.js keeps a 5-store LRU over the per-session stores (each a window of messages): open a
// sixth session and the first store is evicted, and whatever lived only on that store dies with it.
// The draft's text already survives that (localStorage, restored when the store is rebuilt); the image
// blobs did not, which is why an unsent attachment vanished when its chat was evicted. The blobs
// therefore live here — app-lifetime, keyed by session — bounded by DRAFT_IMAGES_MAX per draft
// (the newest win) and DRAFT_SESSIONS_MAX drafts overall (the least recently touched go first). They
// stay in memory: a prepared image can be ~1 MB of base64 each, which would eat the ~5 MB localStorage
// quota that the text drafts share.
//
// Lifecycle: every write replaces the whole list. An empty list releases the blobs — send (the
// message keeps its own copy on the server) or discard (the composer was cleared). Rebuilding a
// store (reopening a session after eviction) finds the blobs again; deleting a session drops them.

import { SvelteMap } from 'svelte/reactivity';

// the send envelope (~2 MB, WsHub.MaxMessageBytes) holds at most two full-size images, so six is far
// more than any one message can carry — which bounds a draft's blobs to ~6 MB
export const DRAFT_IMAGES_MAX = 6;
// a draft is something about to be sent; a user with more open drafts than this loses the oldest attachments
export const DRAFT_SESSIONS_MAX = 24;

const drafts = new SvelteMap(); // sessionId -> [{ mediaType, data, url, name }]
const EMPTY = [];

/** The draft's attachments, live: what the composer renders and a rebuilt store restores. */
export function draftImages(id) {
  return drafts.get(id) ?? EMPTY;
}

/** Replace a draft's attachments, keeping the newest DRAFT_IMAGES_MAX; an empty list releases them. */
export function setDraftImages(id, images) {
  put(id, (images ?? []).slice(-DRAFT_IMAGES_MAX));
}

/** The store was rebuilt (the session reopened): keep a used draft ahead of the eviction order. */
export function touchDraft(id) {
  if (drafts.has(id)) put(id, drafts.get(id));
}

/** The session is gone (deleted): release its blobs. */
export function dropDraft(id) {
  put(id, []);
}

function put(id, images) {
  drafts.delete(id);
  if (!images.length) return; // a draft without blobs keeps no slot (its text is cheap in localStorage)
  drafts.set(id, images);
  if (drafts.size > DRAFT_SESSIONS_MAX) {
    for (const k of drafts.keys()) {
      if (drafts.size <= DRAFT_SESSIONS_MAX) break;
      drafts.delete(k); // the first key is the least recently touched
    }
  }
}
