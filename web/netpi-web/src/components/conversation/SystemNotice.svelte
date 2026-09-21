<script lang="ts">
  // astra-1 G3: system/project/compaction notices render as compact, distinct
  // entries — never disguised as user prose or assistant work. Short notes
  // render inline; long ones (compaction summaries, project changes) truncate
  // with an expandable detail.
  import type { SystemBlock } from "../../types";

  let { block }: { block: SystemBlock } = $props();
  let open = $state(false);

  const isCompaction = $derived(block.text.startsWith("Compaction:"));
  const isProject = $derived(
    block.text.startsWith("Project:") ||
      block.text.startsWith("Switched") ||
      block.text.startsWith("Switch to"),
  );
  const title = $derived(
    isCompaction
      ? "Context compacted"
      : isProject
        ? "Project"
        : block.text.slice(0, 64),
  );
  const detail = $derived(block.text);
  const long = $derived(block.text.length > 96);

  function icon(): string {
    if (isCompaction) return "⊘";
    if (isProject) return "▣";
    return "⚑";
  }
</script>

<div
  class:compaction={isCompaction}
  class:project={isProject}
  class="notice"
>
  <span class="notice-glyph" aria-hidden="true">{icon()}</span>
  {#if long}
    <button
      class="notice-head"
      aria-expanded={open}
      onclick={() => (open = !open)}
    >
      <span class="notice-title">{title}</span>
      <span class="notice-chevron">{open ? "⌄" : "›"}</span>
    </button>
    {#if open}
      <div class="notice-detail">{detail}</div>
    {/if}
  {:else}
    <span class="notice-text" title={detail}>{block.text}</span>
  {/if}
</div>
