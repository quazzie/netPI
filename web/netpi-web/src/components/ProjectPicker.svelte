<script lang="ts">
  // astra-1 C (G1 gap): ProjectPicker — the client-side project surface.
  // Opens from the header project chip. Lists the server's projects
  // (project.list, server-backed — never localStorage), creates new ones
  // (project.create, upsert by path: the same path returns the same id),
  // and applies a project to the CURRENT session via session.project with a
  // fresh client operationId (Composer's idempotent-retry pattern). A switch
  // on a running session goes pending; the pending notice is the EXISTING
  // header plumbing (store.projectPending / session.project.pending+applied) —
  // this component never duplicates it. Errors surface as a picker-local
  // notice (store.projectNotice), never silently.
  import { store } from "../store.svelte";
  import { ws } from "../ws";
  import type { ProjectInfo } from "../types";

  let { open, onClose }: { open: boolean; onClose: () => void } = $props();
  let dialogRef: HTMLDivElement | null = $state(null);
  let pathRef: HTMLInputElement | null = $state(null);
  let opener: HTMLElement | null = null;
  let query = $state("");
  let newPath = $state("");
  let newName = $state("");
  let creating = $state(false);
  let refreshing = $state(false);
  let switchingId: string | null = $state(null);
  let switchError: string | null = $state(null);
  // astra-1 C: the path just created — the upserted row is highlighted until
  // the picker closes (the project.created event upserts it into the list).
  let freshPath: string | null = $state(null);

  $effect(() => {
    if (!open) return;
    opener = document.activeElement as HTMLElement | null;
    // Server-backed list: refresh every open (the store's copy may be stale).
    loadList();
    // Pre-fill the create path from the current session's workspace (the most
    // likely "project I mean right now" path) — the user can edit it.
    newPath = store.session?.workspace ?? "";
    const t = setTimeout(() => pathRef?.focus(), 0);
    return () => {
      clearTimeout(t);
      opener?.focus?.();
      query = "";
      newPath = "";
      newName = "";
      creating = false;
      switchingId = null;
      switchError = null;
      freshPath = null;
    };
  });

  function loadList() {
    // The project.list REPLY event (not the ack) mirrors the payload into the
    // store — the request's promise only resolves on the ack.
    store.projectsLoading = true;
    ws.request("project.list", {})
      .catch((e) => store.setError(e instanceof Error ? e.message : String(e)))
      .finally(() => (store.projectsLoading = false));
  }

  // The current session's active project id (read from the visible session's
  // snapshot — the header chip renders it read-only).
  const currentProjectId = $derived(store.session?.project?.id ?? null);
  const pending = $derived(
    store.session ? store.projectPending[store.session.id] ?? null : null,
  );

  const q = $derived(query.trim().toLowerCase());
  let rows = $derived.by(() => {
    const all = store.projects;
    if (!q) return all;
    return all.filter(
      (p) =>
        p.name.toLowerCase().includes(q) ||
        p.workspacePath.toLowerCase().includes(q),
    );
  });

  // Create a project (upsert by path: the same path returns the same id). The
  // project.created broadcast event upserts the row into store.projects, so
  // the freshly-created row appears in the list as soon as it lands.
  async function createProject() {
    const path = newPath.trim();
    if (!path || creating) return;
    creating = true;
    try {
      await ws.request("project.create", {
        workspacePath: path,
        name: newName.trim() || undefined,
      });
      freshPath = path;
      newPath = "";
      newName = "";
    } catch (e) {
      store.setProjectNotice(e instanceof Error ? e.message : String(e));
    } finally {
      creating = false;
    }
  }

  // Make a project the current session's project. Idle → applies at once
  // (session.updated refreshes the chip); running → the EXISTING pending
  // plumbing (store.projectPending + header chip) carries it until the run's
  // safe boundary (session.project.applied). A fresh operationId makes a
  // re-tap a distinct op, matching Composer's idempotent-retry pattern.
  async function applyToSession(p: ProjectInfo) {
    const sid = store.session?.id;
    if (!sid || switchingId) return;
    if (p.id === currentProjectId) return; // already active
    const operationId =
      globalThis.crypto?.randomUUID?.() ??
      `op-${Date.now()}-${Math.random().toString(16).slice(2)}`;
    switchingId = p.id;
    switchError = null;
    try {
      await ws.request("session.project", {
        sessionId: sid,
        projectId: p.id,
        operationId,
      });
      // The ack is the switch ACCEPTED (applied now or queued pending) — close.
      onClose();
    } catch (e) {
      switchError = e instanceof Error ? e.message : String(e);
      store.setProjectNotice(`project switch failed: ${switchError}`, operationId);
    } finally {
      switchingId = null;
    }
  }

  // Re-snapshot the active project's instructions (picks up instruction-file
  // edits since the last switch). session.project.applied clears the notice.
  async function refreshInstructions() {
    const sid = store.session?.id;
    if (!sid || !currentProjectId || refreshing) return;
    const operationId =
      globalThis.crypto?.randomUUID?.() ??
      `op-${Date.now()}-${Math.random().toString(16).slice(2)}`;
    refreshing = true;
    store.setProjectNotice("refreshing project instructions…", operationId);
    try {
      await ws.request("session.project.refresh", {
        sessionId: sid,
        operationId,
      });
    } catch (e) {
      store.setProjectNotice(e instanceof Error ? e.message : String(e), operationId);
    } finally {
      refreshing = false;
    }
  }
</script>

{#if open}
  <div
    class="picker-backdrop"
    role="presentation"
    onclick={(e) => {
      if (e.target === e.currentTarget) onClose();
    }}
  >
    <div
      bind:this={dialogRef}
      class="project-picker"
      role="dialog"
      aria-modal="true"
      aria-label="Projects"
      tabindex="-1"
      onkeydown={(e) => {
        if (e.key === "Escape") {
          e.stopPropagation();
          onClose();
        }
      }}
    >
      <div class="picker-head">
        <span class="picker-title">Project</span>
        {#if pending}
          <span class="picker-pending" title={pending.operationId}>
            ⇄ {pending.project} pending — applies when the run idles
          </span>
        {/if}
        <button class="picker-close" aria-label="Close" onclick={onClose}>×</button>
      </div>

      {#if store.projectNotice}
        <div class="picker-notice">
          <span>{store.projectNotice.message}</span>
          <button aria-label="Dismiss" onclick={() => store.clearProjectNotice()}>×</button>
        </div>
      {/if}
      {#if switchError}
        <div class="picker-notice picker-notice-err">{switchError}</div>
      {/if}

      <div class="picker-create">
        <input
          bind:this={pathRef}
          bind:value={newPath}
          class="picker-search"
          placeholder="Path to a project folder (e.g. C:\src\myapp)"
          aria-label="New project path"
          onkeydown={(e) => {
            if (e.key === "Enter") createProject();
          }}
        />
        <input
          bind:value={newName}
          class="picker-search"
          placeholder="Name (defaults to the folder name)"
          aria-label="New project name"
          onkeydown={(e) => {
            if (e.key === "Enter") createProject();
          }}
        />
        <button class="btn" disabled={creating || !newPath.trim()} onclick={createProject}>
          {creating ? "Creating…" : "＋ Create"}
        </button>
      </div>

      <input
        bind:value={query}
        class="picker-search"
        placeholder="Search projects…"
        aria-label="Search projects"
      />

      <div class="picker-list" role="list">
        {#each rows as p (p.id)}
          {@const isCurrent = p.id === currentProjectId}
          {@const isFresh = freshPath !== null && p.workspacePath === freshPath}
          <div
            class="session-pick-row"
            class:active={isCurrent}
            class:fresh={isFresh}
            class:switching={switchingId === p.id}
            role="button"
            tabindex="0"
            onclick={() => applyToSession(p)}
            onkeydown={(e) => {
              if (e.key === "Enter" || e.key === " ") {
                e.preventDefault();
                applyToSession(p);
              }
            }}
            title="Click to make this the session's project"
          >
            <span class="picker-dot" class:running={isCurrent} aria-hidden="true"></span>
            <span class="picker-main">
              <span class="picker-title-row">
                <span class="picker-name">{p.name}</span>
                {#if isCurrent}
                  <span class="picker-tag">active</span>
                {/if}
              </span>
              <span class="picker-meta">{p.workspacePath}</span>
            </span>
            <span class="picker-time">{switchingId === p.id ? "…" : ""}</span>
          </div>
        {/each}
        {#if !rows.length && !store.projectsLoading}
          <div class="picker-empty">
            {store.projects.length ? "No projects match." : "No projects yet — create one above."}
          </div>
        {/if}
        {#if store.projectsLoading}
          <div class="picker-empty">Loading projects…</div>
        {/if}
      </div>

      <div class="picker-foot">
        <span class="picker-count">{store.projects.length} project(s)</span>
        {#if store.session?.project}
          <button
            class="btn"
            disabled={refreshing}
            onclick={refreshInstructions}
            title="Re-snapshot this project's instructions (picks up instruction-file edits since the last switch)"
          >
            {refreshing ? "Refreshing…" : "↻ Refresh instructions"}
          </button>
        {/if}
      </div>
    </div>
  </div>
{/if}

<style>
  .project-picker {
    width: min(560px, calc(100vw - 48px));
    max-height: 78vh;
    display: flex;
    flex-direction: column;
    gap: 10px;
    padding: 14px 16px;
    border: 1px solid #333b46;
    border-radius: 12px;
    background: #161a20;
    box-shadow: 0 18px 48px rgba(0, 0, 0, 0.55);
    outline: none;
  }
  .picker-pending {
    color: #e3b341;
    font-size: 10.5px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .picker-notice {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 9px;
    border: 1px solid #3a3320;
    border-radius: 7px;
    background: #171410;
    color: #e3b341;
    font-size: 11.5px;
  }
  .picker-notice button {
    margin-left: auto;
    background: none;
    border: none;
    color: inherit;
    cursor: pointer;
    font-size: 12px;
  }
  .picker-notice-err {
    border-color: #3a2026;
    background: #170f11;
    color: #f29ba2;
  }
  .picker-create {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .picker-create .picker-search:nth-child(2) {
    font-size: 11px;
  }
  .picker-create .btn {
    align-self: flex-end;
    font-size: 11px;
    padding: 4px 10px;
  }
  .picker-tag {
    flex: none;
    padding: 1px 6px;
    border: 1px solid #2c4a34;
    border-radius: 5px;
    background: #122019;
    color: #7ec98a;
    font-size: 9.5px;
    font-weight: 650;
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .session-pick-row.switching .picker-name {
    color: #679efe;
  }
  .session-pick-row.switching {
    opacity: 0.6;
  }
  .session-pick-row.fresh .picker-name {
    color: #e3b341;
  }

</style>
