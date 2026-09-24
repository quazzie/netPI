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
      { value: 'all', label: 'All', icon: 'list', title: 'All levels' },
      { value: 'info', label: 'Info+', icon: 'info', title: 'Information and above' },
      { value: 'warn', label: 'Warn', icon: 'alert', count: counts.wrn || null, title: 'Warnings and errors' },
      { value: 'error', label: 'Error', icon: 'circle-x', count: counts.err || null, tone: 'err', title: 'Errors' },
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
      <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
      <div class="log" data-l={lv} class:open={open === l} onclick={() => (open = open === l ? null : l)}>
        <div class="row np-line">
          <span class="t np-mono">{time(l.time)}</span>
          <span class="lvl np-mono">{lv}</span>
          <span class="cat np-mono np-grow" title={l.category}>{l.category}</span>
          {#if l.exception}<span class="x" title="Has an exception (click to show)">exception</span>{/if}
        </div>
        <div class="msg">{l.message}</div>
        {#if l.exception && open === l}<pre class="exc np-mono">{l.exception}</pre>{/if}
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
    padding: 4px 4px;
    margin: 0 -4px;
    border-radius: var(--radius-sm);
    cursor: pointer;
  }
  .log:hover {
    background: var(--bg-2);
  }
  .row {
    gap: 7px;
  }
  .x {
    font-size: 10px;
    color: var(--err);
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
    /* long messages: 3 lines until opened */
    display: -webkit-box;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 3;
    line-clamp: 3;
    overflow: hidden;
  }
  .log.open .msg {
    display: block;
    -webkit-line-clamp: unset;
    line-clamp: none;
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
</style>
