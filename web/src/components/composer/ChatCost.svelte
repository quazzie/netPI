<script>
  /** What this chat cost on paid models (usage.session), shown once it cost something; refreshed on usage.changed. */
  import { rpc, conn } from '../../lib/rpc.svelte.js';
  import { bus } from '../../lib/bus.js';
  import { usd } from '../../lib/format.js';

  let { sessionId } = $props();
  let cost = $state(null);
  let shown = null; // the chat `cost` belongs to

  // Reads conn.status too: a usage.changed missed while the socket was down is made up for when it opens again.
  $effect(() => {
    const id = sessionId;
    const up = conn.status === 'open';
    if (shown !== id) {
      cost = null;
      shown = id;
    }
    if (!up) return;
    let alive = true;
    const load = () =>
      rpc('usage.session', { sessionId: id })
        .then((c) => alive && (cost = c))
        .catch(() => {});
    load();
    const off = bus.on('usage.changed', load);
    return () => {
      alive = false;
      off();
    };
  });

  // settled cost: a call in flight counts at its reservation until it ends, which made the figure jump (idea-qbi2fv)
  const reserved = $derived(cost?.reservedUsd ?? 0);
  const total = $derived(Math.max(0, (cost?.withSubagentsUsd ?? 0) - reserved));
  const title = $derived(
    cost
      ? `This chat: ${usd(cost.costUsd)} (${cost.calls} model calls)` +
          (cost.withSubagentsUsd > cost.costUsd ? `; with its subagents ${usd(cost.withSubagentsUsd)}` : '') +
          (reserved > 0 ? `, of which ${usd(reserved)} is reserved for calls still running` : '')
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
