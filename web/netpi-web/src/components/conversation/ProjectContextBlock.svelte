<script lang="ts">
  // astra-1 D: a project-change event is a DISTINCT transcript block — its
  // provenance is the APPLICATION (a project switch/refresh snapshot), not the
  // model, so it never renders as a user message or assistant prose. The
  // header carries the project name + workspace; the effective instructions
  // snapshot renders collapsed, expandable, in a monospace detail pane (the
  // same disclosure pattern as tool calls / system notices).
  import type { ProjectContextBlock } from "../../types";

  let { block }: { block: ProjectContextBlock } = $props();
  let open = $state(false);
</script>

<div class="notice project">
  <span class="notice-glyph" aria-hidden="true">▣</span>
  <button
    class="notice-head"
    aria-expanded={open}
    onclick={() => (open = !open)}
  >
    <span class="notice-title">
      Project context — {block.projectName || "unnamed project"}
      {block.workspace ? ` (${block.workspace})` : ""}
    </span>
    <span class="notice-chevron">{open ? "⌄" : "›"}</span>
  </button>
  {#if open}
    {#if block.text}
      <pre class="notice-detail project-text"
        ><code>{block.text}</code></pre
      >
    {:else}
      <div class="notice-detail">(no instructions snapshot)</div>
    {/if}
  {/if}
</div>
