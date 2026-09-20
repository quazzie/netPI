<script lang="ts">
  import type { ThinkingBlock as ThinkingPart } from "../../types";
  import { ui } from "../../ui.svelte";

  let { thinking }: { thinking: ThinkingPart } = $props();
  let userOpen = $state(false);

  function fmt(ms?: number): string {
    if (!ms) return "";
    return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(1)}s`;
  }

  function latestLine(text: string): string {
    const visible = text.trimEnd();
    if (!visible) return "Thinking…";
    const newline = visible.lastIndexOf("\n");
    const line = (newline < 0 ? visible : visible.slice(newline + 1))
      .replaceAll("**", "")
      .replace(/\s+/g, " ")
      .trim();
    if (line.length <= 220) return line;
    return "…" + line.slice(-219);
  }

  let expanded = $derived(thinking.done && (ui.keepThinkingOpen || userOpen));
  let finishedLabel = $derived(
    thinking.durationMs ? `Thinking · ${fmt(thinking.durationMs)}` : "Thinking",
  );
</script>

{#if !thinking.done}
  <div class="thinking-stream" title={thinking.text || "Thinking…"} aria-live="polite">
    <span class="thinking-row-icon" aria-hidden="true">◉</span>
    <span class="thinking-row-title">Think</span>
    <span class="thinking-row-sep" aria-hidden="true"></span>
    <span class="thinking-stream-line">{latestLine(thinking.text)}</span>
  </div>
{:else}
  <div class="thinking-done">
    <button class="thinking-pill" onclick={() => (userOpen = !userOpen)}>
      <span class="thinking-pill-chevron">{expanded ? "⌄" : "›"}</span>
      <span>{finishedLabel}</span>
    </button>

    {#if expanded}
      <div class="thinking-body">{thinking.text}</div>
    {/if}
  </div>
{/if}
