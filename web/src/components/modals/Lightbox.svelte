<script>
  import { onMount } from 'svelte';
  import { escLayer } from './Modal.svelte';
  let { data, onclose } = $props();
  onMount(() => {
    // Esc belongs to the top layer only: a lightbox opened over a dialog, or over the chat with a dialog behind it,
    // must not close whatever is underneath.
    const layer = escLayer();
    const k = (e) => {
      if (e.key === 'Escape' && layer.isTop()) {
        e.preventDefault();
        e.stopPropagation();
        onclose();
      }
    };
    window.addEventListener('keydown', k, true);
    return () => {
      layer.remove();
      window.removeEventListener('keydown', k, true);
    };
  });
</script>

<div class="lb" role="presentation" onclick={onclose}>
  <img src={data.src} alt="" />
</div>

<style>
  .lb {
    position: fixed;
    inset: 0;
    z-index: 90;
    display: grid;
    place-items: center;
    padding: 32px;
    background: rgba(0, 0, 0, 0.8);
    cursor: zoom-out;
  }
  img {
    max-width: 100%;
    max-height: 100%;
    border-radius: 6px;
    box-shadow: var(--shadow);
  }
</style>
