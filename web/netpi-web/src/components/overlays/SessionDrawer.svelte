<script lang="ts">
  import { store } from "../../store.svelte";
  import { ws } from "../../ws";

  let wsInput = $state("");

  $effect(() => {
    if (store.drawerOpen) {
      ws.request("session.list", {});
    }
  });

  function newSession() {
    ws.request("session.create", { workspace: wsInput.trim() || undefined });
  }
  function open(s: { id: string }) {
    ws.request("session.open", { sessionId: s.id });
    store.drawerOpen = false;
  }
</script>

{#if store.drawerOpen}
  <div class="scrim" onclick={() => (store.drawerOpen = false)}></div>
  <div class="drawer">
    <div class="d-head">
      <span>Sessions</span>
      <button onclick={() => (store.drawerOpen = false)} style="font-size:18px;color:var(--text-dim)">×</button>
    </div>
    <div class="d-body">
      <button class="new-session" onclick={newSession}>+ New session</button>
      <label style="display:block;font-size:11px;color:var(--text-faint);margin:0 0 8px 2px">
        workspace (empty = default)
      </label>
      <input
        class="search"
        style="width:100%;background:var(--bg-soft);border:1px solid var(--border-soft);border-radius:7px;padding:6px 9px;outline:none;margin-bottom:10px;font-size:12.5px"
        placeholder="C:\src\project"
        bind:value={wsInput}
      />
      {#each store.sessions as s (s.id)}
        <button class="sess {s.id === store.session?.id ? "active" : ""}" onclick={() => open(s)}>
          <div class="t">{s.title || "untitled"}</div>
          <div class="w">{s.workspace}</div>
        </button>
      {/each}
      {#if !store.sessions.length}
        <div class="cap" style="padding:6px;color:var(--text-faint);font-size:12px">no sessions yet</div>
      {/if}
    </div>
    <div class="d-foot">
      <button onclick={() => { store.overlay = "plugins"; store.drawerOpen = false; }}>Plugins</button>
      <button onclick={() => { store.overlay = "settings"; store.drawerOpen = false; }}>Settings</button>
      <button onclick={() => { store.overlay = "diagnostics"; store.drawerOpen = false; }}>Diagnostics</button>
    </div>
  </div>
{/if}
