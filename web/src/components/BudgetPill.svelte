<script>
  /**
   * The budget in the top bar once it needs attention: the warning level reached (budget.warnPercent) or spent
   * (budget.status, then usage.changed). A click opens Settings → Lanes & budget.
   */
  import Icon from '../lib/kit/Icon.svelte';
  import { rpc, conn } from '../lib/rpc.svelte.js';
  import { bus } from '../lib/bus.js';
  import { usd } from '../lib/format.js';
  import { modals } from '../lib/state/ui.svelte.js';

  let b = $state(null);
  const load = () =>
    rpc('budget.status')
      .then((s) => (b = s ?? null))
      .catch(() => (b = null));

  $effect(() => {
    if (conn.status !== 'open') return;
    load();
    const offs = [bus.on('usage.changed', (d) => (b = d ?? b)), bus.on('settings.changed', load), bus.on('plugins.changed', load)];
    return () => offs.forEach((off) => off());
  });

  const show = $derived(!!b && (b.warning || b.exhausted));
  const title = $derived(
    b
      ? `This month ${usd(b.spentUsd)}${b.monthlyUsd ? ` of ${usd(b.monthlyUsd)}` : ''}, today ${usd(b.todayUsd)}` +
          `${b.dailyUsd ? ` of ${usd(b.dailyUsd)}` : ''}. ` +
          (b.exhausted ? (b.onLimit === 'ask' ? 'Spent: chats ask before paid calls.' : 'Spent: paid calls stop.') : 'Near the budget.') +
          ' Click for the budget settings.'
      : '',
  );
</script>

{#if show}
  <button class="budget-pill" class:spent={b.exhausted} {title} onclick={() => (modals.settings = 'lanes')}>
    <Icon name="dollar" size={13} />
    {#if b.monthlyUsd}
      <span>{usd(b.spentUsd)}&nbsp;/ {usd(b.monthlyUsd)}</span>
    {:else}
      <span>{usd(b.todayUsd)}&nbsp;/ {usd(b.dailyUsd)} today</span>
    {/if}
  </button>
{/if}

<style>
  .budget-pill {
    display: flex;
    align-items: center;
    gap: 5px;
    height: 24px;
    padding: 0 8px;
    border: 1px solid color-mix(in srgb, var(--warn) 45%, var(--border));
    border-radius: 12px;
    background: color-mix(in srgb, var(--warn) 8%, transparent);
    color: var(--warn);
    font-size: var(--fs-xs);
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .budget-pill.spent {
    border-color: color-mix(in srgb, var(--err) 45%, var(--border));
    background: color-mix(in srgb, var(--err) 8%, transparent);
    color: var(--err);
  }
  .budget-pill:hover {
    filter: brightness(1.15);
  }
</style>
