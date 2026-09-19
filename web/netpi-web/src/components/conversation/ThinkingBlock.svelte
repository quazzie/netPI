<script lang="ts">
  import type { ThinkingBlock as ThinkingPart } from "../../types";
  let { thinking }: { thinking: ThinkingPart } = $props();

  // Collapsed by default once done, expanded while streaming (PLAN §37).
  let open = $state(true);

  $effect(() => {
    if (thinking.done) open = false;
    else open = true;
  });

  function fmt(ms?: number): string {
    if (!ms) return "";
    return `${(ms / 1000).toFixed(1)}s`;
  }
</script>

<div class="thinking">
  <header onclick={() => (open = !open)}>
    <span>{open ? "▼" : "▶"}</span>
    <span>
      {thinking.done ? `Thought for ${fmt(thinking.durationMs) || "…"}` : "Thinking…"}
    </span>
  </header>
  {#if open}
    <div class="body">{thinking.text}</div>
  {/if}
</div>
