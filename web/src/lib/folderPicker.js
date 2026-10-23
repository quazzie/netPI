// Folder and file pickers. In the desktop shell (WebView2) the native dialog is used through
// chrome.webview.postMessage({ type: 'pickFolder' | 'pickFile', id, initial }) → { type: 'pickFolderResult' | 'pickFileResult', id, path };
// in a plain browser a modal browses the host filesystem with fs.dirs (with files: true for a file).
import { modals } from './state/ui.svelte.js';

let seq = 0;
let nativePick = null; // the native pick in flight: { settle } removes its listener and resolves it with null

/** Resolves to the chosen absolute path, or null when cancelled. */
export function pickFolder({ initial = null, title = 'Choose a folder' } = {}) {
  return pick('Folder', initial, title);
}

/** The same for a file: resolves to the chosen file's absolute path, or null. */
export function pickFile({ initial = null, title = 'Choose a file' } = {}) {
  return pick('File', initial, title);
}

function pick(kind, initial, title) {
  const wv = window.chrome?.webview;
  if (wv) {
    return new Promise((resolve) => {
      nativePick?.settle(); // a second picker replaces the first: its listener goes and its caller hears 'nothing chosen'
      const id = `pf${Date.now().toString(36)}${++seq}`;
      const onMsg = (e) => {
        let d = e.data;
        if (typeof d === 'string') {
          try {
            d = JSON.parse(d);
          } catch {
            return;
          }
        }
        if (d?.type !== `pick${kind}Result` || d.id !== id) return;
        finish(d.path || null);
      };
      const me = { settle: () => finish(null) };
      const finish = (path) => {
        wv.removeEventListener('message', onMsg);
        if (nativePick === me) nativePick = null;
        resolve(path);
      };
      nativePick = me;
      wv.addEventListener('message', onMsg);
      wv.postMessage({ type: `pick${kind}`, id, initial, title });
    });
  }
  return new Promise((resolve) => {
    modals.folder?.resolve?.(null); // a second picker replaces the first: the first caller's answer is 'nothing chosen'
    modals.folder = { initial, title, resolve, files: kind === 'File' };
  });
}
