// The `ctx` object handed to plugin tab modules: mount(el, ctx). See docs/PROTOCOL.md and docs/UI.md.
import { rpc } from './rpc.svelte.js';
import { bus } from './bus.js';
import { app, openSession, newSession, sendMessage, hasRpc, workspaceOf } from './state/app.svelte.js';
import { composer, toast, modals, openView, openIdeaDialog } from './state/ui.svelte.js';
import { openPanelTab } from './state/tabs.svelte.js';

const appListeners = new Set();

/**
 * A session's workspace as the host knows it (app.workspaces): the files.scope it read when the tab was activated, or
 * the session.workspace that bound the session since — two shapes, reduced to the fields a tab keys on. `identity` is
 * the host's own refresh key (workspace id + version, or the project when unbound) and is null until a files.scope
 * answered; a tab that needs it then asks once itself. null while nothing is known.
 */
function workspaceSnapshot(s) {
  const w = workspaceOf(s);
  if (!w) return null;
  return {
    sessionId: w.sessionId ?? s.id,
    workspaceId: w.workspaceId ?? w.binding?.workspaceId ?? null,
    identity: w.identity ?? null,
    version: w.version ?? w.binding?.version ?? 0,
    root: w.root ?? w.binding?.root ?? w.cwd ?? null,
    branch: w.branch ?? w.binding?.branch ?? null,
  };
}

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
      // the active session's workspace as the host knows it (see workspaceSnapshot): what a tab keys a reload on
      // without a files.scope round trip of its own
      get activeWorkspace() {
        return workspaceSnapshot(app.activeSession);
      },
      /** A session's title from the host's own session list (reactive: a $derived reading it follows session.updated), or null when the host does not list it. */
      sessionTitle: (id) => (id ? (app.sessionsById.get(id)?.title ?? null) : null),
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
