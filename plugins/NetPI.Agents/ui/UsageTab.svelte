<script>
  import { Section, Empty, IconButton, tokens, usd, useRefresh } from '@netpi/kit';

  /**
   * What the models cost (usage.history, usage.chats): the chosen budget period's total against the limit, the last 30
   * days, each agent, each model and the chats that cost most, and every period so far with the all-time total. The
   * period picker and the months list choose which period the rest shows. Free and local models count in calls and
   * tokens but cost nothing, so a period of local work is still readable (the bars switch to tokens).
   * Designed for a 230–320 px panel: one column of rows, figures on the right, nothing wider than the panel.
   */
  let { ctx } = $props();

  let data = $state.raw(null); // usage.history
  let chats = $state.raw(null); // usage.chats of the same period
  let period = $state(''); // '' = the current one
  let failed = $state('');
  let loading = $state(true);
  let updatedAt = $state(null);

  async function load() {
    try {
      const h = await ctx.rpc('usage.history', { ...(period ? { period } : {}), days: 30 });
      if (!tab.alive) return;
      data = h;
      failed = '';
      updatedAt = new Date().toISOString();
      loadChats(h.period);
    } catch (e) {
      failed = e?.message ?? String(e);
    } finally {
      loading = false;
    }
  }
  // the one read that goes through the period's calls: when the period changes and on a refresh, not on every call recorded
  let chatsFor = '';
  async function loadChats(p, force = false) {
    if (!force && chatsFor === p && chats) return;
    chatsFor = p;
    try {
      const c = await ctx.rpc('usage.chats', { period: p, limit: 8 });
      if (tab.alive && chatsFor === p) chats = c;
    } catch {
      if (tab.alive && chatsFor === p) chats = null;
    }
  }

  // svelte-ignore state_referenced_locally
  const tab = useRefresh(ctx, { load, events: ['usage.changed'], pollMs: 60_000, delayMs: 1500 });
  /** Called by main.js (onShow / onHide). */
  export function setVisible(v) {
    tab.setVisible(v);
  }
  async function choose(p) {
    period = p === data?.current ? '' : p;
    chats = null;
    await tab.refresh();
  }
  async function refresh() {
    await tab.refresh();
    await loadChats(data?.period, true);
  }

  const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  const parts = (d) => d.split('-').map(Number); // [y, m, day]
  /** "Oct 2026" for a calendar month, "1 Oct – 30 Oct" for one that starts on another day. */
  function label(start, end) {
    const [y, m, d] = parts(start);
    if (d === 1) return `${MONTHS[m - 1]} ${y}`;
    const last = new Date(`${end}T00:00:00Z`); // the period ends the day before `end`
    last.setUTCDate(last.getUTCDate() - 1);
    return `${d} ${MONTHS[m - 1]} – ${last.getUTCDate()} ${MONTHS[last.getUTCMonth()]}`;
  }
  const since = (d) => {
    const [y, m] = parts(d);
    return `${MONTHS[m - 1]} ${y}`;
  };
  const dayLabel = (d) => {
    const [, m, day] = parts(d);
    return `${day} ${MONTHS[m - 1]}`;
  };

  const isCurrent = $derived(!!data && data.period === data.current);
  const totals = $derived(data?.totals ?? null);
  const budget = $derived(data?.budget ?? null);
  const limit = $derived(isCurrent && budget?.monthlyUsd ? budget.monthlyUsd : null);
  const frac = $derived(limit ? Math.min(1, (totals?.costUsd ?? 0) / limit) : null);
  const days = $derived(data?.days ?? []);
  const byCost = $derived(days.some((d) => d.costUsd > 0));
  const chartMax = $derived(Math.max(0.0000001, ...days.map((d) => (byCost ? d.costUsd : d.inputTokens + d.outputTokens))));
  const daysTotal = $derived(days.reduce((s, d) => s + d.costUsd, 0));
  const todayRow = $derived(days.at(-1) ?? null);
  const anyUsage = $derived(!!data && (data.allTime?.calls ?? 0) > 0);

  /** The share a row takes of the biggest one in its list, for its bar. */
  const share = (rows, key) => {
    const max = Math.max(0.0000001, ...rows.map((r) => r[key]));
    return (r) => r[key] / max;
  };
  const tokenSum = (r) => r.inputTokens + r.outputTokens;
  const agentRows = $derived(data?.agents ?? []);
  const modelRows = $derived(data?.models ?? []);
  const rowKey = (rows) => (rows.some((r) => r.costUsd > 0) ? 'costUsd' : 'calls');
  const agentShare = $derived(share(agentRows, rowKey(agentRows)));
  const modelShare = $derived(share(modelRows, rowKey(modelRows)));
  let showAllModels = $state(false);
</script>

<div class="usage">
  <div class="head">
    {#if data}
      <select class="pick" value={data.period} onchange={(e) => choose(e.currentTarget.value)} aria-label="Period" title="The budget period shown">
        {#each data.periods as p (p.period)}
          <option value={p.period}>{label(p.period, p.end)}{p.period === data.current ? ' · now' : ''}</option>
        {/each}
      </select>
    {:else}
      <span class="np-grow"></span>
    {/if}
    <span class="np-grow"></span>
    <IconButton icon="refresh" size="sm" title={updatedAt ? `Refresh (updated ${new Date(updatedAt).toLocaleTimeString()})` : 'Refresh'} onclick={refresh} />
  </div>

  {#if failed && !data}
    <Empty icon="alert">Usage unavailable: {failed}</Empty>
  {:else if loading && !data}
    <Empty><span class="np-spinner"></span></Empty>
  {:else if data && !anyUsage}
    <Empty icon="dollar">No model call has been recorded yet.</Empty>
  {:else if data}
    <!-- ------------------------------------------------------------ the period: what it cost, against the limit -->
    <div class="top">
      <div class="np-line np-baseline">
        <span class="big np-mono" class:warn={isCurrent && budget?.warning && !budget?.exhausted} class:err={isCurrent && budget?.exhausted}>{usd(totals.costUsd) || '$0'}</span>
        {#if limit}<span class="np-dim np-mono">&nbsp;of {usd(limit)}</span>{/if}
        <span class="np-grow"></span>
        <span class="np-dim small">{label(data.period, data.end)}</span>
      </div>
      {#if frac != null}
        <div class="np-progress" style="--value: {frac}" data-tone={budget.exhausted ? 'err' : budget.warning ? 'warn' : undefined}></div>
      {/if}
      <div class="figs">
        <span><b>{totals.calls.toLocaleString()}</b> calls</span>
        <span><b>{tokens(totals.inputTokens)}</b> in</span>
        <span><b>{tokens(totals.outputTokens)}</b> out</span>
        {#if totals.cacheReadTokens}<span title="Input read from the prompt cache"><b>{tokens(totals.cacheReadTokens)}</b> cached</span>{/if}
      </div>
      {#if isCurrent}
        <div class="figs">
          <span>today <b>{usd(budget?.todayUsd ?? todayRow?.costUsd ?? 0) || '$0'}</b>{#if budget?.dailyUsd}<span class="np-dim">&nbsp;of {usd(budget.dailyUsd)}</span>{/if}</span>
          {#if todayRow}<span><b>{todayRow.calls.toLocaleString()}</b> calls</span>{/if}
        </div>
      {/if}
      {#if totals.unknownCalls}
        <div class="note" title="Cloud models without a known price: set a price on the agent (Settings → Agents) to count them">
          {totals.unknownCalls.toLocaleString()} call{totals.unknownCalls === 1 ? '' : 's'} with an unknown price, not in the total
        </div>
      {/if}
    </div>

    <!-- ------------------------------------------------------------ the last 30 days -->
    <Section title="Last 30 days" count={byCost ? usd(daysTotal) : tokens(days.reduce((s, d) => s + tokenSum(d), 0)) + ' tok'} collapsible storageKey="usage.days">
      <div class="chart" role="img" aria-label="Daily {byCost ? 'cost' : 'tokens'}">
        {#each days as d (d.day)}
          {@const v = byCost ? d.costUsd : tokenSum(d)}
          <div
            class="bar"
            class:zero={v === 0}
            class:today={d.day === days.at(-1)?.day}
            style="--h: {v / chartMax}"
            title="{dayLabel(d.day)} — {usd(d.costUsd) || '$0'} · {d.calls.toLocaleString()} calls · {tokens(tokenSum(d))} tok"
          ></div>
        {/each}
      </div>
      <div class="axis np-dim"><span>{dayLabel(days[0].day)}</span><span>{byCost ? `max ${usd(chartMax)}` : `max ${tokens(chartMax)} tok`}</span><span>today</span></div>
    </Section>

    <!-- ------------------------------------------------------------ who spent it -->
    {#if agentRows.length}
      <Section title="Agents" count={agentRows.length} collapsible storageKey="usage.agents">
        {#each agentRows as r (r.agent ?? '')}
          <div class="row" title="{r.calls.toLocaleString()} calls · {tokens(r.inputTokens)} in · {tokens(r.outputTokens)} out{r.cacheReadTokens ? ` · ${tokens(r.cacheReadTokens)} cached` : ''}">
            <div class="np-line np-baseline">
              <span class="np-grow np-ellipsis name">{r.agent ?? 'no agent'}</span>
              <span class="np-mono val">{r.costUsd > 0 ? usd(r.costUsd) : 'free'}</span>
            </div>
            <div class="np-line sub np-dim"><span class="np-grow np-ellipsis">{r.calls.toLocaleString()} calls · {tokens(tokenSum(r))} tok</span></div>
            <div class="meter"><i style="width: {Math.max(2, agentShare(r) * 100)}%"></i></div>
          </div>
        {/each}
      </Section>
    {/if}

    {#if modelRows.length}
      <Section title="Models" count={modelRows.length} collapsible storageKey="usage.models">
        {#each showAllModels ? modelRows : modelRows.slice(0, 6) as r (r.provider + '|' + r.model)}
          <div class="row" title="{r.provider}/{r.model} — {r.calls.toLocaleString()} calls · {tokens(r.inputTokens)} in · {tokens(r.outputTokens)} out">
            <div class="np-line np-baseline">
              <span class="np-grow np-ellipsis name">{r.model}</span>
              <span class="np-mono val">{r.costUsd > 0 ? usd(r.costUsd) : r.unknownCalls ? '?' : 'free'}</span>
            </div>
            <div class="np-line sub np-dim"><span class="np-grow np-ellipsis">{r.provider} · {r.calls.toLocaleString()} calls · {tokens(tokenSum(r))} tok</span></div>
            <div class="meter"><i style="width: {Math.max(2, modelShare(r) * 100)}%"></i></div>
          </div>
        {/each}
        {#if modelRows.length > 6}
          <button class="more" onclick={() => (showAllModels = !showAllModels)}>{showAllModels ? 'Show less' : `Show all ${modelRows.length}`}</button>
        {/if}
      </Section>
    {/if}

    <!-- ------------------------------------------------------------ the chats that cost most -->
    <Section title="Chats" count={chats?.chatCount ?? ''} collapsible storageKey="usage.chats">
      {#if !chats}
        <div class="na">…</div>
      {:else if !chats.chats.length}
        <div class="na">No chat in this period.</div>
      {:else}
        {#each chats.chats as c (c.sessionId)}
          <button class="row chat" disabled={c.deleted} onclick={() => ctx.app.openSession(c.sessionId)} title={c.deleted ? 'This chat was deleted' : 'Open the chat'}>
            <div class="np-line np-baseline">
              <span class="np-grow np-ellipsis name" class:gone={c.deleted}>{c.title || (c.deleted ? 'deleted chat' : 'untitled')}</span>
              <span class="np-mono val">{c.costUsd > 0 ? usd(c.costUsd) : 'free'}</span>
            </div>
            <div class="np-line sub np-dim"><span class="np-grow np-ellipsis">{c.project ? `${c.project} · ` : ''}{c.calls.toLocaleString()} calls · {tokens(tokenSum(c))} tok</span></div>
          </button>
        {/each}
        {#if chats.noChat?.calls}
          <div class="np-dim small pad">{chats.noChat.calls.toLocaleString()} calls outside any chat ({usd(chats.noChat.costUsd) || '$0'})</div>
        {/if}
        {#if chats.truncated}<div class="np-dim small pad">Only the first {(200000).toLocaleString()} calls of the period were ranked.</div>{/if}
      {/if}
    </Section>

    <!-- ------------------------------------------------------------ every period so far -->
    <Section title="Months" count={data.periods.length} collapsible storageKey="usage.months">
      {#each data.periods as p (p.period)}
        <button class="row month" class:on={p.period === data.period} onclick={() => choose(p.period)}>
          <div class="np-line np-baseline">
            <span class="np-grow np-ellipsis name">{label(p.period, p.end)}{p.period === data.current ? ' · now' : ''}</span>
            <span class="np-mono val">{p.costUsd > 0 ? usd(p.costUsd) : 'free'}</span>
          </div>
          <div class="np-line sub np-dim"><span class="np-grow np-ellipsis">{p.calls.toLocaleString()} calls · {tokens(p.inputTokens + p.outputTokens)} tok</span></div>
        </button>
      {/each}
      <div class="all np-line np-baseline">
        <span class="np-grow">All time{data.allTime.since ? ` · since ${since(data.allTime.since)}` : ''}</span>
        <span class="np-mono"><b>{usd(data.allTime.costUsd) || '$0'}</b></span>
      </div>
      <div class="np-dim small pad">{data.allTime.calls.toLocaleString()} calls · {tokens(data.allTime.inputTokens)} in · {tokens(data.allTime.outputTokens)} out</div>
    </Section>
  {/if}
</div>

<style>
  .usage {
    flex: 1 0 auto;
    display: flex;
    flex-direction: column;
    font-size: var(--fs-sm);
  }
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 6px 4px 12px;
    border-bottom: 1px solid var(--border);
  }
  .pick {
    min-width: 0;
    max-width: 62%;
    height: 24px;
    padding: 0 4px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg-1);
    color: var(--fg);
    font: inherit;
  }
  .top {
    display: flex;
    flex-direction: column;
    gap: 5px;
    padding: 10px 12px 10px;
    border-bottom: 1px solid var(--border);
  }
  .big {
    font-size: 22px;
    font-weight: 600;
    line-height: 1.1;
    color: var(--fg);
  }
  .big.warn {
    color: var(--warn);
  }
  .big.err {
    color: var(--err);
  }
  .small {
    font-size: var(--fs-xs);
  }
  .figs {
    display: flex;
    flex-wrap: wrap;
    gap: 2px 12px;
    color: var(--fg-muted);
  }
  .figs b {
    color: var(--fg);
    font-weight: 600;
    font-variant-numeric: tabular-nums;
  }
  .note {
    color: var(--warn);
    font-size: var(--fs-xs);
  }
  .chart {
    display: flex;
    align-items: flex-end;
    gap: 2px;
    height: 56px;
    padding-top: 2px;
  }
  .bar {
    flex: 1 1 0;
    min-width: 2px;
    height: calc(max(2px, var(--h) * 100%));
    border-radius: 2px 2px 0 0;
    background: var(--accent);
    opacity: 0.75;
  }
  .bar.zero {
    background: var(--border);
    opacity: 1;
  }
  .bar.today {
    opacity: 1;
  }
  .bar:hover {
    opacity: 1;
    outline: 1px solid var(--accent-line, var(--accent));
  }
  .axis {
    display: flex;
    justify-content: space-between;
    margin-top: 3px;
    font-size: var(--fs-xs);
  }
  .row {
    display: flex;
    flex-direction: column;
    gap: 1px;
    width: 100%;
    padding: 4px 0 5px;
    border: 0;
    background: none;
    color: inherit;
    font: inherit;
    text-align: left;
  }
  button.row {
    padding-left: 4px;
    padding-right: 4px;
    margin-left: -4px;
    width: calc(100% + 8px);
    border-radius: 6px;
    cursor: pointer;
  }
  button.row:hover:not(:disabled),
  button.row.on {
    background: var(--bg-2);
  }
  button.row:disabled {
    cursor: default;
  }
  .name {
    color: var(--fg);
  }
  .name.gone {
    color: var(--fg-dim);
    font-style: italic;
  }
  .val {
    flex: none;
    margin-left: 8px;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
  }
  .sub {
    font-size: var(--fs-xs);
  }
  .meter {
    height: 3px;
    margin-top: 2px;
    border-radius: 2px;
    background: var(--bg-3);
    overflow: hidden;
  }
  .meter i {
    display: block;
    height: 100%;
    background: var(--accent);
    opacity: 0.7;
  }
  .more {
    margin-top: 2px;
    padding: 2px 0;
    border: 0;
    background: transparent;
    color: var(--accent);
    font-size: var(--fs-xs);
    cursor: pointer;
  }
  .na {
    padding: 2px 0;
    color: var(--fg-dim);
  }
  .pad {
    padding-top: 3px;
  }
  .all {
    margin-top: 6px;
    padding-top: 6px;
    border-top: 1px dashed var(--border);
    color: var(--fg-muted);
  }
  .all b {
    color: var(--fg);
  }
</style>
