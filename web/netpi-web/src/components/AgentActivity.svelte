<script lang="ts">
  import { store } from "../store.svelte";

  let startedAt = $state(Date.now());
  let now = $state(Date.now());
  let wasRunning = false;

  $effect(() => {
    const running = store.busy || store.requestPending;
    if (running && !wasRunning) {
      startedAt = Date.now();
      now = startedAt;
    }
    wasRunning = running;
  });

  $effect(() => {
    if (!store.busy && !store.requestPending) return;
    const timer = setInterval(() => (now = Date.now()), 1000);
    return () => clearInterval(timer);
  });

  let hasVisibleWork = $derived.by(() => {
    if (!store.activeAssistantId) return false;
    const block = store.blocks.find((b) => b.id === store.activeAssistantId);
    return block?.kind === "assistant"
      && (!!block.text || !!block.thinking?.text || block.toolCalls.length > 0);
  });

  let elapsed = $derived(Math.max(0, now - startedAt));

  function fmt(ms: number): string {
    const total = Math.floor(ms / 1000);
    if (total < 15) return "";
    const min = Math.floor(total / 60);
    const sec = total % 60;
    if (min === 0) return `${sec}s`;
    return `${min}m ${sec.toString().padStart(2, "0")}s`;
  }

  let label = $derived.by(() => {
    if (!hasVisibleWork) return store.activity ?? "Preparing…";
    return "Deep diving…";
  });
</script>

{#if store.busy || store.requestPending}
  <div class="agent-activity" aria-live="polite">
    <span class="activity-label">{label}</span>
    {#if fmt(elapsed)}<span class="activity-elapsed">{fmt(elapsed)}</span>{/if}
    {#if store.wireNote}
      <span class="activity-warning" title="{store.wireNote}">⚠ {store.wireNote}</span>
    {/if}
    {#if store.connection !== "open"}
      <span class="activity-warning">connection {store.connection}</span>
    {/if}
  </div>
{/if}
