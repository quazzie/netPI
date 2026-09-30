import './styles/tokens.css';
import './styles/kit.css';
import './styles/app.css';
import { mount } from 'svelte';
import { initToken } from './lib/auth.js';
import { icons } from './lib/icons.js';
import { renderMarkdown, highlight, installCodeCopy, copyText } from './lib/markdown.js';
import { installFileLinks } from './lib/openFile.js';
import { confirmDialog } from './lib/state/ui.svelte.js';
import App from './App.svelte';

initToken();

// Services shared with plugin bundles through @netpi/kit (see lib/kit/host.js).
globalThis.__netpiHost = Object.freeze({
  version: 1,
  icon: (name) => icons[name] ?? icons.puzzle,
  renderMarkdown: (text) => renderMarkdown(text),
  highlight: (node, enabled) => highlight(node, enabled),
  confirm: (opts) => confirmDialog(opts ?? {}),
  copyText: (text) => copyText(text),
});

installCodeCopy();
installFileLinks();

const app = mount(App, { target: document.getElementById('app') });
export default app;
