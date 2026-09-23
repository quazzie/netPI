<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { renderMarkdown } from '../../lib/markdown.js';
  import { prefs } from '../../lib/state/ui.svelte.js';
  import { duration } from '../../lib/format.js';

  let { step, chat } = $props();
  const p = $derived(step.part);
  const open = $derived(chat.expanded.get(step.key) ?? prefs.expandThinking);
  const preview = $derived.by(() => {
    const t = (p.text || '').trim();
    const i = t.indexOf('\n');
    return (i < 0 ? t : t.slice(0, i)).replace(/[*_#`>]/g, '').slice(0, 200);
  });
</script>

<div class="thinking" class:open>
  <button class="line" aria-expanded={open} onclick={() => chat.expanded.set(step.key, !open)}>
    <span class="ic"><Icon name="brain" size={14} /></span>
    <span class="label">{p.redacted && !p.text ? 'Thinking (redacted)' : 'Thinking'}</span>
    {#if p.durationMs}<span class="dur">{duration(p.durationMs)}</span>{/if}
    {#if !open && preview}<span class="preview">{preview}</span>{/if}
    <span class="chev" class:open><Icon name="chevron-right" size={12} /></span>
  </button>
  {#if open && p.text}
    <div class="body md">{@html renderMarkdown(p.text)}</div>
  {/if}
</div>

<style>
  .thinking {
    min-width: 0;
  }
  .line {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    height: 28px;
    padding: 0 8px 0 4px;
    margin-left: -4px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    text-align: left;
    min-width: 0;
  }
  .line:hover {
    background: var(--bg-2);
  }
  .ic {
    display: grid;
    place-items: center;
    width: 16px;
    color: var(--fg-dim);
  }
  .label {
    flex: none;
  }
  .dur {
    flex: none;
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-size: 11px;
  }
  .preview {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    color: var(--fg-dim);
    font-style: italic;
  }
  .chev {
    margin-left: auto;
    display: inline-grid;
    color: var(--fg-dim);
    opacity: 0;
    transition:
      transform var(--t-fast),
      opacity var(--t-fast);
  }
  .line:hover .chev,
  .chev.open {
    opacity: 1;
  }
  .chev.open {
    transform: rotate(90deg);
  }
  .body {
    margin: 2px 0 8px 7px;
    padding: 2px 0 2px 16px;
    border-left: 2px solid var(--border);
    color: var(--fg-muted);
    font-size: 13px;
    line-height: 1.55;
  }
</style>
