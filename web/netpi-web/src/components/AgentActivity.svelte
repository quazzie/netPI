<script lang="ts">
  import { store } from "../store.svelte";

  let hasVisibleAssistantWork = $derived.by(() => {
    if (!store.activeAssistantId) return false;
    const block = store.blocks.find((b) => b.id === store.activeAssistantId);
    return block?.kind === "assistant"
      && (!!block.text || !!block.thinking?.text || block.toolCalls.length > 0);
  });

  let visible = $derived(!!store.activity && !hasVisibleAssistantWork);
</script>

{#if visible}
  <div class="agent-activity" aria-live="polite">
    <span class="activity-spinner" aria-hidden="true"></span>
    <span>{store.activity}</span>
    {#if store.connection !== "open"}
      <span class="activity-warning">connection {store.connection}</span>
    {/if}
  </div>
{/if}
