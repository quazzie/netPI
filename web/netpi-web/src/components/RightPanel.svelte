<script lang="ts">
  import { store } from "../store.svelte";
  import { ui } from "../ui.svelte";
  import { ws } from "../ws";

  type BuiltinTab = { id: string; title: string; icon: string };
  const builtins: BuiltinTab[] = [
    { id: "plugins", title: "Plugins", icon: "◇" },
    { id: "diagnostics", title: "Diagnostics", icon: "◌" },
  ];

  let allTabs = $derived([
    ...builtins,
    ...store.webPanels.map((p) => ({ id: p.id, title: p.title, icon: p.icon || "□" })),
  ]);

  let selectedPlugin = $derived(store.webPanels.find((p) => p.id === ui.rightTab));

  $effect(() => {
    // Bootstrap/plugin reload events keep the panel catalog current. This
    // effect only guards against a tab disappearing during hot reload.
    if (ui.rightOpen && !allTabs.some((t) => t.id === ui.rightTab))
      ui.setRightTab("plugins", true);
  });

  function select(id: string) {
    if (ui.rightTab === id && ui.rightOpen) {
      ui.toggleRight();
      return;
    }
    ui.setRightTab(id, true);
    if (id === "plugins") ws.request("plugins.list", {}).catch(() => {});
  }

  function reload(id: string) {
    store.setPluginReloadState(id, "draining");
    ws.request("plugin.reload", { pluginId: id })
      .catch((e) => store.setError(String(e)));
  }

  function reloadAll() {
    ws.request("plugin.reloadAll", {}).catch((e) => store.setError(String(e)));
  }

  function fmtTokens(n?: number): string {
    if (!n) return "—";
    return n >= 1_000_000 ? (n / 1_000_000).toFixed(1) + "M"
      : n >= 1000 ? Math.round(n / 1000) + "k" : String(n);
  }
</script>

<aside class:open={ui.rightOpen} class="right-panel">
  <nav class="right-tab-rail" aria-label="Right panel">
    <button class="right-collapse" title={ui.rightOpen ? "Collapse right panel" : "Open right panel"} onclick={() => ui.toggleRight()}>
      {ui.rightOpen ? "›" : "‹"}
    </button>

    {#each allTabs as tab (tab.id)}
      <button
        class="vertical-tab"
        class:active={ui.rightOpen && ui.rightTab === tab.id}
        title={tab.title}
        onclick={() => select(tab.id)}
      >
        <span class="vertical-tab-content">
          <span class="vertical-tab-title">{tab.title}</span>
          <span class="vertical-tab-icon">{tab.icon}</span>
        </span>
      </button>
    {/each}
    <span class="rail-spacer"></span>
  </nav>

  {#if ui.rightOpen}
    <div class="right-panel-content">
      {#if ui.rightTab === "plugins"}
        <div class="panel-title-row">
          <div>
            <h2>Plugins</h2>
            <p>Hot-reloadable netPI extensions.</p>
          </div>
          <button class="btn" onclick={reloadAll}>Reload all</button>
        </div>

        <div class="side-plugin-list">
          {#each store.plugins as p (p.id)}
            <div class="side-plugin-row">
              <div class="plugin-main">
                <div class="plugin-title">{p.name || p.id}</div>
                <div class="plugin-meta">{p.id} · gen {p.generation}</div>
                {#if p.lastError}<div class="plugin-error">{p.lastError}</div>{/if}
              </div>
              <div class="plugin-actions">
                <span class="badge {p.state}">{p.state}</span>
                <button class="mini-btn" disabled={p.state === "draining" || p.state === "unloading"} onclick={() => reload(p.id)}>↻</button>
              </div>
            </div>
          {/each}
        </div>
      {:else if ui.rightTab === "diagnostics"}
        <div class="panel-title-row">
          <div>
            <h2>Diagnostics</h2>
            <p>Current harness state.</p>
          </div>
        </div>
        <div class="diagnostic-grid">
          <span>Connection</span><strong>{store.connection}</strong>
          <span>Agent</span><strong>{store.agentState}</strong>
          <span>Session</span><strong>{store.session?.title || "—"}</strong>
          <span>Workspace</span><strong title={store.session?.workspace ?? ""}>{store.session?.workspace || "—"}</strong>
          <span>Model</span><strong>{store.currentModel || "—"}</strong>
          <span>Reasoning</span><strong>{store.reasoningLevel || "off / default"}</strong>
          <span>Prompt tokens</span><strong>{fmtTokens(store.lastUsage?.promptTokens)}</strong>
          <span>Total tokens</span><strong>{fmtTokens(store.lastUsage?.totalTokens)}</strong>
        </div>
      {:else if selectedPlugin}
        <div class="plugin-panel-frame">
          <div class="panel-title-row compact">
            <div>
              <h2>{selectedPlugin.title}</h2>
              <p>Provided by a hot-reloadable plugin.</p>
            </div>
          </div>
          <iframe title={selectedPlugin.title} src={selectedPlugin.entryUrl}></iframe>
        </div>
      {:else}
        <div class="right-panel-empty">This panel is no longer available.</div>
      {/if}
    </div>
  {/if}
</aside>
