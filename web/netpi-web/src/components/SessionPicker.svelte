<script lang="ts">
  // astra-1 G1: SessionPicker — searchable global session list with
  // pagination (page size 50 from the server, "load more" continues from the
  // current offset), rename and delete. Rows show title, project (workspace),
  // updated time and running state. Search covers the LOADED pages; true
  // server-side search lands with the F2 session.* slice — until then the
  // placeholder says what it searches (honest, not a fake no-result).
  import { store } from "../store.svelte";
  import { ws } from "../ws";
  import type { SessionInfo } from "../types";

  let { open, onClose }: { open: boolean; onClose: () => void } = $props();
  let dialogRef: HTMLDivElement | null = $state(null);
  let listRef: HTMLDivElement | null = $state(null);
  let searchRef: HTMLInputElement | null = $state(null);
  let opener: HTMLElement | null = null;
  let query = $state("");
  let renamingId: string | null = $state(null);
  let renameText = $state("");

  $effect(() => {
    if (!open) return;
    opener = document.activeElement as HTMLElement | null;
    // open against the latest known list; ask for a refresh if the store
    // never loaded one (e.g. a fresh client that went straight to a picker).
    if (!store.sessions.length) ws.request("session.list", {}).catch(() => {});
    const t = setTimeout(() => searchRef?.focus(), 0);
    return () => {
      clearTimeout(t);
      opener?.focus?.();
      query = "";
      renamingId = null;
    };
  });

  const q = $derived(query.trim().toLowerCase());
  let rows = $derived.by(() => {
    const all = store.sessions;
    if (!q) return all;
    return all.filter(
      (s) =>
        s.title.toLowerCase().includes(q) ||
        (s.workspace ?? "").toLowerCase().includes(q),
    );
  });

  function fmtTime(ts: number): string {
    if (!ts) return "";
    const d = new Date(ts);
    const today = new Date();
    if (d.toDateString() === today.toDateString())
      return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    return d.toLocaleDateString([], { month: "short", day: "numeric" });
  }

  function runningState(id: string): "running" | "idle" {
    return store.busySessions[id] !== undefined ? "running" : "idle";
  }

  function openSession(id: string) {
    ws.openSession(id).catch((e) => store.setError(String(e)));
    onClose();
  }

  function newSession() {
    ws.createSession({
      workspace: store.session?.workspace || undefined,
    })
      .catch((e) => store.setError(String(e)))
      .finally(onClose);
  }

  function startRename(s: SessionInfo) {
    renamingId = s.id;
    renameText = s.title;
  }

  function commitRename(s: SessionInfo) {
    const t = renameText.trim();
    if (t && t !== s.title)
      ws.request("session.rename", {
        sessionId: s.id,
        title: t,
      }).catch((e) => store.setError(String(e)));
    renamingId = null;
  }

  function deleteSession(id: string, title: string) {
    if (!confirm(`Delete session "${title}"? Its transcript is removed permanently.`)) return;
    ws.request("session.delete", { sessionId: id }).catch((e) => store.setError(String(e)));
  }

  // Keyboard access: each row is a focusable div (Tab to move, Enter opens,
  // ✎/✕ are real buttons). We deliberately do NOT put a keydown on the list
  // container — that would flag an a11y "non-interactive keydown" warning and
  // Tab-through rows already gives full keyboard reach.
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
      class="session-picker"
      role="dialog"
      aria-modal="true"
      aria-label="Sessions"
      tabindex="-1"
      onkeydown={(e) => {
        if (e.key === "Escape") {
          e.stopPropagation();
          onClose();
        }
      }}
    >
      <div class="picker-head">
        <span class="picker-title">Sessions</span>
        <button
          class="btn picker-new"
          onclick={newSession}
          title="Start a new session"
        >＋ New</button>
        <button class="picker-close" aria-label="Close" onclick={onClose}>×</button>
      </div>

      <input
        bind:this={searchRef}
        class="picker-search"
        bind:value={query}
        placeholder="Search loaded sessions…"
        aria-label="Search sessions"
      />

      <div bind:this={listRef} class="picker-list" role="list">
        {#each rows as s (s.id)}
          {@const busy = runningState(s.id)}
          <div
            class="session-pick-row"
            class:active={s.id === store.session?.id}
            class:busy={busy === "running"}
            role="button"
            tabindex="0"
            onclick={() => openSession(s.id)}
            onkeydown={(e) => {
              if (e.key === "Enter" || e.key === " ") {
                e.preventDefault();
                openSession(s.id);
              }
            }}
          >
            <span
              class="picker-dot"
              class:running={busy === "running"}
              aria-hidden="true"></span>
            <span class="picker-main">
              {#if renamingId === s.id}
                <input
                  class="picker-rename"
                  bind:value={renameText}
                  onkeydown={(e) => {
                    e.stopPropagation();
                    if (e.key === "Enter") commitRename(s);
                    if (e.key === "Escape") renamingId = null;
                  }}
                  onblur={() => commitRename(s)}
                  aria-label="Rename session"
                />
              {:else}
                <span class="picker-title-row">
                  <span class="picker-name" title="Click ✎ to rename">
                    {s.title || "untitled"}
                  </span>
                  <button
                    class="picker-edit"
                    title="Rename"
                    aria-label={`Rename ${s.title || "session"}`}
                    onclick={(e) => {
                      e.stopPropagation();
                      startRename(s);
                    }}
                  >✎</button>
                </span>
              {/if}
              <span class="picker-meta">{s.workspace || "default workspace"}</span>
            </span>
            <span class="picker-time">{fmtTime(s.updatedAt)}</span>
            <button
              class="picker-delete"
              aria-label={`Delete ${s.title || "session"}`}
              onclick={(e) => {
                e.stopPropagation();
                deleteSession(s.id, s.title || "untitled");
              }}
            >✕</button>
          </div>
        {/each}
        {#if !rows.length}
          <div class="picker-empty">
            {store.sessions.length ? "No loaded sessions match." : "No sessions yet."}
          </div>
        {/if}
      </div>

      <div class="picker-foot">
        <span class="picker-count">{rows.length} shown</span>
        {#if store.sessionRemaining > 0}
          <button
            class="btn"
            disabled={store.sessionMoreLoading}
            onclick={() => ws.loadMoreSessions()}
          >
            {store.sessionMoreLoading ? "Loading…" : "Load older"}
          </button>
        {/if}
        {#if q}
          <span class="picker-hint">search covers loaded pages only</span>
        {/if}
      </div>
    </div>
  </div>
{/if}
