<script lang="ts">
  import { store } from "./store.svelte";
  import { ui } from "./ui.svelte";
  import "./ws";
  import LeftPanel from "./components/LeftPanel.svelte";
  import RightPanel from "./components/RightPanel.svelte";
  import HarnessHeader from "./components/HarnessHeader.svelte";
  import ConversationViewport from "./components/ConversationViewport.svelte";
  import Composer from "./components/Composer.svelte";

  let shellStyle = $derived(
    `--left-panel-width:${ui.leftOpen ? ui.leftWidth : 0}px;--right-panel-width:${ui.rightOpen ? ui.rightWidth : 42}px`,
  );
</script>

<div class="app-shell" style={shellStyle}>
  <LeftPanel />

  <main class="chat-shell">
    {#if store.errorBanner}
      <div class="error-banner">
        <span>{store.errorBanner}</span>
        <button aria-label="Dismiss error" onclick={() => store.setError(null)}>×</button>
      </div>
    {/if}

    <HarnessHeader />
    <ConversationViewport />
    <Composer />
  </main>

  <RightPanel />
</div>
