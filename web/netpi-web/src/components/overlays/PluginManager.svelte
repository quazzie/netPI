<script lang="ts">
  import { onMount } from "svelte";
  import { store } from "../../store.svelte";

  import { ws } from "../../ws";

  let busy = $state<string | null>(null);
  let refreshing = $state(false);

  function reload(id: string) {
    busy = id;
    store.setPluginReloadState(id, "draining");
    ws.request("plugin.reload", { pluginId: id })
      .then(() => {})
      .catch((e) => {
        store.setPluginReloadState(id, "failed");
        store.setError(e.message);
      })
      .finally(() => (busy = null));
  }

  function reloadAll() {
    for (const p of store.plugins) {
      if (p.state === "active") reload(p.id);
    }
  }

  onMount(() => {
    ws.request("plugins.list").catch(() => {});
  });
</script>

<div class="overlay" onclick={() => (store.overlay = null)}>

  <div class="panel" onclick={(e) => e.stopPropagation()}>
    <h2>
      <span>Plugins</span>
      <button class="close" onclick={() => (store.overlay = null)}>×</button>
    </h2>

    <div class="plugin-row head">
      <span>plugin</span><span>version</span><span>gen</span><span>state</span><span>leases</span><span>error</span><span></span>

    </div>

    {#each store.plugins as p (p.id)}
      <div class="plugin-row">
        <span>{p.name}</span>
        <span class="dim" style="color:var(--text-faint);font-size:11px">{p.version || ""}</span>

        <span>{p.generation}</span>
        <span class="badge {p.state}">{p.state}</span>
        <span>{p.activeLeases}</span>
        <span class="dim" style="color:var(--text-faint);font-size:11px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title={p.lastError ?? ""}>{p.lastError ?? ""}</span>
        <button
          class="btn"
          disabled={busy === p.id || p.state !== "active"}
          onclick={() => reload(p.id)}
        >{busy === p.id ? "…" : "Reload"}</button>
      </div>
    {/each}

    {#if !store.plugins.length}
      <div class="cap" style="color:var(--text-faint);padding:10px 0">no plugin state received yet</div>
    {/if}

    <div style="margin-top:14px;display:flex;justify-content:flex-end">
      <button class="btn" onclick={async () => { refreshing = true; try { await ws.request("plugins.list"); } finally { refreshing = false; } }}>{refreshing ? "…" : "Refresh"}</button>
      <button class="btn primary" onclick={reloadAll}>Reload All</button>

    </div>
  </div>
</div>
