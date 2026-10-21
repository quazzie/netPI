// The settings document, fetched once for every dialog that shows it (Settings, the agent dialog). app.settingsVersion
// is the one change signal; follow() (called in a component $effect) reloads when the loaded copy is older than it.
import { untrack } from 'svelte';
import { app } from './app.svelte.js';
import { rpc } from '../rpc.svelte.js';

class SettingsDoc {
  doc = $state.raw({});
  path = $state('');
  /** the app.settingsVersion the doc was loaded for */
  version = $state(-1);
  loaded = $state(false);
  #inflight = null;

  /** Fetch settings.get (concurrent calls share one request; refetches if the version moved meanwhile). */
  refresh() {
    if (this.#inflight) return this.#inflight;
    this.#inflight = (async () => {
      try {
        for (;;) {
          const v = app.settingsVersion;
          try {
            const res = await rpc('settings.get');
            this.doc = res?.settings ?? {};
            this.path = res?.path ?? '';
            this.version = v;
            this.loaded = true;
          } catch {
            return; // keep the previous doc
          }
          if (app.settingsVersion === v) return;
        }
      } finally {
        this.#inflight = null;
      }
    })();
    return this.#inflight;
  }

  /** Call inside a $effect: re-runs when app.settingsVersion changes and reloads a stale or missing doc. */
  follow() {
    const v = app.settingsVersion;
    untrack(() => {
      if (!this.loaded || this.version !== v) this.refresh();
    });
  }
}

export const settingsDoc = new SettingsDoc();
