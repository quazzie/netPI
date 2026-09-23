<script>
  import { onMount, untrack } from 'svelte';
  import { SearchInput, Segmented, Empty } from '@netpi/kit';

  /** logs.recent (host), polled while visible. Newest first. */
  let { initial = null, ctx, visible = true } = $props();

  let logs = $state.raw(untrack(() => initial));
  let error = $state('');
  let q = $state('');
  let level = $state('all');
  let open = $state(null);

  const RANK = { trc: 0, dbg: 1, inf: 2, wrn: 3, err: 4, crt: 5 };
  const norm = (l) => {
    const s = String(l ?? '').toLowerCase();
    return s.startsWith('tr') ? 'trc' : s.startsWith('d') ? 'dbg' : s.startsWith('i') ? 'inf' : s.startsWith('w') ? 'wrn' : s.startsWith('c') || s.startsWith('f') ? 'crt' : s.startsWith('e') ? 'err' : s;
  };

  async function load() {
    try {
      logs = await ctx.rpc('logs.recent', { max: 400 });
      error = '';
    } catch (e) {
      error = e.message;
    }
  }
  onMount(() => {
    load();
    const t = setInterval(() => visible && load(), 3000);
    return () => clearInterval(t);
  });

  const min = $derived({ all: 0, info: 2, warn: 3, error: 4 }[level]);
  const shown = $derived(
    (logs ?? [])
      .filter((l) => (RANK[norm(l.level)] ?? 2) >= min && (!q || `${l.category} ${l.message}`.toLowerCase().includes(q.toLowerCase())))
      .slice(-400)
      .reverse(),
  );
  const counts = $derived.by(() => {
    const c = { wrn: 0, err: 0 };
    for (const l of logs ?? []) {
      const n = norm(l.level);
      if (n === 'wrn') c.wrn++;
      if (n === 'err' || n === 'crt') c.err++;
    }
    return c;
  });
  const time = (t) => {
    const d = new Date(t);
    return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString('en-GB');
  };
</script>

<div class="bar">
  <SearchInput bind:value={q} placeholder="Filter logs" />
</div>
<div class="lv">
  <Segmented
    bind:value={level}
    options={[
      { value: 'all', label: 'All' },
      { value: 'info', label: 'Info+' },
      { value: 'warn', label: 'Warn', count: counts.wrn || null },
      { value: 'error', label: 'Error', count: counts.err || null },
    ]}
  />
</div>
{#if logs == null && !error}
  <Empty><span class="np-spinner"></span></Empty>
{:else if error && !logs}
  <Empty icon="alert">logs.recent unavailable: {error}</Empty>
{:else}
  <div class="list">
    {#each shown as l, i (i + l.time + l.message.length)}
      {@const lv = norm(l.level)}
      <div class="log" data-l={lv}>
        <button class="row" onclick={() => (open = open === l ? null : l)} disabled={!l.exception}>
          <span class="t np-mono">{time(l.time)}</span>
          <span class="lvl np-mono">{lv}</span>
          <span class="cat np-mono" title={l.category}>{l.category}</span>
        </button>
        <div class="msg">{l.message}</div>
        {#if l.exception}
          {#if open === l}<pre class="exc np-mono">{l.exception}</pre>{:else}<button class="lnk" onclick={() => (open = l)}>show exception</button>{/if}
        {/if}
      </div>
    {:else}
      <Empty>No log entries{q || level !== 'all' ? ' match the filter' : ''}</Empty>
    {/each}
  </div>
{/if}

<style>
  .bar {
    padding: 4px 10px 2px 12px;
  }
  .lv {
    padding: 4px 10px 6px 12px;
  }
  .list {
    padding: 0 10px 10px 12px;
  }
  .log {
    padding: 4px 0;
    border-bottom: 1px solid color-mix(in srgb, var(--border) 50%, transparent);
  }
  .row {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    padding: 0;
    border: 0;
    background: transparent;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: default;
    min-width: 0;
  }
  .t {
    font-size: 10.5px;
    color: var(--fg-dim);
    flex: none;
  }
  .lvl {
    flex: none;
    width: 28px;
    font-size: 10px;
    font-weight: 700;
    text-transform: uppercase;
    color: var(--fg-dim);
  }
  [data-l='inf'] .lvl {
    color: var(--info);
  }
  [data-l='wrn'] .lvl {
    color: var(--warn);
  }
  [data-l='err'] .lvl,
  [data-l='crt'] .lvl {
    color: var(--err);
  }
  .cat {
    font-size: 10.5px;
    color: var(--fg-dim);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    min-width: 0;
  }
  .msg {
    margin-top: 1px;
    font-size: var(--fs-sm);
    line-height: 1.4;
    overflow-wrap: anywhere;
  }
  [data-l='err'] .msg,
  [data-l='crt'] .msg {
    color: var(--err);
  }
  [data-l='dbg'] .msg,
  [data-l='trc'] .msg {
    color: var(--fg-muted);
  }
  .exc {
    margin: 4px 0 0;
    padding: 6px 8px;
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    color: var(--fg-muted);
    font-size: 10.5px;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    max-height: 220px;
    overflow: auto;
  }
  .lnk {
    padding: 0;
    border: 0;
    background: transparent;
    color: var(--accent);
    font-size: var(--fs-xs);
    cursor: pointer;
  }
</style>
