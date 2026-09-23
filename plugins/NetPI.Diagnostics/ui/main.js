// Diagnostics tab (netpi.diagnostics): plugins, tools, RPC methods, live events, logs and the context preview.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { mount as svelteMount, unmount } from 'svelte';
import DiagTab from './DiagTab.svelte';

export function mount(el, ctx) {
  const view = svelteMount(DiagTab, { target: el, props: { ctx } });
  return {
    unmount: () => unmount(view),
    onShow: () => view.setVisible(true),
    onHide: () => view.setVisible(false),
  };
}
