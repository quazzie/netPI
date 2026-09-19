<script lang="ts">
import { marked } from "marked";
import DOMPurify from "dompurify";
import type { AssistantBlock } from "../../types";
import ThinkingBlock from "./ThinkingBlock.svelte";
import ToolCallBlock from "./ToolCallBlock.svelte";

let { block }: { block: AssistantBlock } = $props();

let html = $state("");

// Throttled markdown render (PLAN §40): append text cheaply, re-parse at
// ~150ms intervals while streaming and exactly once when the block
// completes. Completed blocks are never re-rendered.
let timer: ReturnType<typeof setTimeout> | null = null;
$effect(() => {
  const text = block.text;
  if (!text) return;
  if (block.done) {
    const raw = marked.parse(text, { async: false }) as string;
    html = DOMPurify.sanitize(raw);
  } else if (!timer) {
    timer = setTimeout(() => {
      timer = null;
      const raw = marked.parse(text, { async: false }) as string;
      html = DOMPurify.sanitize(raw);
    }, 150);
  }
});
</script>

<div class="msg-assistant">
  <div class="who">Assistant</div>

  {#if block.thinking && block.thinking.text}
    <ThinkingBlock thinking={block.thinking} />
  {/if}

  {#if block.toolCalls.length}
    {#each block.toolCalls as call (call.id)}
      <ToolCallBlock call={call} />
    {/each}
  {/if}

  {#if block.text}
    <div class="md">
{@html html || (block.text.replace(/\n/g, "<br>"))}
    </div>
  {/if}

  {#if !block.done && !block.text && (!block.thinking || !block.thinking.text) && block.toolCalls.length === 0}
    <div class="sys-block">thinking…</div>
  {/if}
</div>
