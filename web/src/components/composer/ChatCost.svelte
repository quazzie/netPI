<script>
  /** What this chat cost on paid models (usage.session), shown once it cost something; refreshed on usage.changed. */
  import { rpc } from '../../lib/rpc.svelte.js';
  import { bus } from '../../lib/bus.js';
  import { usd } from '../../lib/format.js';

  let { sessionId } = $props();
  let cost = $state(null);

  $effect(() => {
    const id = sessionId;
    let alive = true;
    const load = () =>
      rpc('usage.session', { sessionId: id })
        .then((c) => alive && (cost = c))
        .catch(() => {});
    cost = null;
    load();
    const off = bus.on('usage.changed', load);
    return () => {
      alive = false;
      off();
    };
  });

  const total = $derived(cost?.withSubagentsUsd ?? 0);
  const title = $derived(
    cost
      ? `This chat: ${usd(cost.costUsd)} (${cost.calls} model calls)` +
          (cost.withSubagentsUsd > cost.costUsd ? `; with its subagents ${usd(cost.withSubagentsUsd)}` : '')
      : '',
  );
</script>

{#if total > 0}
  <span class="cost np-mono" {title}>{usd(total)}</span>
{/if}

<style>
  .cost {
    padding: 0 4px;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
</style>
