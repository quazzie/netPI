<script lang="ts">
  import { store } from "../store.svelte";

  let menuOpen = $state(false);
  let connClass = $derived(store.connection === "open" ? "conn open" : store.connection === "closed" ? "conn closed" : "conn");
  let sessionLabel = $derived(
    store.session?.title || (store.session?.workspace ? store.session.workspace : "no session"),
  );
</script>

<div class="header">
  <button class="menu-btn" title="sessions" onclick={() => { store.drawerOpen = !store.drawerOpen; menuOpen = false; }}>☰</button>
  <span class="title">netPI</span>
  <span class="session" title={store.session?.workspace ?? ""}>{sessionLabel}</span>
  <span class="spacer"></span>
  {#if store.agentState !== "Idle"}
    <span class="conn">{store.agentState}</span>
  {/if}
  <span class={connClass}>{store.connection}</span>
  <button class="dots" title="menu" onclick={() => (menuOpen = !menuOpen)}>⋯</button>
  {#if menuOpen}
    <div class="menu-pop" style="position: fixed; top: 44px; right: 8px">
      <button class="item" onclick={() => { store.overlay = "plugins"; menuOpen = false; }}>Plugins</button>
      <button class="item" onclick={() => { store.overlay = "settings"; menuOpen = false; }}>Settings</button>
      <button class="item" onclick={() => { store.overlay = "diagnostics"; menuOpen = false; }}>Diagnostics</button>
      <button class="item" onclick={() => { store.overlay = null; store.drawerOpen = true; menuOpen = false; }}>Session details</button>
    </div>
  {/if}
</div>
