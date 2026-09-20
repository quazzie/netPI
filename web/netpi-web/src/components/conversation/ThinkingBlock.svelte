<script lang="ts">
  import type { ThinkingBlock as ThinkingPart } from "../../types";
  import { ui } from "../../ui.svelte";

  let { thinking }: { thinking: ThinkingPart } = $props();
  let userOpen = $state(false);

  function fmt(ms?: number): string {
    if (!ms) return "";
    return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(1)}s`;
  }

  /**
   * Streaming reasoning is deliberately rendered as ONE replacing line,
   * not an ever-growing block. Prefer the most recent non-empty line. Models
   * that stream prose without newlines still get a compact tail so the row
   * remains useful instead of becoming a horizontally scrolling paragraph.
   */
  function latestThought(text: string): string {
    const normalized = text.replace(/\r/g, "");
    const lines = normalized.split("\n");

    for (let i = lines.length - 1; i >= 0; i--) {
      const line = lines[i].trim();
      if (line) return compactTail(line);
    }

    return "Thinking…";
  }

  function compactTail(text: string): string {
    const clean = text.replace(/\s+/g, " ").trim();
    if (clean.length <= 180) return clean;

    // Keep the useful/current end of a long continuously-streamed thought.
    // Prefer a sentence boundary near the tail, otherwise hard-tail it.
    const tail = clean.slice(-180);
    const boundary = tail.search(/[.!?]\s+/);
    return boundary >= 0 && boundary < 80
      ? tail.slice(boundary + 2).trim()
      : "…" + tail.slice(1);
  }

  let expanded = $derived(thinking.done && (ui.keepThinkingOpen || userOpen));
  let finishedLabel = $derived(
    thinking.durationMs ? `Thought for ${fmt(thinking.durationMs)}` : "Thinking",
  );
</script>

{#if !thinking.done}
  <div class="thinking-stream" title={thinking.text || "Thinking…"} aria-live="polite">
    <span class="thinking-spark" aria-hidden="true">✦</span>
    <span class="thinking-stream-line">{latestThought(thinking.text)}</span>
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
