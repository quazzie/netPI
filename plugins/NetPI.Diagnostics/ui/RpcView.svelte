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
            <div class="row np-line">
              <button class="m np-mono np-grow" title="{r.method} (click to copy)" onclick={() => copyText(r.method)}>{r.method}</button>
              <span class="plug np-mono" title={r.pluginId}>{r.pluginId.replace(/^netpi\./, '')}</span>
            </div>
            {#if r.description}<div class="d" title={r.description}>{r.description}</div>{/if}
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
    gap: 8px;
  }
  .m {
    text-align: left;
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
  .row > .plug {
    flex: 0 1 auto;
    max-width: 45%;
    overflow: hidden;
    text-overflow: ellipsis;
    font-size: 10.5px;
    color: var(--fg-dim);
  }
  .d {
    font-size: var(--fs-xs);
    color: var(--fg-muted);
    line-height: 1.4;
    overflow-wrap: anywhere;
    display: -webkit-box;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    overflow: hidden;
  }
</style>
