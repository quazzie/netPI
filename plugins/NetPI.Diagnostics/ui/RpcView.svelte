<script>
  import { SearchInput, Empty, copyText } from '@netpi/kit';
  let { rpcs } = $props();
  let q = $state('');
  const filtered = $derived(
    (rpcs ?? []).filter((r) => !q || `${r.method} ${r.pluginId} ${r.description ?? ''}`.toLowerCase().includes(q.toLowerCase())),
  );
  const groups = $derived.by(() => {
    const m = new Map();
    for (const r of filtered) {
      const g = r.method.split('.')[0];
      if (!m.has(g)) m.set(g, []);
      m.get(g).push(r);
    }
    return [...m];
  });
</script>

<div class="bar"><SearchInput bind:value={q} placeholder="Filter RPC methods" /></div>
{#if !rpcs}
  <Empty>RPC list unavailable</Empty>
{:else}
  <div class="list">
    {#each groups as [g, arr] (g)}
      <div class="grp">
        {#each arr as r (r.method)}
          <div class="rpc">
            <div class="row">
              <button class="m np-mono" title="Copy method name" onclick={() => copyText(r.method)}>{r.method}</button>
              <span class="np-spacer"></span>
              <span class="plug np-mono">{r.pluginId}</span>
            </div>
            {#if r.description}<div class="d">{r.description}</div>{/if}
          </div>
        {/each}
      </div>
    {:else}
      <Empty>No methods match “{q}”</Empty>
    {/each}
  </div>
{/if}

<style>
  .bar {
    padding: 4px 10px 4px 12px;
  }
  .list {
    padding: 2px 10px 10px 12px;
  }
  .grp {
    padding: 4px 0;
    border-bottom: 1px solid color-mix(in srgb, var(--border) 60%, transparent);
  }
  .rpc {
    padding: 2px 0;
  }
  .row {
    display: flex;
    align-items: baseline;
    gap: 8px;
  }
  .m {
    padding: 0;
    border: 0;
    background: transparent;
    color: var(--accent);
    font-size: 12px;
    cursor: copy;
  }
  .m:hover {
    text-decoration: underline;
  }
  .plug {
    font-size: 10.5px;
    color: var(--fg-dim);
  }
  .d {
    font-size: var(--fs-xs);
    color: var(--fg-muted);
    line-height: 1.4;
    overflow-wrap: anywhere;
  }
</style>
