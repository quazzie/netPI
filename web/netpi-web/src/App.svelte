<script lang="ts">
  import { store } from "./store.svelte";
  import { ui } from "./ui.svelte";
  import "./ws";
  import LeftPanel from "./components/LeftPanel.svelte";
  import RightPanel from "./components/RightPanel.svelte";
  import HarnessHeader from "./components/HarnessHeader.svelte";
  import SessionTabs from "./components/SessionTabs.svelte";
  import ConversationViewport from "./components/ConversationViewport.svelte";
  import Composer from "./components/Composer.svelte";
  import SettingsDialog from "./components/SettingsDialog.svelte";
  import SessionPicker from "./components/SessionPicker.svelte";

  let shellStyle = $derived(
    `--left-panel-width:${ui.leftOpen ? ui.leftWidth : 0}px;--right-panel-width:${ui.rightOpen ? ui.rightWidth : 26}px`, /* 26 must match --right-rail-width in app.css */
  );

  // astra-1 G1: settings move from the left-panel page into a proper dialog
  // (the dialog component is the permanent surface; the left panel keeps
  // working until SessionPicker lands and the panel is removed).
  let settingsOpen = $state(false);
  let pickerOpen = $state(false);

  function beginPanelResize(side: "left" | "right", e: PointerEvent) {
    e.preventDefault();
    e.stopPropagation();

    const startX = e.clientX;
    const startWidth = side === "left" ? ui.leftWidth : ui.rightWidth;

    document.body.classList.add("panel-resizing");

    const move = (ev: PointerEvent) => {
      const delta = ev.clientX - startX;
      if (side === "left") ui.setLeftWidth(startWidth + delta, false);
      else ui.setRightWidth(startWidth - delta, false);
    };

    const finish = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", finish);
      window.removeEventListener("pointercancel", finish);
      document.body.classList.remove("panel-resizing");
      ui.persistLayout();
    };

    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", finish);
    window.addEventListener("pointercancel", finish);
  }
</script>

<div class="app-shell" style={shellStyle}>
  <div class="left-slot">
    <LeftPanel onOpenSettings={() => (settingsOpen = true)} />
  </div>

  <main class="chat-shell">
    {#if store.errorBanner}
      <div class="error-banner">
        <span>{store.errorBanner}</span>
        <button aria-label="Dismiss error" onclick={() => store.setError(null)}>×</button>
      </div>
    {/if}

    <HarnessHeader onSessions={() => (pickerOpen = true)} />
    <SessionTabs />
    <ConversationViewport />
    <Composer />
  </main>

  <RightPanel />

  {#if ui.leftOpen}
    <div
      class="panel-resizer panel-resizer-left"
      style:left={`${ui.leftWidth - 3}px`}
      title="Resize left panel"
      onpointerdown={(e) => beginPanelResize("left", e)}
    ></div>
  {/if}

  {#if ui.rightOpen}
    <div
      class="panel-resizer panel-resizer-right"
      style:right={`${ui.rightWidth - 3}px`}
      title="Resize right panel"
      onpointerdown={(e) => beginPanelResize("right", e)}
    ></div>
  {/if}

  <SettingsDialog open={settingsOpen} onClose={() => (settingsOpen = false)} />
  <SessionPicker open={pickerOpen} onClose={() => (pickerOpen = false)} />
</div>
