// Files tab (netpi.tools.files, left panel): lazy file tree of the active session's workspace.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { mount as svelteMount, unmount } from 'svelte';
import FilesTab from './FilesTab.svelte';

export function mount(el, ctx) {
  const view = svelteMount(FilesTab, { target: el, props: { ctx } });
  return {
    unmount: () => unmount(view),
    onShow: () => view.setVisible(true),
    onHide: () => view.setVisible(false),
  };
}
