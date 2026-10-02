// Open a local path with the operating system (files.open in the files plugin): links to files in chat messages, the
// file of a read/write/edit tool call, the file tree. Relative paths resolve against the session's working directory.
import { rpc } from './rpc.svelte.js';
import { app } from './state/app.svelte.js';
import { confirmDialog, toast } from './state/ui.svelte.js';

export async function openFile(path, sessionId = app.activeId) {
  if (!path) return;
  const call = (confirm) => rpc('files.open', { path, ...(sessionId ? { sessionId } : {}), ...(confirm ? { confirm: true } : {}) });
  try {
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
      res = await call(true);
    }
    if (res?.action === 'reveal') toast(`Shown in the file manager: ${res.path}`);
  } catch (e) {
    toast(e.message, e.code === 'not_found' ? 'warn' : 'error');
  }
}

let installed = false;
/** One delegated click handler for the file links renderMarkdown emits (a.file-link[data-path]). */
export function installFileLinks() {
  if (installed) return;
  installed = true;
  document.addEventListener('click', (e) => {
    const a = e.target.closest?.('a.file-link[data-path]');
    if (!a) return;
    e.preventDefault();
    openFile(a.dataset.path);
  });
}
