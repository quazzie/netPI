<script>
  /**
   * The Claude subscription's usage, for the context ring's popout in a Claude CLI session: the session and weekly windows
   * (percent, bar, reset time) as the Claude CLI printed them after its last call (`claudecli.usage`, updated on
   * `usage.claude`), and when that was. Nothing is fetched: before the first call there is no reading.
   */
  import { rpc, conn } from '../lib/rpc.svelte.js';
  import { bus } from '../lib/bus.js';
  import { duration, timeAgo, toMs } from '../lib/format.js';

  let s = $state(null);

  const load = () =>
    rpc('claudecli.usage')
      .then((x) => (s = x ?? null))
      .catch(() => {}); // the method is absent when the Claude CLI provider is not loaded

  $effect(() => {
    if (conn.status !== 'open') return;
    load();
    const offs = [bus.on('usage.claude', (d) => (s = d ?? s)), bus.on('plugins.changed', load)];
    return () => offs.forEach((off) => off());
  });

  const limits = $derived(s?.limits ?? []);
  const tone = (pct) => (pct >= 90 ? 'err' : pct >= 75 ? 'warn' : 'ok');

  function resetsIn(t) {
    const ms = toMs(t) - Date.now();
    return ms > 0 ? `resets in ${duration(ms)}` : 'resets soon';
  }
</script>

<div class="cu">
  <div class="head">
    <span class="h">Claude usage</span>
    {#if s?.plan}<span class="chip np-mono">{s.plan}</span>{/if}
  </div>

  {#if limits.length}
    <div class="rows">
      {#each limits as l (l.key)}
        {@const pct = Math.min(100, l.percent ?? 0)}
        <div class="lim">
          <div class="lim-l">
            <span>{l.label}</span>
            <b class="np-mono" data-tone={tone(pct)}>{Math.round(pct)}%</b>
          </div>
          <div class="bar" data-tone={tone(pct)}><i style:width="{pct}%"></i></div>
          {#if l.resetsAt}<div class="sub np-dim tiny">{resetsIn(l.resetsAt)}</div>{/if}
        </div>
      {/each}
    </div>
    <div class="np-dim tiny foot">{s.stale ? 'Old reading — ' : ''}as of the last Claude CLI call, {timeAgo(s.fetchedAt)}</div>
  {:else}
    <div class="np-dim small">No reading yet: the Claude CLI reports its usage with each call.</div>
  {/if}
</div>

<style>
  .cu {
    display: flex;
    flex-direction: column;
    gap: 7px;
  }
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .head .h {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .chip {
    font-size: var(--fs-xs);
    padding: 1px 6px;
    border-radius: 8px;
    background: var(--bg-3);
    color: var(--fg-muted);
  }
  .rows {
    display: flex;
    flex-direction: column;
    gap: 7px;
  }
  .lim-l {
    display: flex;
    align-items: baseline;
    gap: 6px;
    color: var(--fg-muted);
  }
  .lim-l b {
    margin-left: auto;
    font-weight: 500;
    color: var(--fg);
  }
  .lim-l b[data-tone='warn'] {
    color: var(--warn);
  }
  .lim-l b[data-tone='err'] {
    color: var(--err);
  }
  .bar {
    height: 4px;
    margin-top: 3px;
    border-radius: 2px;
    background: var(--bg-3);
    overflow: hidden;
  }
  .bar i {
    display: block;
    height: 100%;
    border-radius: 2px;
    background: var(--fg-muted);
  }
  .bar[data-tone='warn'] i {
    background: var(--warn);
  }
  .bar[data-tone='err'] i {
    background: var(--err);
  }
  .sub {
    margin-top: 1px;
  }
  .small {
    font-size: var(--fs-xs);
    line-height: 1.4;
  }
  .tiny {
    font-size: 10px;
    line-height: 1.4;
  }
</style>
