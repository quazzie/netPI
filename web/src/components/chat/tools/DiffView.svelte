<script>
  import { parseDiff, editsToDiff } from '../../../lib/diff.js';
  import { arg } from '../../../lib/tools.js';

  /** edit / write: unified diff from details.diff (or a pseudo diff from the arguments while pending) */
  let { name, args, result } = $props();
  const LIMIT = 400;

  const d = $derived(result?.details ?? null);
  const diffText = $derived(d?.diff ?? (name === 'edit' || name === 'ssh_edit' ? editsToDiff(args) : null));
  const parsed = $derived(diffText ? parseDiff(diffText) : null);
  const visibleRows = $derived(parsed ? parsed.rows.filter((r) => r.t !== 'file') : []);
  let showAll = $state(false);
  const rows = $derived(showAll ? visibleRows : visibleRows.slice(0, LIMIT));
  const pseudo = $derived(!d?.diff && (name === 'edit' || name === 'ssh_edit'));

  // write of a new file: show the content
  const content = $derived((name === 'write' || name === 'ssh_write') && !d?.diff ? String(arg(args, 'content', 'text') ?? '') : '');
  const contentLines = $derived(content ? content.split('\n') : []);
  let showAllContent = $state(false);

  const sign = { add: '+', del: '−', ctx: ' ', hunk: '', meta: '' };
</script>

{#if parsed && visibleRows.length}
  {#if pseudo && !result}<div class="note">pending — showing requested edits</div>{/if}
  <div class="diff np-mono np-scroll">
    {#each rows as r, i (i)}
      {#if r.t === 'hunk' || r.t === 'meta'}
        <div class="dl {r.t}"><span class="gut"></span><span class="code">{r.text}</span></div>
      {:else}
        <div class="dl {r.t}">
          <span class="ln">{r.o ?? ''}</span><span class="ln">{r.n ?? ''}</span><span class="sg">{sign[r.t]}</span><span
            class="code">{r.text}</span
          >
        </div>
      {/if}
    {/each}
  </div>
  {#if visibleRows.length > LIMIT}
    <button class="np-btn np-btn-ghost np-btn-sm more" onclick={() => (showAll = !showAll)}>
      {showAll ? 'Collapse' : `Show all ${visibleRows.length} lines`}
    </button>
  {/if}
{:else if content}
  <div class="note">{d?.created ? 'new file' : 'content'} · {contentLines.length} lines{d?.bytes != null ? ` · ${d.bytes} bytes` : ''}</div>
  <div class="diff np-mono np-scroll">
    {#each showAllContent ? contentLines : contentLines.slice(0, 60) as l, i (i)}
      <div class="dl add"><span class="ln"></span><span class="ln">{i + 1}</span><span class="sg">+</span><span class="code">{l}</span></div>
    {/each}
  </div>
  {#if contentLines.length > 60}
    <button class="np-btn np-btn-ghost np-btn-sm more" onclick={() => (showAllContent = !showAllContent)}>
      {showAllContent ? 'Collapse' : `Show all ${contentLines.length} lines`}
    </button>
  {/if}
{:else if result && !result.isError}
  <div class="note">{result.content}</div>
{/if}
{#if d?.fuzzy?.length}
  <div class="note">
    {#each d.fuzzy as f, i (i)}<div>edit {f.edit} matched with <b>{f.strategy}</b> fallback at line {f.line}</div>{/each}
  </div>
{/if}

<style>
  .diff {
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    font-size: 12px;
    line-height: 1.5;
    overflow: auto;
    max-height: 520px;
    padding: 4px 0;
  }
  .dl {
    display: flex;
    min-width: max-content;
    width: 100%;
  }
  .ln {
    flex: none;
    width: 42px;
    padding-right: 8px;
    text-align: right;
    color: var(--fg-dim);
    opacity: 0.7;
    user-select: none;
    font-variant-numeric: tabular-nums;
  }
  .gut {
    flex: none;
    width: 84px;
  }
  .sg {
    flex: none;
    width: 16px;
    text-align: center;
    user-select: none;
    color: var(--fg-dim);
  }
  .code {
    flex: 1;
    white-space: pre;
    padding-right: 12px;
    color: var(--fg-muted);
  }
  .dl.add {
    background: var(--diff-add);
  }
  .dl.add .code,
  .dl.add .sg {
    color: var(--diff-add-fg);
  }
  .dl.del {
    background: var(--diff-del);
  }
  .dl.del .code,
  .dl.del .sg {
    color: var(--diff-del-fg);
  }
  .dl.hunk {
    background: var(--diff-hunk);
    margin: 2px 0;
  }
  .dl.hunk .code,
  .dl.meta .code {
    color: var(--info);
    opacity: 0.85;
  }
  .note {
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .more {
    align-self: flex-start;
  }
</style>
