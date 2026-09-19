<script lang="ts">
  import type { Block } from "../../types";
  import UserMessage from "./UserMessage.svelte";
  import AssistantMessage from "./AssistantMessage.svelte";

  let { block }: { block: Block } = $props();
</script>

{#if block.kind === "user"}
  <UserMessage block={block} />
{:else if block.kind === "assistant"}
  <AssistantMessage block={block} />
{:else if block.kind === "tool"}
  <div class="sys-block">{block.name}: {block.output}</div>
{:else}
  <div class="sys-block">{block.text}</div>
{/if}
