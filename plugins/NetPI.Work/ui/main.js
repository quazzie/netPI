// Work tab (netpi.work): what is running right now — lanes, agents, processes and today's usage.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { mount as svelteMount, unmount } from 'svelte';
import WorkTab from './WorkTab.svelte';

export function mount(el, ctx) {
  const view = svelteMount(WorkTab, { target: el, props: { ctx } });
  return {
    unmount: () => unmount(view),
    onShow: () => view.setVisible(true),
    onHide: () => view.setVisible(false),
  };
}
