<script lang="ts">
  import type { ThinkingBlock as ThinkingPart } from "../../types";
  import { ui } from "../../ui.svelte";

  let { thinking }: { thinking: ThinkingPart } = $props();
  let userOpen = $state(false);

  function fmt(ms?: number): string {
    if (!ms) return "";
    return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(1)}s`;
  }

  function lastLine(text: string): string {
    const parts = text.replace(/\r/g, "").split("\n");
    for (let i = parts.length - 1; i >= 0; i--) {
      const line = parts[i].trim();
      if (line) return line;
    }
    return "Thinking…";
  }

  let expanded = $derived(thinking.done && (ui.keepThinkingOpen || userOpen));
  let label = $derived(
    thinking.done
      ? `Thinking${thinking.durationMs ? " · " + fmt(thinking.durationMs) : ""}`
      : "Thinking",
  );
</script>

{#if !thinking.done}
  <div class="thinking-stream" title={thinking.text}>
    <span class="thinking-spinner" aria-hidden="true"></span>
    <span class="thinking-stream-label">Thinking</span>
    <span class="thinking-stream-line">{lastLine(thinking.text)}</span>
  </div>
{:else}
  <div class="thinking-done">
    <button class="thinking-pill" onclick={() => (userOpen = !userOpen)}>
      <span>{expanded ? "⌄" : "›"}</span>
      <span>{label}</span>
    </button>
    {#if expanded}
      <div class="thinking-body">{thinking.text}</div>
    {/if}
  </div>
{/if}
