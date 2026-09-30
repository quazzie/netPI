<script>
  import { highlightText, langFromPath } from '../../../lib/markdown.js';
  import { pathArg } from '../../../lib/tools.js';
  import { bytes } from '../../../lib/format.js';

  /** read: path, line range and the returned content (collapsed to the first lines) */
  let { args, result } = $props();
  const PREVIEW = 40;

  const d = $derived(result?.details ?? {});
  const path = $derived(d.path ?? pathArg(args) ?? '');
  const content = $derived(result?.content ?? '');
  const lines = $derived(content ? content.replace(/\n$/, '').split('\n') : []);
  let showAll = $state(false);
  const shown = $derived(showAll ? lines : lines.slice(0, PREVIEW));
  const start = $derived(d.startLine ?? 1);
  const gutter = $derived(shown.map((_, i) => start + i).join('\n'));
  const text = $derived(shown.join('\n'));

  let hl = $state(null);
  $effect(() => {
    const t = text;
    const lang = langFromPath(path);
    hl = null;
    if (!lang || !t) return;
    let cancelled = false;
    highlightText(t, lang).then((h) => !cancelled && (hl = h));
    return () => (cancelled = true);
  });
</script>

<div class="read">
  <div class="meta np-mono">
    <span class="path" title={path}>{path}</span>
    {#if d.startLine != null}<span class="np-dim">lines {d.startLine}–{d.endLine}{d.totalLines != null ? ` of ${d.totalLines}` : ''}</span>{/if}
    {#if d.truncated}<span class="np-badge" data-tone="warn">truncated</span>{/if}
    {#if d.eol === 'crlf'}<span class="np-badge">CRLF</span>{/if}
    {#if d.encoding}<span class="np-badge">{d.encoding}</span>{/if}
    {#if d.image}<span class="np-badge">{d.mediaType} · {bytes(d.bytes)}</span>{/if}
  </div>
  {#if result?.images?.length}
    <div class="imgs">
      {#each result.images as img, i (i)}
        <img src="data:{img.mediaType};base64,{img.data}" alt="" />
      {/each}
    </div>
  {/if}
  {#if lines.length && !d.image}
    <div class="code np-scroll">
      <pre class="gutter np-mono">{gutter}</pre>
      {#if hl}
        <pre class="src np-mono">{@html hl}</pre>
      {:else}
        <pre class="src np-mono">{text}</pre>
      {/if}
    </div>
    {#if lines.length > PREVIEW}
      <button class="np-btn np-btn-ghost np-btn-sm more" onclick={() => (showAll = !showAll)}>
        {showAll ? 'Collapse' : `Show all ${lines.length} lines`}
      </button>
    {/if}
  {:else if !result}
    <div class="np-dim np-small">reading…</div>
  {/if}
</div>

<style>
  .read {
    display: flex;
    flex-direction: column;
    gap: 6px;
    min-width: 0;
  }
  .meta {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 11.5px;
    min-width: 0;
    flex-wrap: wrap;
  }
  .path {
    color: var(--fg-muted);
    overflow-wrap: anywhere;
  }
  .code {
    display: flex;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    max-height: 480px;
    overflow: auto;
  }
  pre {
    margin: 0;
    padding: 6px 0;
    font-size: 12px;
    line-height: 1.5;
  }
  .gutter {
    flex: none;
    padding: 6px 10px 6px 8px;
    text-align: right;
    color: var(--fg-dim);
    opacity: 0.65;
    user-select: none;
    border-right: 1px solid var(--border);
    position: sticky;
    left: 0;
    background: var(--code-bg);
  }
  .src {
    flex: 1;
    padding: 6px 12px;
    color: var(--fg-muted);
    white-space: pre;
  }
  .imgs img {
    max-width: 100%;
    max-height: 320px;
    border-radius: var(--radius-sm);
    border: 1px solid var(--border);
  }
  .more {
    align-self: flex-start;
  }
</style>
