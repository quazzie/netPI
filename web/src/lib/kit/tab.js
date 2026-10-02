// The mount() a plugin tab's main.js exports (docs/PROTOCOL.md → Plugin UI tabs), built from the component itself:
//
//   export const mount = createTab(IdeasTab);
//
// It mounts with ctx, forwards the host's onShow/onHide to the component's own setVisible, and unmounts on the way out.
// Everything the component registered through ctx is released when the host disposes it (see pluginCtx.js).
import { mount, unmount } from 'svelte';

export function createTab(Component) {
  return (el, ctx) => {
    const view = mount(Component, { target: el, props: { ctx } });
    return {
      unmount: () => unmount(view),
      onShow: () => view.setVisible?.(true),
      onHide: () => view.setVisible?.(false),
    };
  };
}