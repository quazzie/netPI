<script lang="ts">
  import { store } from "../store.svelte";
  import { ui } from "../ui.svelte";

  // astra-1 G1: the Sessions button opens the global picker.
  let { onSessions, onProjects, onNew }: { onSessions: () => void; onProjects: () => void; onNew: () => void } = $props();

  let connClass = $derived(
    store.connection === "open" ? "conn open"
      : store.connection === "closed" ? "conn closed"
      : "conn",
  );
  // Draft mode: an explicit New Session with no server session yet — the
  // header shows the placeholder title (the first prompt creates + titles it).
  let isDraft = $derived(store.session === null && store.newDraft !== null);
  let sessionLabel = $derived(
    isDraft ? "New session"
      : store.session?.title || (store.session?.workspace ? store.session.workspace : "untitled"),
  );
  // Contextual tooltip for the one-click ＋: the draft captures the currently
  // APPLIED project (a pending switch is not inherited), else none.
  let newSessionTitle = $derived(
    store.session?.project?.name
      ? `New session in ${store.session.project.name}`
      : store.newDraft?.projectName
        ? `New session in ${store.newDraft.projectName}`
        : "New session (no project)",
  );
  // astra-1 D2: the session's active project (F1/D2 make it first-class)
  // shows next to the session label — switching projects updates it.
  let projectLabel = $derived(store.session?.project?.name ?? null);
  let projectPending = $derived(
    store.session ? store.projectPending[store.session.id] ?? null : null,
  );
</script>

<div class="header">
  <span class="title">netPI</span>

  <!-- astra-1 G1: Sessions opens the global picker; the current-session label
       stays after it (the left panel's session list remains until removal). -->
  <button class="header-sessions" title="All sessions" onclick={onSessions}>Sessions</button>
  <!-- One-click New: a local draft (no server session) with the applied
       project — the first prompt creates + titles the real session. -->
  <button class="header-new" title={newSessionTitle} aria-label={newSessionTitle} onclick={onNew}>＋</button>
  <span class="session" title={store.session?.workspace ?? ""}>{sessionLabel}</span>
  <!-- astra-1 C (G1 gap): the project chip opens ProjectPicker (switch/create).
       Always shown — "No project" is an actionable state, not an empty one. -->
  <button class="header-project" title="Switch project" onclick={onProjects}>
    ▣ {projectLabel ?? "No project"}
  </button>
  {#if projectPending}
    <span class="header-project-pending" title={projectPending.operationId}>
      ⇄ {projectPending.project}…
    </span>
  {/if}

  <span class="spacer"></span>

  {#if store.agentState !== "Idle"}
    <span class="header-agent-state">{store.agentState}</span>
  {/if}

  <span class={connClass}>{store.connection}</span>
</div>
