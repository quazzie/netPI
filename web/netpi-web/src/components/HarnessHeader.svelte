<script lang="ts">
  import { store } from "../store.svelte";
  import { ui } from "../ui.svelte";

  // astra-1 G1: the Sessions button opens the global picker.
  let { onSessions, onProjects }: { onSessions: () => void; onProjects: () => void } = $props();

  let connClass = $derived(
    store.connection === "open" ? "conn open"
      : store.connection === "closed" ? "conn closed"
      : "conn",
  );
  let sessionLabel = $derived(
    store.session?.title || (store.session?.workspace ? store.session.workspace : "untitled"),
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
