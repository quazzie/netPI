<script lang="ts">
  import { store } from "../store.svelte";
  import { ui } from "../ui.svelte";
  import { ws } from "../ws";

   let workspace = $state("");
  let font = $derived(ui.font);

  $effect(() => {
    if (ui.leftOpen) ws.request("session.list", {}).catch(() => {});
  });

  function newSession() {
    // astra-1 F: explicit navigation — the created session becomes visible.
    ws.createSession({
      workspace: store.session?.workspace || undefined,
    }).catch((e) => store.setError(String(e)));
  }

  function openSession(id: string) {
    // astra-1 F: explicit navigation — only this opens the visible session.
    ws.openSession(id).catch((e) => store.setError(String(e)));
  }

  function deleteSession(e: Event, id: string, title: string) {
    e.stopPropagation();
    if (!confirm(`Delete session "${title}"? Its transcript is removed permanently.`)) return;
    ws.request("session.delete", { sessionId: id }).catch((err) => store.setError(String(err)));
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
      <button class:active={ui.leftPage === "sessions"} onclick={() => (ui.leftPage = "sessions")}>Sessions</button>
      <button class:active={ui.leftPage === "settings"} onclick={() => (ui.leftPage = "settings")}>Settings</button>
    </div>

    {#if ui.leftPage === "sessions"}
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
              <span
                class="session-delete"
                title="Delete session"
                onclick={(e) => deleteSession(e, s.id, s.title || "untitled")}
              >✕</span>
            </button>
          {/each}
          {#if store.sessionRemaining > 0}
            <button
              class="left-more"
              disabled={store.sessionMoreLoading}
              onclick={() => ws.loadMoreSessions()}
            >
              {store.sessionMoreLoading ? "Loading…" : `Load ${Math.min(50, store.sessionRemaining)} older`}
            </button>
          {/if}
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
            onchange={(e) => ui.setFont((e.currentTarget as HTMLSelectElement).value as any)}
          >
            <option value="system">System</option>
            <option value="inter">Inter / UI sans</option>
            <option value="mono">Monospace</option>
            <option value="serif">Serif</option>
          </select>
        </div>

        <label class="setting-check">
          <input
            type="checkbox"
            checked={ui.keepThinkingOpen}
            onchange={(e) => ui.setKeepThinkingOpen((e.currentTarget as HTMLInputElement).checked)}
          />
          <span>
            <strong>Keep thinking open</strong>
            <small>Completed reasoning stays expanded instead of collapsing to a pill.</small>
          </span>
        </label>

        <label class="setting-check">
          <input
            type="checkbox"
            checked={ui.keepToolsOpen}
            onchange={(e) => ui.setKeepToolsOpen((e.currentTarget as HTMLInputElement).checked)}
          />
          <span>
            <strong>Keep tool calls open</strong>
            <small>Tool calls stay expanded by default instead of collapsing to a header row.</small>
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
