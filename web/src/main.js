import './styles/tokens.css';
import './styles/kit.css';
import './styles/app.css';
import { mount } from 'svelte';
import * as svelte from 'svelte';
import * as svelteClient from 'svelte/internal/client';
import * as svelteReactivity from 'svelte/reactivity';
import * as kit from './lib/kit/index.js';
import { initToken } from './lib/auth.js';
import { icons } from './lib/icons.js';
import { renderMarkdown, highlight, installCodeCopy, copyText } from './lib/markdown.js';
import { installFileLinks } from './lib/openFile.js';
import { confirmDialog } from './lib/state/ui.svelte.js';
import { app as appState } from './lib/state/app.svelte.js';
import App from './App.svelte';

initToken();

// Services shared with plugin bundles through @netpi/kit (see lib/kit/host.js). The whole Svelte runtime and the kit
// go with them (idea-3pbkvg): a plugin tab bundle is built against shims that read them from here, so the page has one
// reactive system and a tab carries no copy of either. A bundle built against another svelte says so and fails to open.
globalThis.__netpiHost = Object.freeze({
  version: 1,
  icon: (name) => icons[name] ?? icons.puzzle,
  renderMarkdown: (text) => renderMarkdown(text),
  highlight: (node, enabled) => highlight(node, enabled),
  confirm: (opts) => confirmDialog(opts ?? {}),
  copyText: (text) => copyText(text),
  svelte: { version: __SVELTE_VERSION__, svelte, client: svelteClient, reactivity: svelteReactivity },
  kit,
});

installCodeCopy();
installFileLinks();

// Connect mode: the desktop shell (NetPI.exe --connect) tells the page it is a remote client — the file features work
// on the server's files (the shell downloads them and opens the local copies), and no native dialog is ever shown.
if (window.chrome?.webview) {
  window.chrome.webview.addEventListener('message', (e) => {
    let d = e.data;
    if (typeof d === 'string') {
      try {
        d = JSON.parse(d);
      } catch {
        return;
      }
    }
    if (d?.type === 'clientMode' && d.remote) appState.remote = true;
  });
}

const app = mount(App, { target: document.getElementById('app') });
export default app;
