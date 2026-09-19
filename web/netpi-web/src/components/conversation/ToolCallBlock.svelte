<script lang="ts">
  import type { ToolCall } from "../../types";
  let { call }: { call: ToolCall } = $props();

  // Large output stays collapsed until expanded (PLAN §40).
  let open = $state(false);
  let autoOpen = $state(false);
  $effect(() => {
    if (call.result !== undefined && call.result.length > 1200 && !autoOpen) {
      open = false;
    }
  });

  function argsPreview(): string {
    try {
      const o = JSON.parse(call.argsJson || "{}");
      const parts = Object.entries(o)
        .map(([k, v]) => `${k}=${typeof v === "string" ? v : JSON.stringify(v)}`)
        .join(" ");
      return parts || "(no args)";
    } catch {
      return call.argsJson;
    }
  }

  function fmt(ms?: number): string {
    if (!ms) return "";
    return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)}s`;
  }
</script>

<div class="tool">
  <header onclick={() => (open = !open)}>
    <span class="name">{call.name}</span>
    <span class="args">{argsPreview()}</span>
    {#if call.isError}
      <span class="err">✗</span>
    {:else if call.result !== undefined}
      <span class="ok">✓</span>
    {/if}
    {#if call.durationMs}
      <span class="ms">{fmt(call.durationMs)}</span>
    {/if}
  </header>
  {#if open}
    {#if call.argsJson}
      <div class="args-block">{call.argsJson}</div>
    {/if}
    {#if call.result !== undefined}
      <div class="body {call.isError ? "error" : ""}">{call.result}</div>
    {:else}
      <div class="body">running…</div>
    {/if}
  {/if}
</div>
