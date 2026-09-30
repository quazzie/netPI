import { mount as svelteMount, unmount } from 'svelte';
import McpTab from './McpTab.svelte';
export function mount(el, ctx) {
  const view = svelteMount(McpTab, { target: el, props: { ctx } });
  return { unmount: () => unmount(view), onShow: () => view.refresh() };
}
