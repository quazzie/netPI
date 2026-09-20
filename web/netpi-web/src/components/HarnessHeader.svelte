<script lang="ts">
  import { store } from "../store.svelte";
  import { ui } from "../ui.svelte";

  let connClass = $derived(
    store.connection === "open" ? "conn open"
      : store.connection === "closed" ? "conn closed"
      : "conn",
  );
  let sessionLabel = $derived(
    store.session?.title || (store.session?.workspace ? store.session.workspace : "untitled"),
  );
</script>

<div class="header">
  <button
    class="menu-btn"
    title={ui.leftOpen ? "Collapse navigation" : "Open navigation"}
    onclick={() => ui.toggleLeft()}
  >☰</button>

  <span class="title">netPI</span>
  <span class="session" title={store.session?.workspace ?? ""}>{sessionLabel}</span>

  <span class="spacer"></span>

  {#if store.agentState !== "Idle"}
    <span class="header-agent-state">{store.agentState}</span>
  {/if}

  <span class={connClass}>{store.connection}</span>

  <button
    class="right-toggle"
    class:active={ui.rightOpen}
    title={ui.rightOpen ? "Collapse right panel" : "Open right panel"}
    onclick={() => ui.toggleRight()}
  >◫</button>
</div>
