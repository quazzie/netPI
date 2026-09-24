<script>
  /** An image the agent showed with show_image. It stays visible when the run's steps collapse; a click enlarges it. */
  let { item, onimage } = $props();
  const d = $derived(item.result?.details ?? {});
  const src = $derived(d.data ? `data:${d.mediaType};base64,${d.data}` : '');
  let size = $state('');
  const kb = (n) => (n >= 1024 * 1024 ? `${(n / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);
</script>

<figure class="shown">
  {#if src}
    <button class="frame" title="Open full size" onclick={() => onimage?.(src)}>
      <img {src} alt={d.caption || d.name || 'Image'} onload={(e) => (size = `${e.currentTarget.naturalWidth}×${e.currentTarget.naturalHeight}`)} />
    </button>
  {/if}
  <figcaption>
    {#if d.caption}<span class="cap">{d.caption}</span>{/if}
    <span class="meta">{[d.name, size, d.bytes ? kb(d.bytes) : ''].filter(Boolean).join(' · ')}</span>
  </figcaption>
</figure>

<style>
  .shown {
    margin: 2px 0;
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 6px;
    min-width: 0;
  }
  .frame {
    display: block;
    max-width: 100%;
    padding: 0;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-2);
    overflow: hidden;
    cursor: zoom-in;
  }
  .frame:hover {
    border-color: var(--border-strong);
  }
  img {
    display: block;
    max-width: 100%;
    max-height: min(60vh, 560px);
    object-fit: contain;
  }
  figcaption {
    display: flex;
    flex-direction: column;
    gap: 1px;
    min-width: 0;
    max-width: 100%;
  }
  .cap {
    color: var(--fg);
    font-size: var(--fs-sm);
  }
  .meta {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }
</style>
