<script>
  /** The budget and what this period cost, per model (usage.summary; refreshed on usage.changed and settings.changed). */
  import { rpc } from '../../lib/rpc.svelte.js';
  import { bus } from '../../lib/bus.js';
  import { tokens, usd } from '../../lib/format.js';

  let status = $state(null);
  let models = $state([]);

  async function load() {
    try {
      const s = await rpc('usage.summary');
      status = s?.budget ?? null;
      models = s?.models ?? [];
    } catch {
      status = null;
    }
  }
  $effect(() => {
    load();
    const offs = [bus.on('usage.changed', load), bus.on('settings.changed', load)];
    return () => offs.forEach((off) => off());
  });

  const pct = $derived(status?.monthlyUsd ? Math.min(100, (status.spentUsd / status.monthlyUsd) * 100) : 0);
  const state = $derived(status?.exhausted ? 'err' : status?.warning ? 'warn' : 'ok');
</script>

{#if status}
  <div class="budget" data-state={state}>
    <div class="row">
      <span class="lbl">This month</span>
      <span class="np-dim">since {status.periodStart}</span>
      <span class="grow"></span>
      <b class="np-mono">{usd(status.spentUsd)}</b>{#if status.monthlyUsd}<span class="np-dim">&nbsp;of {usd(status.monthlyUsd)}</span>{/if}
    </div>
    {#if status.monthlyUsd}<div class="bar"><span style="width: {pct}%"></span></div>{/if}
    <div class="row">
      <span class="lbl">Today</span>
      <span class="grow"></span>
      <b class="np-mono">{usd(status.todayUsd)}</b>{#if status.dailyUsd}<span class="np-dim">&nbsp;of {usd(status.dailyUsd)}</span>{/if}
    </div>
    {#if status.exhausted}
      <div class="note err">
        The budget is spent: paid models are stopped{status.onLimit === 'ask' ? '; a chat can be allowed to go over' : ''}. Free and local ones keep working.
      </div>
    {:else if status.warning}
      <div class="note warn">Over {status.warnPercent} % of the budget: agents use paid lanes only when you ask.</div>
    {/if}
    {#if models.length}
      <table class="models">
        <thead><tr><th>This period</th><th>calls</th><th>tokens</th><th>cost</th></tr></thead>
        <tbody>
          {#each models as m (`${m.agent}|${m.provider}|${m.model}`)}
            <tr>
              <td class="np-mono np-ellipsis" title="{m.provider}/{m.model}">{m.provider}/{m.model}{#if m.agent}<span class="np-dim">&nbsp;· {m.agent}</span>{/if}</td>
              <td>{m.calls}</td>
              <td>{tokens((m.inputTokens ?? 0) + (m.outputTokens ?? 0) + (m.cacheWriteTokens ?? 0))}</td>
              <td class="np-mono" title={m.unknownCost ? 'Some calls had no known price' : ''}>{usd(m.costUsd) || '–'}{m.unknownCost ? ' ?' : ''}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}
  </div>
{/if}

<style>
  .budget {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin: 4px 0 14px;
    padding: 10px 12px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-1);
    font-size: var(--fs-sm);
  }
  .row {
    display: flex;
    align-items: baseline;
    gap: 8px;
  }
  .lbl {
    font-weight: 550;
  }
  .grow {
    flex: 1;
  }
  .bar {
    height: 6px;
    border-radius: 3px;
    background: var(--bg-3);
    overflow: hidden;
  }
  .bar span {
    display: block;
    height: 100%;
    background: var(--ok);
  }
  [data-state='warn'] .bar span {
    background: var(--warn);
  }
  [data-state='err'] .bar span {
    background: var(--err);
  }
  .note {
    font-size: var(--fs-xs);
  }
  .note.warn {
    color: var(--warn);
  }
  .note.err {
    color: var(--err);
  }
  .models {
    width: 100%;
    margin-top: 4px;
    border-collapse: collapse;
    table-layout: fixed;
    font-size: var(--fs-xs);
  }
  .models th {
    color: var(--fg-dim);
    font-weight: 500;
    text-align: right;
  }
  .models th:first-child,
  .models td:first-child {
    text-align: left;
    width: 55%;
  }
  .models td {
    padding: 2px 0;
    text-align: right;
  }
</style>
