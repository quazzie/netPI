<script>
  /**
   * The owner's Claude subscription usage in the top bar (the Claude CLI provider's monitor): the session
   * and weekly windows with a detail popover — every window with its reset time, extra-usage credits,
   * the week's breakdown by surface. The reading is the one the Claude CLI printed after its last call (it is not fetched
   * from anywhere). Hidden while the provider plugin is not loaded (the RPC is absent) or no call has produced a reading yet.
   */
  import Icon from '../lib/kit/Icon.svelte';
  import Popover from './Popover.svelte';
  import { rpc, conn } from '../lib/rpc.svelte.js';
  import { bus } from '../lib/bus.js';
  import { duration, timeAgo, usd, toMs } from '../lib/format.js';

  /**
   * compact: the composer's row (in place of the context ring in a Claude CLI session, where the ring's
   * reading is the CLI's own last call, not this chat): smaller, borderless, the popover opens upward.
   */
  let { compact = false } = $props();

  let s = $state(null);
  let open = $state(false);
  let el = $state();

  const load = () =>
    rpc('claudecli.usage')
      .then((x) => (s = x ?? null))
      .catch(() => {}); // the method is absent when the Claude CLI provider is not loaded: stay hidden

  $effect(() => {
    if (conn.status !== 'open') return;
    load();
    const offs = [
      bus.on('usage.claude', (d) => (s = d ?? s)),
      bus.on('plugins.changed', load),
      bus.on('settings.changed', load),
    ];
    return () => offs.forEach((off) => off());
  });

  const limits = $derived(s?.limits ?? []);
  const byKey = $derived(Object.fromEntries(limits.map((l) => [l.key, l])));
  const worst = $derived(limits.reduce((m, l) => Math.max(m, l.percent ?? 0), 0));
  const tone = $derived(worst >= 90 ? 'err' : worst >= 75 ? 'warn' : 'ok');
  const show = $derived(!!s && (limits.length > 0 || s.spend));

  const pillText = $derived.by(() => {
    const parts = [];
    if (byKey.session?.percent != null) parts.push(`${Math.round(byKey.session.percent)}%`);
    if (byKey.weekly_all?.percent != null) parts.push(`W ${Math.round(byKey.weekly_all.percent)}%`);
    return parts.join(' · ');
  });

  const tip = $derived(
    s
      ? `Claude subscription: ${limits.map((l) => `${l.label} ${Math.round(l.percent ?? 0)}%`).join(', ')}${s.stale ? ' (stale reading)' : ''}. Click for the detail.`
      : '',
  );

  function resetsIn(t) {
    const ms = toMs(t) - Date.now();
    return ms > 0 ? `resets in ${duration(ms)}` : 'resets soon';
  }

</script>

{#if show}
  <button bind:this={el} class="cu-pill" class:compact class:warn={tone === 'warn'} class:err={tone === 'err'} {tip}
    aria-expanded={open} aria-haspopup="dialog" onclick={() => (open = !open)}>
    <Icon name="zap" size={compact ? 12 : 13} />
    <span>{pillText || '—'}</span>
  </button>

  {#if open}
    <Popover anchor={el} placement={compact ? 'top-end' : 'bottom-end'} width={300} onclose={() => (open = false)}>
      <div class="cu">
        <div class="head np-line">
          <span class="h">Claude usage</span>
          <span class="np-grow"></span>
          {#if s.plan}
            <span class="chip np-mono">{s.plan}{s.tier ? ` · ${s.tier.replace('default_claude_', '')}` : ''}</span>
          {/if}
        </div>

        <div class="rows">
          {#each limits as l (l.key)}
            {@const pct = Math.min(100, l.percent ?? 0)}
            <div class="lim">
              <div class="lim-l np-line">
                <span>{l.label}</span>
                <b class="np-mono" data-tone={pct >= 90 ? 'err' : pct >= 75 ? 'warn' : 'ok'}>{Math.round(pct)}%</b>
              </div>
              <div class="bar" data-tone={pct >= 90 ? 'err' : pct >= 75 ? 'warn' : 'ok'}>
                <i style:width="{pct}%"></i>
              </div>
              {#if l.resetsAt}
                <div class="sub np-dim tiny">{resetsIn(l.resetsAt)}</div>
              {/if}
            </div>
          {/each}
        </div>

        {#if s.spend && (s.spend.enabled || s.spend.usedUsd)}
          <div class="sec">
            <div class="sec-h">Extra usage</div>
            <div class="row">
              <span>{s.spend.enabled ? 'Credits used' : 'Credits (disabled)'}</span>
              <b class="np-mono">
                {usd(s.spend.usedUsd)}{s.spend.limitUsd != null ? ` / ${usd(s.spend.limitUsd)}` : ''}
              </b>
            </div>
          </div>
        {/if}

        {#if s.breakdown?.length}
          <div class="sec">
            <div class="sec-h">This week by surface</div>
            <div class="bd np-dim small">
              {#each s.breakdown as b (b.key)}
                <span class="bd-item">{b.label} <b class="np-mono">{Math.round(b.percent ?? 0)}%</b></span>
              {/each}
            </div>
          </div>
        {/if}

        <div class="foot">
          <div class="np-dim tiny">
            {s.stale ? 'Old reading — ' : ''}as of the last Claude CLI call, {timeAgo(s.fetchedAt)}{s.error ? ` · ${s.error}` : ''}
          </div>
        </div>
      </div>
    </Popover>
  {/if}
{/if}

<style>
  .cu-pill {
    display: flex;
    align-items: center;
    gap: 5px;
    height: 24px;
    padding: 0 8px;
    border: 1px solid var(--border);
    border-radius: 12px;
    background: var(--bg-2);
    color: var(--fg-muted);
    font: inherit;
    font-size: var(--fs-xs);
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
    cursor: default;
  }
  .cu-pill:hover {
    filter: brightness(1.12);
  }
  .cu-pill.warn {
    border-color: color-mix(in srgb, var(--warn) 45%, var(--border));
    background: color-mix(in srgb, var(--warn) 8%, transparent);
    color: var(--warn);
  }
  .cu-pill.err {
    border-color: color-mix(in srgb, var(--err) 45%, var(--border));
    background: color-mix(in srgb, var(--err) 8%, transparent);
    color: var(--err);
  }
  /* the composer's row: the ring's own look (borderless, a hover background), so it sits where the ring sat */
  .cu-pill.compact {
    height: 22px;
    padding: 0 6px;
    border: 0;
    border-radius: 11px;
    background: transparent;
    font-size: 11px;
  }
  .cu-pill.compact:hover,
  .cu-pill.compact[aria-expanded='true'] {
    filter: none;
    background: var(--bg-2);
  }
  .cu-pill.compact.warn:hover,
  .cu-pill.compact.warn[aria-expanded='true'] {
    color: var(--warn);
  }
  .cu-pill.compact.err:hover,
  .cu-pill.compact.err[aria-expanded='true'] {
    color: var(--err);
  }

  /* the popout */
  .cu {
    display: flex;
    flex-direction: column;
    padding: 9px 11px 10px;
    font-size: var(--fs-sm);
  }
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .head .h {
    font-weight: 600;
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
    margin-top: 8px;
  }
  .lim-l {
    display: flex;
    align-items: baseline;
    gap: 6px;
    color: var(--fg-muted);
  }
  .lim-l b {
    font-weight: 500;
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
  .sec {
    margin-top: 9px;
    padding-top: 8px;
    border-top: 1px solid var(--border);
  }
  .sec-h {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .row {
    display: flex;
    align-items: baseline;
    gap: 8px;
    color: var(--fg-muted);
  }
  .row b {
    margin-left: auto;
    color: var(--fg);
    font-weight: 500;
  }
  .bd {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 12px;
    margin-top: 4px;
  }
  .bd-item b {
    color: var(--fg);
    font-weight: 500;
  }
  .foot {
    display: flex;
    flex-direction: column;
    gap: 5px;
    margin-top: 9px;
    padding-top: 8px;
    border-top: 1px solid var(--border);
  }
  .link {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    align-self: flex-start;
    height: 24px;
    padding: 0 8px;
    font-size: var(--fs-xs);
  }
  .tiny {
    font-size: 10px;
    line-height: 1.4;
  }
</style>
