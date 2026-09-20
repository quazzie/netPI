<script lang="ts">
  import type { ThinkingBlock as ThinkingPart } from "../../types";
  let { thinking }: { thinking: ThinkingPart } = $props();

  let open = $state(true);
  let userToggled = $state(false);

  $effect(() => {
    if (userToggled) return;
    open = !thinking.done;
  });

  function toggle() {
    userToggled = true;
    open = !open;
  }

  function fmt(ms?: number): string {
    if (!ms) return "";
    return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(1)}s`;
  }

  let label = $derived(
    thinking.done
      ? (thinking.durationMs ? `Thought for ${fmt(thinking.durationMs)}` : "Thinking")
      : "Thinking…",
  );
</script>

<div class="thinking">
  <header onclick={toggle}>
    <span>{open ? "⌄" : "›"}</span>
    <span>{label}</span>
  </header>
  {#if open}
    <div class="body">{thinking.text}</div>
  {/if}
</div>
