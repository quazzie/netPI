<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { copyText } from '../../lib/markdown.js';
  import { stamp } from '../../lib/format.js';

  /** msg: ChatMessage (role user, or a steer notice) — or the optimistic { text, images } while sending */
  let { msg, pending = false, onimage } = $props();

  const esc = (s) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

  const text = $derived(
    msg.text ?? (msg.parts ?? []).filter((p) => p.type === 'text').map((p) => p.text).join('\n'),
  );
  const images = $derived(msg.images ?? (msg.parts ?? []).filter((p) => p.type === 'image'));
  const html = $derived(esc(text).replace(/(^|[\s(])(@[^\s@)]+)/g, '$1<span class="mention">$2</span>'));
  const tag = $derived(
    msg.meta?.kind === 'steer' || msg.meta?.delivery === 'steer' || msg.meta?.mode === 'steer'
      ? 'steered'
      : msg.meta?.kind === 'queued' || msg.meta?.delivery === 'queue' || msg.meta?.mode === 'queue'
        ? 'queued'
        : msg.meta?.source && msg.meta.source !== 'user'
          ? msg.meta.source
          : null,
  );

  let copied = $state(false);
  async function copy() {
    if (await copyText(text)) {
      copied = true;
      setTimeout(() => (copied = false), 1200);
    }
  }
</script>

<div class="user" class:pending>
  <div class="meta">
    {#if pending}<span class="np-dim">sending…</span>{:else}
      <button class="copy" onclick={copy} title="Copy">
        <Icon name={copied ? 'check' : 'copy'} size={12} />
      </button>
      <span class="time">{stamp(msg.createdAt)}</span>
    {/if}
    {#if tag}<span class="tag"><Icon name={tag === 'queued' ? 'queue' : 'steer'} size={11} />{tag}</span>{/if}
  </div>
  <div class="bubble">
    {#if images.length}
      <div class="imgs">
        {#each images as img, i (i)}
          {@const src = img.url ?? `data:${img.mediaType};base64,${img.data}`}
          <button class="img" onclick={() => onimage?.(src)} title="Open image"><img {src} alt="attachment {i + 1}" /></button>
        {/each}
      </div>
    {/if}
    {#if text}<div class="text">{@html html}</div>{/if}
  </div>
</div>

<style>
  .user {
    display: flex;
    justify-content: flex-end;
    align-items: flex-end;
    gap: 8px;
    padding-left: 12%;
  }
  .bubble {
    min-width: 0;
    max-width: 100%;
    padding: 9px 14px;
    border-radius: 14px 14px 4px 14px;
    background: var(--bg-3);
    border: 1px solid var(--border);
    color: var(--fg);
    font-size: var(--fs-chat);
    line-height: 1.55;
  }
  .text {
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .text :global(.mention) {
    color: var(--accent);
    font-family: var(--font-mono);
    font-size: 0.9em;
  }
  .imgs {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
    margin-bottom: 8px;
  }
  .imgs:last-child {
    margin-bottom: 0;
  }
  .img {
    padding: 0;
    border: 1px solid var(--border);
    border-radius: 8px;
    overflow: hidden;
    background: var(--bg-2);
    cursor: zoom-in;
  }
  .img img {
    display: block;
    height: 96px;
    max-width: 220px;
    object-fit: cover;
  }
  .meta {
    flex: none;
    display: flex;
    align-items: center;
    gap: 6px;
    height: 22px;
    margin-bottom: 2px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    white-space: nowrap;
  }
  .time,
  .copy {
    opacity: 0;
    transition: opacity var(--t-fast);
  }
  .user:hover .time,
  .user:hover .copy {
    opacity: 1;
  }
  .copy {
    display: grid;
    place-items: center;
    width: 20px;
    height: 20px;
    padding: 0;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg-dim);
  }
  .copy:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .tag {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    color: var(--accent);
  }
</style>
