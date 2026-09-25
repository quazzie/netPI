// The `ctx` object handed to plugin tab modules: mount(el, ctx). See docs/PROTOCOL.md and docs/UI.md.
import { rpc } from './rpc.svelte.js';
import { bus } from './bus.js';
import { app, openSession, newSession } from './state/app.svelte.js';
import { composer, toast, modals } from './state/ui.svelte.js';
import { openPanelTab } from './state/tabs.svelte.js';

const appListeners = new Set();

/** Called by the App whenever the active session / project changes. */
export function notifyAppChange() {
  for (const fn of appListeners) {
    try {
      fn();
    } catch (e) {
      console.error('[plugin] onChange handler failed', e);
    }
  }
}

const plain = (v) => (v == null ? null : JSON.parse(JSON.stringify(v)));

/**
 * Create a ctx for one mounted tab. Everything registered through it (event handlers, onChange
 * listeners) is released by dispose() when the tab unmounts or reloads, even if the plugin forgets.
 */
export function createPluginCtx(tab) {
  const disposers = new Set();
  const track = (off) => {
    disposers.add(off);
    return () => {
      if (disposers.delete(off)) off();
    };
  };

  const ctx = {
    pluginId: tab.pluginId,
    tabId: tab.id,
    rpc: (method, params) => rpc(method, params ?? {}),
    on: (pattern, handler) => track(bus.on(pattern, handler)),
    app: Object.freeze({
      get activeSessionId() {
        return app.activeId;
      },
      get activeSession() {
        return plain(app.activeSession);
      },
      get activeProject() {
        return plain(app.activeProject);
      },
      onChange(cb) {
        appListeners.add(cb);
        return track(() => appListeners.delete(cb));
      },
      openSession: (id) => {
        openSession(id);
      },
      newSession: async (opts) => {
        await newSession(opts ?? {});
      },
      insertText: (text) => {
        if (composer.insertText) composer.insertText(String(text ?? ''));
        else toast('Open a session first', 'warn');
      },
      openTab: (key) => {
        openPanelTab(key);
      },
      openSettings: (page) => {
        modals.settings = page || true;
      },
      toast: (text, level = 'info') => {
        toast(text, level);
      },
    }),
  };

  return {
    ctx: Object.freeze(ctx),
    dispose() {
      for (const off of disposers) {
        try {
          off();
        } catch {}
      }
      disposers.clear();
    },
  };
}
