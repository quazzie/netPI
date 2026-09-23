// Ideas tab (netpi.ideas): the ideas backlog of the active session's project (or the global file).
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { mount as svelteMount, unmount } from 'svelte';
import IdeasTab from './IdeasTab.svelte';

export function mount(el, ctx) {
  const view = svelteMount(IdeasTab, { target: el, props: { ctx } });
  return {
    unmount: () => unmount(view),
    onShow: () => view.setVisible(true),
    onHide: () => view.setVisible(false),
  };
}
