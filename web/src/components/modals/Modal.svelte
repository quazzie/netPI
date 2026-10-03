<script module>
  // open dialogs (and popovers), innermost last: only the top one reacts to Esc
  const stack = [];

  /** A layer that takes Esc before the dialogs below it (a popover inside a dialog): { isTop(), remove() }. */
  export function escLayer() {
    const me = {};
    stack.push(me);
    return {
      isTop: () => stack.at(-1) === me,
      remove: () => {
        const i = stack.indexOf(me);
        if (i >= 0) stack.splice(i, 1);
      },
    };
  }

  const FOCUSABLE = 'a[href], button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])';

  /**
   * Keep Tab inside <paramref name="root"/>: from the last control to the first and back, and never out of it (the
   * overlays are siblings of the app, so the browser's next tab stop after the dialog is the chat behind it).
   */
  export function trapTab(e, root) {
    const items = [...(root?.querySelectorAll(FOCUSABLE) ?? [])].filter((el) => el.getClientRects().length > 0);
    if (items.length === 0) return;
    const active = document.activeElement;
    const outside = !root?.contains(active);
    if (outside || (e.shiftKey ? active === items[0] : active === items[items.length - 1])) {
      e.preventDefault();
      (e.shiftKey ? items[items.length - 1] : items[0]).focus();
    }
  }

  /**
   * Make everything beside <paramref name="overlay"/> inert while it is open, and return the function that puts it
   * back: a background control that takes focus, or a click that lands on the chat, would act behind the dialog. The
   * overlays live inside the app root, so the walk inerts that root's other children. A dialog under this one is left
   * alone — it is the top dialog that takes the keyboard, and a control it holds has to be focusable again when this
   * one closes and hands the focus back.
   */
  export function inertBackground(overlay) {
    let node = overlay;
    while (node?.parentElement && !node.parentElement.classList.contains('app')) node = node.parentElement;
    const root = node?.parentElement;
    if (!root) return () => {};
    const was = [];
    for (const child of root.children)
    {
      if (child === node || child.querySelector('[aria-modal="true"]')) continue;
      was.push([child, child.inert]);
      child.inert = true;
    }
    return () =>
    {
      for (const [child, inert] of was) child.inert = inert;
    };
  }
</script>

<script>
  import { onMount } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  /** Centered dialog with overlay. Esc / overlay click → onclose. Tab stays inside it; the app behind it is inert. */
  let { title = '', onclose, width = 520, children, footer = undefined, class: cls = '', padded = true } = $props();
  let dialog = $state();
  let overlay = $state();

  onMount(() => {
    const me = {};
    stack.push(me);
    const onKey = (e) => {
      if (stack.at(-1) !== me) return;
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        onclose?.();
      } else if (e.key === 'Tab') {
        trapTab(e, dialog);
      }
    };
    window.addEventListener('keydown', onKey, true);
    const prev = document.activeElement;   // before the background goes inert: that blurs whatever had the focus
    const uninert = inertBackground(overlay);
    const first = dialog?.querySelector('[data-autofocus]') ?? dialog?.querySelector('input, textarea, select, button:not(.x)');
    first?.focus?.({ preventScroll: true });
    return () => {
      stack.splice(stack.indexOf(me), 1);
      window.removeEventListener('keydown', onKey, true);
      uninert();
      if (prev?.isConnected) prev.focus?.({ preventScroll: true });
    };
  });
</script>

<div class="overlay" bind:this={overlay} role="presentation" onpointerdown={(e) => e.target === e.currentTarget && onclose?.()}>
  <div class="dialog {cls}" bind:this={dialog} role="dialog" aria-modal="true" aria-label={title} style:width="min({width}px, calc(100vw - 32px))">
    {#if title}
      <div class="head">
        <div class="title">{title}</div>
        <button class="x" aria-label="Close" onclick={() => onclose?.()}><Icon name="x" size={14} /></button>
      </div>
    {/if}
    <div class="body" class:padded>{@render children?.()}</div>
    {#if footer}<div class="foot">{@render footer()}</div>{/if}
  </div>
</div>

<style>
  .overlay {
    position: fixed;
    inset: 0;
    z-index: 80;
    display: flex;
    align-items: flex-start;
    justify-content: center;
    padding: 10vh 16px 16px;
    background: var(--overlay);
    animation: fade var(--t) var(--ease);
  }
  .dialog {
    display: flex;
    flex-direction: column;
    max-height: 80vh;
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-lg);
    background: var(--bg-1);
    box-shadow: var(--shadow);
    overflow: hidden;
    animation: rise var(--t) var(--ease);
  }
  .head {
    display: flex;
    align-items: center;
    gap: 8px;
    height: 44px;
    padding: 0 10px 0 16px;
    border-bottom: 1px solid var(--border);
    flex: none;
  }
  .title {
    flex: 1;
    font-weight: 600;
    font-size: 14px;
  }
  .x {
    display: grid;
    place-items: center;
    width: 26px;
    height: 26px;
    padding: 0;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-dim);
  }
  .x:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  .body {
    flex: 1;
    min-height: 0;
    overflow: auto;
    scrollbar-width: thin;
  }
  .body.padded {
    padding: 16px;
  }
  .foot {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 8px;
    padding: 10px 16px;
    border-top: 1px solid var(--border);
    flex: none;
  }
  @keyframes fade {
    from {
      opacity: 0;
    }
  }
  @keyframes rise {
    from {
      opacity: 0;
      transform: translateY(8px) scale(0.99);
    }
  }
</style>
