<script>
  import { onDestroy } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import { renderMarkdown, highlight, copyText } from '../../lib/markdown.js';
  import { duration, tokens, stamp } from '../../lib/format.js';

  /**
   * item: { kind:'text', msg, text, last }, or while the answer streams { kind:'text', text, stream, msg:null }: then the
   * markdown is re-rendered at most every 100ms, never highlighted, with a caret that takes no room, in the same box as
   * the finished text so nothing moves when message.added replaces it.
   */
  let { item } = $props();
  const streaming = $derived(!!item.stream);
  const html = $derived(streaming ? '' : renderMarkdown(item.text));
  const m = $derived(item.msg);
  const u = $derived(m?.usage);

  let liveHtml = $state('');
  let timer = 0;
  let last = 0;
  function render() {
    timer = 0;
    last = performance.now();
    // the caret goes inside the last paragraph so it follows the text
    liveHtml = renderMarkdown(item.text, { cache: false }).replace(/<\/p>\s*$/, '<span class="caret"></span></p>');
  }
  $effect(() => {
    const t = item.text;
    if (!streaming || !t || timer) return;
    const wait = 100 - (performance.now() - last);
    if (wait <= 0) render();
    else timer = setTimeout(render, wait);
  });
  onDestroy(() => clearTimeout(timer));

  let copied = $state(false);
  async function copy() {
    const all = m.parts.filter((p) => p.type === 'text').map((p) => p.text).join('\n\n');
    if (await copyText(all)) {
      copied = true;
      setTimeout(() => (copied = false), 1200);
    }
  }
</script>

{#if streaming}
  <div class="assistant live">
    <div class="md">{@html liveHtml}</div>
  </div>
{:else}
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
{/if}

<style>
  /* a caret without width, so it never wraps a line the finished text would not wrap */
  .live :global(.caret) {
    position: relative;
    display: inline-block;
    width: 0;
    height: 1.05em;
    vertical-align: text-bottom;
  }
  .live :global(.caret)::after {
    content: '';
    position: absolute;
    left: 2px;
    top: 0;
    width: 7px;
    height: 100%;
    border-radius: 1px;
    background: var(--accent);
    animation: blink 1s steps(2, start) infinite;
  }
  @keyframes blink {
    to {
      visibility: hidden;
    }
  }
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
    /* hidden, it hangs below the message: it must not take the clicks meant for what is there */
    pointer-events: none;
    transition: opacity var(--t-fast);
  }
  .assistant:hover .foot,
  .foot:focus-within {
    opacity: 1;
    pointer-events: auto;
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
