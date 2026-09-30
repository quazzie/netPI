// e2e only (web/mock/e2e.mjs): when the page is connected to the mock server, the app reports the sizes of
// its client caches to the mock (mock.cacheReport, every 250 ms) and exposes synchronous readers on
// window.__netpiProbe. A check then reads back what the app currently holds (mock.caches on the server keeps
// the latest report, tagged with this page's client id) and can drive the per-session caches at unit speed
// (touchRecall / touchSkills) instead of opening a tab for each. The mock identifies itself in the
// WebSocket hello (version "0.1.0-mock"; the page's own token is unreadable — the mock sets it as an
// HttpOnly cookie, like the host), so with any real server the probe stays off: no reporting, no window
// handle, and none of the cache code below runs.
import { rpc, conn } from './rpc.svelte.js';
import { asks, askSeen } from './state/asks.svelte.js';
import { allChats } from './state/chat.svelte.js';
import { app, saidJustNow } from './state/app.svelte.js';
import { recall } from '../components/composer/ideaRecall.svelte.js';
import { skillCommands, cache as skillCache } from './skills.js';

export function installCacheProbe() {
  let snapshot = null;
  const iv = setInterval(() => {
    if (!snapshot) {
      if (conn.version === null) return; // still waiting for the hello
      if (conn.version !== '0.1.0-mock') {
        clearInterval(iv);
        return;
      }
      snapshot = () => ({
        at: Date.now(),
        clientId: conn.clientId,
        asksClosed: asks.closed.size,
        asksCleared: asks.cleared.size,
        askSeen: { ...askSeen },
        saidJustNow: { size: saidJustNow.size, sids: [...saidJustNow.keys()] },
        recall: { size: recall.size, sids: [...recall.keys()] },
        skills: { size: skillCache.size, sids: [...skillCache.keys()] },
        live: Object.fromEntries([...allChats()].map((c) => [c.id, c.live.size])),
      });
      globalThis.__netpiProbeApp = app;
      globalThis.__netpiProbe = {
        caches: snapshot,
        touchRecall: (sid) => recall.get(sid),
        touchSkills: (sid) => skillCommands(sid),
      };
    }
    rpc('mock.cacheReport', snapshot()).catch(() => {});
  }, 250);
}
