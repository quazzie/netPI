// Open a local path with the operating system (files.open in the files plugin): links to files in chat messages, the
// file of a read/write/edit tool call, the file tree. Relative paths resolve against the session's working directory.
import { rpc } from './rpc.svelte.js';
import { app } from './state/app.svelte.js';
import { toast } from './state/ui.svelte.js';

export async function openFile(path, sessionId = app.activeId) {
  if (!path) return;
  try {
    const res = await rpc('files.open', { path, ...(sessionId ? { sessionId } : {}) });
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
