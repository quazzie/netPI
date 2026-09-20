<script lang="ts">
  import { store } from "../store.svelte";
</script>

{#if store.activity}
  <div class="agent-activity" aria-live="polite">
    <span class="activity-spinner" aria-hidden="true"></span>
    <span>{store.activity}</span>
    {#if store.connection !== "open"}
      <span class="activity-warning">connection {store.connection}</span>
    {/if}
  </div>
{/if}
