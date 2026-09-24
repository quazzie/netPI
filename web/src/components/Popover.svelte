<script>
  import { onMount, tick } from 'svelte';
  import { escLayer } from './modals/Modal.svelte';
  /**
   * Floating panel anchored to an element (fixed positioning, so it escapes overflow clipping).
   * placement: bottom-start | bottom-end | top-start | top-end
   */
  let { anchor, onclose, placement = 'bottom-start', width = undefined, class: cls = '', children } = $props();

  let el = $state();
  let pos = $state({ left: 0, top: null, bottom: null });

  function place() {
    if (!anchor) return;
    const r = anchor.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const w = el?.offsetWidth ?? width ?? 240;
    let left = placement.endsWith('end') ? r.right - w : r.left;
    left = Math.max(8, Math.min(left, vw - w - 8));
    if (placement.startsWith('top')) pos = { left, top: null, bottom: vh - r.top + 6 };
    else pos = { left, top: r.bottom + 6, bottom: null };
  }

  function onPointerDown(e) {
    if (el?.contains(e.target) || anchor?.contains(e.target)) return;
    onclose?.();
  }
  // on the dialogs' Esc stack: Esc closes this popover, not the dialog it opened in
  let layer = null;
  function onKey(e) {
    if (e.key === 'Escape' && (!layer || layer.isTop())) {
      e.preventDefault();
      e.stopPropagation();
      onclose?.();
    }
  }

  onMount(() => {
    layer = escLayer();
    place();
    tick().then(place);
    window.addEventListener('pointerdown', onPointerDown, true);
    window.addEventListener('keydown', onKey, true);
    window.addEventListener('resize', place);
    return () => {
      layer?.remove();
      window.removeEventListener('pointerdown', onPointerDown, true);
      window.removeEventListener('keydown', onKey, true);
      window.removeEventListener('resize', place);
    };
  });
</script>

<div
  bind:this={el}
  class="popover {cls}"
  style:left="{pos.left}px"
  style:top={pos.top != null ? `${pos.top}px` : null}
  style:bottom={pos.bottom != null ? `${pos.bottom}px` : null}
  style:width={width ? `${width}px` : null}
  role="dialog"
>
  {@render children?.()}
</div>

<style>
  .popover {
    position: fixed;
    z-index: 60;
    max-height: min(70vh, 560px);
    display: flex;
    flex-direction: column;
    background: var(--bg-1);
    border: 1px solid var(--border-strong);
    border-radius: var(--radius);
    box-shadow: var(--shadow);
    overflow: hidden;
    animation: pop-in var(--t) var(--ease);
  }
  @keyframes pop-in {
    from {
      opacity: 0;
      transform: translateY(3px);
    }
  }
</style>
