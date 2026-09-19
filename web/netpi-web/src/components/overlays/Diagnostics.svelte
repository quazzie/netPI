<script lang="ts">
  import { store } from "../../store.svelte";

  let dbPath = $state("C:\\Users\\quazz\\.netpi\\netpi.db");
</script>

<div class="overlay" onclick={() => (store.overlay = null)}>
  <div class="panel" onclick={(e) => e.stopPropagation()}>
    <h2>
      <span>Diagnostics</span>
      <button class="close" onclick={() => (store.overlay = null)}>×</button>
    </h2>
    <div class="kv">
      <span class="k">connection</span>
      <span class="v">{store.connection}</span>
      <span class="k">agent state</span>
      <span class="v">{store.agentState}</span>
      <span class="k">model catalog age</span>
      <span class="v">{store.models.length} models cached</span>
      <span class="k">database</span>
      <span class="v">{dbPath}</span>
      <span class="k">plugins loaded</span>
      <span class="v">{store.plugins.length} ({store.plugins.filter((p) => p.state === "active").length} active)</span>
    </div>
    <div style="margin-top:14px">
      <div class="title" style="font-size:11px;text-transform:uppercase;letter-spacing:.5px;color:var(--text-faint);margin-bottom:6px">plugins</div>
      {#each store.plugins as p (p.id)}
        <div style="display:flex;gap:10px;font-size:12.5px;padding:3px 0">
          <span style="width:220px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">{p.name}</span>
          <span class="badge {p.state}">{p.state}</span>
          <span style="color:var(--text-faint)">gen {p.generation} · {p.activeLeases} leases</span>
          {#if p.lastError}<span style="color:var(--red)">{p.lastError}</span>{/if}
        </div>
      {/each}
    </div>
  </div>
</div>
