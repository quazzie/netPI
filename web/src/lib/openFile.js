// Open a local path with the operating system (files.open in the files plugin): links to files in chat messages, the
// file of a read/write/edit tool call. Relative paths resolve against the session's working directory.
import { rpc } from './rpc.svelte.js';
import { app } from './state/app.svelte.js';
import { confirmDialog, toast } from './state/ui.svelte.js';

/**
 * `ask` is for a path the model wrote into a message: a link is text until it is clicked, and clicking it runs whatever
 * program the OS associates with it (an executable included), so the user is asked first. A button they pressed ("Open
 * file" on a read/write/edit row) is already deliberate and skips that question. Either way a path outside the session's
 * workspace is asked about separately below.
 */
export async function openFile(path, sessionId = app.activeId, { ask = false } = {}) {
  if (!path) return;
  const call = (confirm) => rpc('files.open', { path, ...(sessionId ? { sessionId } : {}), user: true, ...(confirm ? { confirm: true } : {}) });
  try {
    if (ask) {
      const go = await confirmDialog({
        title: 'Open this file?',
        message: `${path}\n\nIt opens with its default program — an executable or a script runs.`,
        confirmLabel: 'Open',
        danger: true,
      });
      if (!go) return;
    }
    let res = await call(false);
    // A path outside the session's workspace (a link the model wrote to somewhere else on the machine) is not opened
    // until the user says so; the host answers 'confirm' and opens nothing itself.
    if (res?.action === 'confirm') {
      const ok = await confirmDialog({
        title: 'Open a file outside the workspace?',
        message: `${res.path}\n\nThis session works somewhere else, so opening it is your call.`,
        confirmLabel: 'Open',
        danger: true,
      });
      if (!ok) return;
      await call(true);
    }
  } catch (e) {
    toast(e.message, e.code === 'not_found' ? 'warn' : 'error');
  }
}

let installed = false;
/** One delegated click handler for the file links renderMarkdown emits (a.file-link[data-path]). The path came from a
 * message, so the user is asked before it opens. */
export function installFileLinks() {
  if (installed) return;
  installed = true;
  document.addEventListener('click', (e) => {
    const a = e.target.closest?.('a.file-link[data-path]');
    if (!a) return;
    e.preventDefault();
    openFile(a.dataset.path, undefined, { ask: true });
  });
}
