<script lang="ts">
  import { store } from "../store.svelte";
  import { ui } from "../ui.svelte";

  // The tab list is entirely plugin-contributed (the ui.panels catalog) —
  // the shell has no hardcoded tabs. NetPI.Web self-registers its
  // "plugins"/"diagnostics" panels; see docs/web-panels.md.
  let selectedPanel = $derived(store.webPanels.find((p) => p.id === ui.rightTab));

  $effect(() => {
    // A hot reload can drop a tab; fall back to the first available panel.
    if (ui.rightOpen && store.webPanels.length > 0 && !store.webPanels.some((p) => p.id === ui.rightTab))
      ui.setRightTab(store.webPanels[0].id, true);
  });

  function select(id: string) {
    if (ui.rightTab === id && ui.rightOpen) {
      ui.toggleRight();
      return;
    }
    ui.setRightTab(id, true);
  }
</script>

<aside class:open={ui.rightOpen} class="right-panel">
  <nav class="right-tab-rail" aria-label="Right panel">
    <button class="right-collapse" title={ui.rightOpen ? "Collapse right panel" : "Open right panel"} onclick={() => ui.toggleRight()}>
      {ui.rightOpen ? "›" : "‹"}
    </button>

    {#each store.webPanels as panel (panel.id)}
      <button
        class="vertical-tab"
        class:active={ui.rightOpen && ui.rightTab === panel.id}
        title={panel.title}
        onclick={() => select(panel.id)}
      >
        <span class="vertical-tab-content">
          <span class="vertical-tab-title">{panel.title}</span>
          <span class="vertical-tab-icon">{panel.icon}</span>
        </span>
      </button>
    {/each}
    <span class="rail-spacer"></span>
  </nav>

  {#if ui.rightOpen}
    <div class="right-panel-content">
      {#if selectedPanel}
        <div class="plugin-panel-frame">
          <div class="panel-title-row compact">
            <div>
              <h2>{selectedPanel.title}</h2>
              <p>Provided by a hot-reloadable plugin.</p>
            </div>
          </div>
          <iframe title={selectedPanel.title} src={selectedPanel.entryUrl}></iframe>
        </div>
      {:else}
        <div class="right-panel-empty">No panels available.</div>
      {/if}
    </div>
  {/if}
</aside>
