<script>
  /** grep / find / ls: result lines with light structure (path, line number) */
  let { name, result } = $props();
  const LIMIT = 300;

  const d = $derived(result?.details ?? {});
  const lines = $derived((result?.content ?? '').replace(/\n$/, '').split('\n').filter((l, i, a) => l || i < a.length - 1));
  let showAll = $state(false);
  const shown = $derived(showAll ? lines : lines.slice(0, LIMIT));

  // grep content lines: "path:12: text" (match) or "path-13- text" (context)
  const GREP = /^(.+?)([:-])(\d+)\2 ?(.*)$/;
  function parse(l) {
    if (name !== 'grep' || d.outputMode === 'files' || d.outputMode === 'count') return null;
    const m = GREP.exec(l);
    return m ? { path: m[1], ctx: m[2] === '-', line: m[3], text: m[4] } : null;
  }
</script>

{#if result}
  <div class="search np-mono np-scroll">
    {#each shown as l, i (i)}
      {@const g = parse(l)}
      {#if g}
        <div class="l" class:ctx={g.ctx}><span class="p">{g.path}</span><span class="n">{g.line}</span><span class="t">{g.text}</span></div>
      {:else if l === '--'}
        <div class="sep"></div>
      {:else if l.startsWith('[')}
        <div class="l note">{l}</div>
      {:else}
        <div class="l" class:dir={l.endsWith('/')}>{l}</div>
      {/if}
    {/each}
  </div>
  <div class="foot">
    {#if name === 'grep' && d.matches != null}
      <span>{d.matches} matches in {d.files} files</span>
      {#if d.filesSearched}<span>· {d.filesSearched} searched</span>{/if}
    {:else if name === 'find' && d.count != null}
      <span>{d.count} results</span>
    {:else if name === 'ls' && d.entries != null}
      <span>{d.dirs ?? 0} dirs · {d.files ?? 0} files{d.hidden ? ` · ${d.hidden} hidden` : ''}</span>
    {/if}
    {#if d.truncated}<span class="np-badge" data-tone="warn">truncated</span>{/if}
    <span class="np-spacer"></span>
    {#if lines.length > LIMIT}
      <button class="np-btn np-btn-ghost np-btn-sm" onclick={() => (showAll = !showAll)}>{showAll ? 'Collapse' : `Show all ${lines.length}`}</button>
    {/if}
  </div>
{:else}
  <div class="np-dim np-small">searching…</div>
{/if}

<style>
  .search {
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    font-size: 12px;
    line-height: 1.55;
    max-height: 360px;
    overflow: auto;
    padding: 6px 0;
  }
  .l {
    padding: 0 10px;
    white-space: pre;
    color: var(--fg-muted);
    min-width: max-content;
  }
  .l.dir {
    color: var(--info);
  }
  .l.ctx {
    opacity: 0.6;
  }
  .l.note {
    color: var(--warn);
    white-space: pre-wrap;
  }
  .p {
    color: var(--fg-dim);
  }
  .n {
    color: var(--accent);
    padding: 0 8px 0 6px;
  }
  .t {
    color: var(--fg);
  }
  .sep {
    height: 1px;
    margin: 4px 10px;
    background: var(--border);
  }
  .foot {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
</style>
