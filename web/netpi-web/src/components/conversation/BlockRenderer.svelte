<script lang="ts">
  import type { Block } from "../../types";
  import UserMessage from "./UserMessage.svelte";
  import AssistantMessage from "./AssistantMessage.svelte";
  import ToolCallBlock from "./ToolCallBlock.svelte";

  let { block }: { block: Block } = $props();
</script>

{#if block.kind === "user"}
  <UserMessage block={block} />
{:else if block.kind === "assistant"}
  <AssistantMessage block={block} />
{:else if block.kind === "tool"}
  <!-- astra-1 G3: standalone/replayed tool entries render with the SAME
    status/detail/output behavior as live tool calls — no degraded raw-text
    fallback. (An assistant's attached tool calls stay on the assistant
    block; only standalone entries land here.) -->
  <ToolCallBlock
    call={{
      id: block.id,
      name: block.name,
      argsJson: block.argsPreview,
      result: block.output,
      isError: block.isError,
      durationMs: block.durationMs,
      interrupted: block.done === false,
      status: block.done ? (block.isError ? "failed" : "done") : "interrupted",
    }}
  />
{:else}
  <div class="sys-block">{block.text}</div>
{/if}
