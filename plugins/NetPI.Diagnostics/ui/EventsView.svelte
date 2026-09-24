<script>
  import { onMount, untrack } from 'svelte';
  import { SearchInput, IconButton, Empty } from '@netpi/kit';

  /** Live event tail: seeded from diag.snapshot (no payloads), then ctx.on('*') (with payloads). Newest first. */
  let { initial = [], ctx, visible = true } = $props();

  const MAX = 1000;
  const NOISE = /^(stream\.delta|tool\.output|process\.output)$/;
  let rows = $state.raw(
    untrack(() => initial.map((e) => ({ seq: e.seq, type: e.type, sid: e.sessionId ?? null, ts: Date.parse(e.time), source: e.source, d: undefined })).reverse()),
  );
  let q = $state('');
  let hideNoise = $state(true);
  let paused = $state(false);
  let open = $state(null); // seq
  let detail = $state(null); // { seq, data | error }
  let pending = [];
  let raf = 0;

  function flush() {
    raf = 0;
    if (!pending.length) return;
    const add = pending.reverse();
    pending = [];
    rows = add.concat(rows).slice(0, MAX);
  }

  onMount(() => {
    const off = ctx.on('*', (d, env) => {
      if (paused || !visible) return;
      pending.push({ seq: env.seq, type: env.type, sid: env.sid ?? null, ts: env.ts ?? Date.now(), source: env.source, d });
      if (!raf) raf = requestAnimationFrame(flush);
    });
    return () => {
      off();
      cancelAnimationFrame(raf);
    };
  });

  const shown = $derived.by(() => {
    const f = q.trim().toLowerCase();
    const out = [];
    for (const r of rows) {
      if (hideNoise && NOISE.test(r.type)) continue;
      if (f && !(f.endsWith('*') ? r.type.startsWith(f.slice(0, -1)) : r.type.includes(f) || (r.sid ?? '').includes(f))) continue;
      out.push(r);
      if (out.length >= 300) break;
    }
    return out;
  });
  const noiseCount = $derived(rows.filter((r) => NOISE.test(r.type)).length);

  async function toggle(r) {
    if (open === r.seq) {
      open = null;
      return;
    }
    open = r.seq;
    if (r.d !== undefined) {
      detail = { seq: r.seq, data: r.d };
      return;
    }
    detail = { seq: r.seq, loading: true };
    try {
      const ev = await ctx.rpc('diag.event', { seq: r.seq });
      if (open === r.seq) detail = { seq: r.seq, data: ev?.data, source: ev?.source };
    } catch (e) {
      if (open === r.seq) detail = { seq: r.seq, error: e.message };
    }
  }

  const time = (ts) => new Date(ts).toLocaleTimeString('en-GB');
  const ms = (ts) => `.${String(new Date(ts).getMilliseconds()).padStart(3, '0')}`;
  const tone = (t) =>
    t.startsWith('agent.') ? 'accent' : t.startsWith('stream.') || t.startsWith('tool.') ? 'info' : t.startsWith('message') ? 'ok' : t.endsWith('.changed') ? 'warn' : '';
</script>

<div class="bar">
  <SearchInput bind:value={q} placeholder="Filter type or session" title="Event type (agent.* for a prefix) or session id" />
  <IconButton icon={paused ? 'play' : 'pause'} title={paused ? 'Resume' : 'Pause'} size="sm" pressed={paused} onclick={() => (paused = !paused)} />
  <IconButton icon="trash" title="Clear" size="sm" onclick={() => (rows = [])} />
</div>
<div class="opts np-line">
  <label class="np-check np-grow" title="Hide stream.delta, tool.output and process.output events"
    ><input type="checkbox" bind:checked={hideNoise} /> <span class="np-ellipsis">Hide output noise ({noiseCount})</span></label
  >
  <span class="state" class:paused title="Newest first">{paused ? 'paused' : 'live'}</span>
</div>
<div class="list np-mono">
  {#each shown as r (r.seq)}
    <button class="ev" class:open={open === r.seq} onclick={() => toggle(r)}>
      <span class="t">{time(r.ts)}<span class="ms">{ms(r.ts)}</span></span>
      <span class="ty" data-tone={tone(r.type)}>{r.type}</span>
      {#if r.sid}<span class="sid" title={r.sid}>{r.sid.slice(-6)}</span>{/if}
    </button>
    {#if open === r.seq && detail?.seq === r.seq}
      <div class="detail">
        {#if detail.loading}<span class="np-dim">loading…</span>
        {:else if detail.error}<span class="err">{detail.error}</span>
        {:else}<pre>{JSON.stringify(detail.data, null, 2)}</pre>{/if}
      </div>
    {/if}
  {:else}
    <Empty icon="activity">{rows.length ? 'No events match the filter' : 'Waiting for events…'}</Empty>
  {/each}
</div>

<style>
  .bar {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 4px 10px 2px 12px;
  }
  .bar :global(.np-search) {
    flex: 1;
  }
  .opts {
    padding: 2px 12px 6px;
    font-size: var(--fs-xs);
  }
  .opts .np-check {
    font-size: var(--fs-xs);
    color: var(--fg-muted);
    min-width: 0;
  }
  .state {
    color: var(--ok);
  }
  .state.paused {
    color: var(--warn);
  }
  .ms {
    color: var(--fg-dim);
    opacity: 0.7;
  }
  @container (max-width: 279px) {
    .ms {
      display: none;
    }
  }
  .list {
    padding: 0 6px 10px;
    font-size: 11px;
  }
  .ev {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    min-height: 20px;
    padding: 0 6px;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg);
    font: inherit;
    text-align: left;
    cursor: pointer;
  }
  .ev:hover,
  .ev.open {
    background: var(--bg-2);
  }
  .t {
    color: var(--fg-dim);
    flex: none;
  }
  .ty {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .ty[data-tone='accent'] {
    color: var(--accent);
  }
  .ty[data-tone='info'] {
    color: var(--info);
  }
  .ty[data-tone='ok'] {
    color: var(--ok);
  }
  .ty[data-tone='warn'] {
    color: var(--warn);
  }
  .sid {
    flex: none;
    color: var(--fg-dim);
  }
  .detail {
    margin: 2px 6px 6px;
    padding: 6px 8px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    max-height: 280px;
    overflow: auto;
  }
  .detail pre {
    margin: 0;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    color: var(--fg-muted);
  }
  .err {
    color: var(--err);
  }
</style>
