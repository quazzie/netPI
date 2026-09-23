<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { renderMarkdown, highlight, copyText } from '../../lib/markdown.js';
  import { duration, tokens, stamp } from '../../lib/format.js';

  /** item: { kind:'text', msg, text, last } */
  let { item } = $props();
  const html = $derived(renderMarkdown(item.text));
  const m = $derived(item.msg);
  const u = $derived(m.usage);

  let copied = $state(false);
  async function copy() {
    const all = m.parts.filter((p) => p.type === 'text').map((p) => p.text).join('\n\n');
    if (await copyText(all)) {
      copied = true;
      setTimeout(() => (copied = false), 1200);
    }
  }
</script>

<div class="assistant" class:compacted={m.compacted}>
  {#key html}
    <div class="md" use:highlight>{@html html}</div>
  {/key}
  {#if item.last && m.stopReason !== 'tool_use'}
    <div class="foot">
      <button class="act" onclick={copy} title="Copy message"><Icon name={copied ? 'check' : 'copy'} size={12} /></button>
      <span class="info np-mono">
        {#if m.model}<span>{m.model.split('/').pop()}</span>{/if}
        {#if m.durationMs}<span>{duration(m.durationMs)}</span>{/if}
        {#if u}<span title="input · cache read · output tokens"
            >{tokens(u.inputTokens)} in{u.cacheReadTokens ? ` (+${tokens(u.cacheReadTokens)} cached)` : ''} · {tokens(
              u.outputTokens,
            )} out</span
          >{/if}
        <span>{stamp(m.createdAt)}</span>
      </span>
    </div>
  {/if}
</div>

<style>
  .assistant {
    position: relative;
    min-width: 0;
    padding: 0 2px;
  }
  .compacted {
    opacity: 0.8;
  }
  .foot {
    position: absolute;
    left: 2px;
    top: 100%;
    z-index: 1;
    display: flex;
    align-items: center;
    gap: 6px;
    height: 22px;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    opacity: 0;
    transition: opacity var(--t-fast);
  }
  .assistant:hover .foot,
  .foot:hover {
    opacity: 1;
  }
  .act {
    display: grid;
    place-items: center;
    width: 22px;
    height: 22px;
    margin-left: -4px;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg-dim);
  }
  .act:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .info {
    display: flex;
    gap: 10px;
    font-size: 11px;
  }
</style>
