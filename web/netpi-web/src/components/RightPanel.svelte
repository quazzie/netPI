<script lang="ts">
  import { store } from "../store.svelte";
  import { ui } from "../ui.svelte";
  import { setActivePanel, getActivePanel } from "../panel-bridge";

  // The tab list is entirely plugin-contributed (the ui.panels catalog) —
  // the shell has no hardcoded tabs. Panels today: Diagnostics (which folds
  // in the former "Plugins" view) and the combined Work panel (id "background",
  // registered by NetPI.Activity); see docs/web-panels.md.
  let selectedPanel = $derived(store.webPanels.find((p) => p.id === ui.rightTab));

  $effect(() => {
    // A hot reload can drop a tab; fall back to the first available panel.
    if (ui.rightOpen && store.webPanels.length > 0 && !store.webPanels.some((p) => p.id === ui.rightTab))
      ui.setRightTab(store.webPanels[0].id, true);
  });

  // ---- astra-2 §12.3: register the live panel iframe with the bridge -------
  // The iframe is keyed by the panel entry URL, so a panel swap remounts it and
  // the bridge re-registers the new contentWindow + origin. On unmount the
  // registration is cleared, so a stale frame can never navigate.
  let iframeEl: HTMLIFrameElement | null = $state(null);

  function panelOrigin(entryUrl: string): string {
    try { return new URL(entryUrl, window.location.href).origin; }
    catch { return ""; }
  }

  function registerFrame(el: HTMLIFrameElement) {
    const panel = selectedPanel;
    if (!panel || !ui.rightOpen) return;
    const origin = panelOrigin(panel.entryUrl);
    const win = el.contentWindow;
    if (!origin || !win) return;
    // Adopt the loaded frame. A panel swap reuses the same bind target, so the
    // {#key} remount produces a fresh contentWindow we must re-register (the
    // cleanup below clears the old one first).
    const cur = getActivePanel();
    if (cur && cur.entryUrl === panel.entryUrl && cur.win === win) return;
    setActivePanel({ win, entryUrl: panel.entryUrl, origin });
    // One-time handshake: hand the panel the shell's origin so it can post
    // navigation back to it. targetOrigin is the panel's OWN origin (the
    // registered frame) — never "*", never a hardcoded port. The page caches
    // it from `netpi.panel.init`; if a load raced the handshake it still polls
    // (and re-receives on the next registration).
    try { win.postMessage({ type: "netpi.panel.init", version: 1 }, origin); }
    catch { /* frame not ready yet — it will catch up on the next registration */ }
  }

  // De-register the frame whenever it stops being the mounted panel. The
  // {#key} remount (panel swap) and the {#if} unmount (panel close) both run
  // this cleanup before the new frame's onload can re-register, so a stale
  // frame can never navigate. We check the LIVE store state (not the captured
  // one) because by the time the cleanup runs the state has already moved on.
  $effect(() => {
    const entry = selectedPanel?.entryUrl ?? null;
    return () => {
      if (!entry) return;
      const cur = getActivePanel();
      if (!cur || cur.entryUrl !== entry) return;
      const stillMounted = ui.rightOpen && selectedPanel?.entryUrl === entry;
      if (!stillMounted) setActivePanel(null);
    };
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
          <!-- astra-2 §12.3: key by entryUrl so a panel swap remounts the
               iframe; onload re-registers the new frame with the bridge (the old
               one is de-registered by the $effect cleanup above). -->
          {#key selectedPanel.entryUrl}
            <iframe
              bind:this={iframeEl}
              title={selectedPanel.title}
              src={selectedPanel.entryUrl}
              onload={(e) => registerFrame(e.currentTarget as HTMLIFrameElement)}
            ></iframe>
          {/key}
        </div>
      {:else}
        <div class="right-panel-empty">No panels available.</div>
      {/if}
    </div>
  {/if}
</aside>
