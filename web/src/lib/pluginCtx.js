// The `ctx` object handed to plugin tab modules: mount(el, ctx). See docs/PROTOCOL.md and docs/UI.md.
import { rpc } from './rpc.svelte.js';
import { bus } from './bus.js';
import { app, openSession, newSession, sendMessage, hasRpc } from './state/app.svelte.js';
import { composer, toast, modals, openView, openIdeaDialog } from './state/ui.svelte.js';
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
 * Create a ctx for one mounted tab. A session view (panel "session") also gets the chat it shows: ctx.sessionId. Everything registered through it (event handlers, onChange
 * listeners) is released by dispose() when the tab unmounts or reloads, even if the plugin forgets.
 * Registering after that releases it at once and hands back a no-op, so a handler that arrives from
 * a pending await (an onMount that crossed one) cannot outlive the tab it was registered for.
 */
export function createPluginCtx(tab, sessionId = null) {
  const disposers = new Set();
  let disposed = false;
  const track = (off) => {
    if (disposed) {
      off();
      return () => {};
    }
    disposers.add(off);
    return () => {
      if (disposers.delete(off)) off();
    };
  };

  const ctx = {
    pluginId: tab.pluginId,
    tabId: tab.id,
    sessionId,
    rpc: (method, params) => rpc(method, params ?? {}),
    hasRpc,
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
      // A new chat (in opts.projectId's project, if any) that starts at once on `text`.
      startChat: async (opts) => {
        const s = await newSession({ projectId: opts?.projectId ?? null, title: opts?.title });
        if (s) await sendMessage(s.id, String(opts?.text ?? ''), [], 'auto');
      },
      insertText: (text) => {
        if (composer.insertText) composer.insertText(String(text ?? ''));
        else toast('Open a session first', 'warn');
      },
      openTab: (key) => {
        openPanelTab(key);
      },
      /** Show a session view in a chat (null key: the chat's messages again). */
      openView: (sid, key) => {
        openView(sid, key);
      },
      // The idea dialog (new, or { idea } to edit one; { projectId } says where a new one goes, { refine: true } opens with the
      // agent task on). The same dialog Ctrl+I opens: the host owns it so it is there whichever tab is showing.
      openIdea: (opts) => {
        openIdeaDialog(opts ?? {});
      },
      openSettings: (page, target) => {
        // target (an agent id, say) rides along in the page string: 'agents:<id>' opens that agent's dialog
        modals.settings = page ? (target ? `${page}:${target}` : page) : true;
      },
      toast: (text, level = 'info') => {
        toast(text, level);
      },
    }),
  };

  return {
    ctx: Object.freeze(ctx),
    dispose() {
      disposed = true;
      for (const off of disposers) {
        try {
          off();
        } catch {}
      }
      disposers.clear();
    },
  };
}
