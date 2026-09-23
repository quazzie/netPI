<script>
  import { onMount } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  /** Centered dialog with overlay. Esc / overlay click → onclose. */
  let { title = '', onclose, width = 520, children, footer = undefined, class: cls = '', padded = true } = $props();
  let dialog = $state();

  function onKey(e) {
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      onclose?.();
    }
  }
  onMount(() => {
    window.addEventListener('keydown', onKey, true);
    const prev = document.activeElement;
    const first = dialog?.querySelector('[data-autofocus]') ?? dialog?.querySelector('input, textarea, select, button:not(.x)');
    first?.focus?.();
    return () => {
      window.removeEventListener('keydown', onKey, true);
      if (prev?.isConnected) prev.focus?.({ preventScroll: true });
    };
  });
</script>

<div class="overlay" role="presentation" onpointerdown={(e) => e.target === e.currentTarget && onclose?.()}>
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
