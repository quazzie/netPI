<script>
  /**
   * What the model was sent, exactly as sent (monospace, wrapped), with copy. `markdown`: the content is meant to be read
   * (a subagent's report, a summary), so it opens rendered and a toggle shows it as sent.
   */
  import { untrack } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import { renderMarkdown, copyText } from '../../lib/markdown.js';
  import { toast } from '../../lib/state/ui.svelte.js';

  let { text, note = 'Sent to the model', markdown = null, children } = $props();
  let rendered = $state(untrack(() => markdown != null));
  let copied = $state(false);

  async function copy() {
    if (await copyText(text)) {
      copied = true;
      setTimeout(() => (copied = false), 1200);
    } else toast('Copy failed', 'error');
  }
</script>

<div class="sent">
  <div class="bar">
    <span class="note np-ellipsis">{note}</span>
    <span class="size np-dim">{text.length.toLocaleString()} chars · ≈{Math.round(text.length / 4).toLocaleString()} tokens</span>
    {#if markdown != null}
      <button class="tb" class:on={!rendered} onclick={() => (rendered = !rendered)} title={rendered ? 'Show it as sent' : 'Show it rendered'}>
        {rendered ? 'As sent' : 'Rendered'}
      </button>
    {/if}
    <button class="tb" onclick={copy} title="Copy what was sent" aria-label="Copy"><Icon name={copied ? 'check' : 'copy'} size={12} /></button>
  </div>
  {#if rendered}
    <div class="md">{@html renderMarkdown(markdown)}</div>
  {:else}
    <pre class="raw np-mono">{text}</pre>
  {/if}
  {@render children?.()}
</div>

<style>
  .sent {
    margin: 6px 24px 4px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-1);
    overflow: hidden;
  }
  .bar {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 26px;
    padding: 2px 6px 2px 12px;
    border-bottom: 1px solid var(--border);
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .note {
    flex: 1;
    min-width: 0;
  }
  .size {
    flex: none;
    font-variant-numeric: tabular-nums;
  }
  .tb {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    height: 20px;
    padding: 0 6px;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
  }
  .tb:hover,
  .tb.on {
    background: var(--bg-3);
    color: var(--fg);
  }
  .raw {
    margin: 0;
    padding: 10px 12px;
    max-height: 480px;
    overflow: auto;
    font-size: 11.5px;
    line-height: 1.5;
    color: var(--fg-muted);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .md {
    padding: 10px 14px;
    max-height: 480px;
    overflow: auto;
    font-size: 13px;
    color: var(--fg-muted);
  }
</style>
