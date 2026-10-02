// Folder picker. In the desktop shell (WebView2) the native dialog is used through
// chrome.webview.postMessage({ type: 'pickFolder', id, initial }) → { type: 'pickFolderResult', id, path };
// in a plain browser a modal browses the host filesystem with fs.dirs.
import { modals } from './state/ui.svelte.js';

let seq = 0;

export function hasNativePicker() {
  return !!window.chrome?.webview;
}

/** Resolves to the chosen absolute path, or null when cancelled. */
export function pickFolder({ initial = null, title = 'Choose a folder' } = {}) {
  const wv = window.chrome?.webview;
  if (wv) {
    return new Promise((resolve) => {
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
        if (d?.type !== 'pickFolderResult' || d.id !== id) return;
        wv.removeEventListener('message', onMsg);
        resolve(d.path || null);
      };
      wv.addEventListener('message', onMsg);
      wv.postMessage({ type: 'pickFolder', id, initial });
    });
  }
  return new Promise((resolve) => {
    modals.folder?.resolve?.(null); // a second picker replaces the first: the first caller's answer is 'nothing chosen'
    modals.folder = { initial, title, resolve };
  });
}
