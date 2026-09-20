<script lang="ts">
  import { store } from "../store.svelte";
  import { ui } from "../ui.svelte";
  import { ws } from "../ws";

  let view = $state<"sessions" | "settings">("sessions");
  let workspace = $state("");
  let font = $derived(ui.font);

  $effect(() => {
    if (ui.leftOpen) ws.request("session.list", {}).catch(() => {});
  });

  function newSession() {
    ws.request("session.create", {
      workspace: store.session?.workspace || undefined,
    }).catch((e) => store.setError(String(e)));
  }

  function openSession(id: string) {
    ws.request("session.open", { sessionId: id }).catch((e) => store.setError(String(e)));
  }

  function saveWorkspace() {
    if (!store.session || !workspace.trim()) return;
    ws.request("session.rename", {
      sessionId: store.session.id,
      workspace: workspace.trim(),
    }).catch((e) => store.setError(String(e)));
  }

  $effect(() => {
    workspace = store.session?.workspace ?? "";
  });
</script>

{#if ui.leftOpen}
  <aside class="left-panel">
    <div class="left-panel-head">
      <div class="brand-mark">π</div>
      <div class="brand-title">netPI</div>
      <button class="panel-collapse" title="Collapse left panel" onclick={() => ui.toggleLeft()}>‹</button>
    </div>

    <div class="left-switcher">
      <button class:active={view === "sessions"} onclick={() => (view = "sessions")}>Sessions</button>
      <button class:active={view === "settings"} onclick={() => (view = "settings")}>Settings</button>
    </div>

    {#if view === "sessions"}
      <div class="left-body">
        <button class="left-new" onclick={newSession}>＋ New session</button>

        <div class="left-section-label">Workspace</div>
        <div class="workspace-card">
          <div class="workspace-name" title={store.session?.workspace ?? ""}>
            {store.session?.workspace || "No workspace selected"}
          </div>
        </div>

        <div class="left-section-label sessions-label">Recent</div>
        <div class="session-list">
          {#each store.sessions as s (s.id)}
            <button class="session-row" class:active={s.id === store.session?.id} onclick={() => openSession(s.id)}>
              <div class="session-row-title">{s.title || "untitled"}</div>
              <div class="session-row-meta">{s.workspace || "default workspace"}</div>
            </button>
          {/each}
          {#if !store.sessions.length}
            <div class="left-empty">No sessions yet.</div>
          {/if}
        </div>
      </div>
    {:else}
      <div class="left-body settings-body">
        <div class="setting-group">
          <label for="font-choice">Interface font</label>
          <select
            id="font-choice"
            value={font}
            onchange={(e) => (ui.font = (e.currentTarget as HTMLSelectElement).value as any)}
          >
            <option value="system">System</option>
            <option value="inter">Inter / UI sans</option>
            <option value="mono">Monospace</option>
            <option value="serif">Serif</option>
          </select>
        </div>

        <label class="setting-check">
          <input type="checkbox" bind:checked={ui.keepThinkingOpen} />
          <span>
            <strong>Keep thinking open</strong>
            <small>Completed reasoning stays expanded instead of collapsing to a pill.</small>
          </span>
        </label>

        <div class="setting-group">
          <label for="workspace-setting">Session workspace</label>
          <input id="workspace-setting" bind:value={workspace} placeholder="C:\src\project" />
          <button class="btn" onclick={saveWorkspace} disabled={!store.session || !workspace.trim()}>Update workspace</button>
        </div>

        <div class="setting-note">
          Model and reasoning are saved with the session. UI preferences are stored locally.
        </div>
      </div>
    {/if}
  </aside>
{/if}
