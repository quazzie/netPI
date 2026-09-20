<script lang="ts">
  import { store } from "../../store.svelte";
  import type { ToolCall } from "../../types";

  let { call }: { call: ToolCall } = $props();

  let open = $state(false);
  let userToggled = $state(false);

  function parsedArgs(): Record<string, unknown> {
    try {
      const value = JSON.parse(call.argsJson || "{}");
      return value && typeof value === "object" ? value : {};
    } catch {
      return {};
    }
  }

  let args = $derived(parsedArgs());
  let running = $derived(call.result === undefined);
  let shellLike = $derived(call.name === "bash" || call.name === "powershell");
  let linkedFile = $derived(filePath());

  $effect(() => {
    if (!userToggled && running && shellLike) open = true;
  });

  function toggle() {
    userToggled = true;
    open = !open;
  }

  function filePath(): string | null {
    const value = args.path;
    return typeof value === "string" && value.trim() ? value.trim() : null;
  }

  function summary(): string {
    const path = filePath();
    if (path) {
      if (call.name === "grep") {
        const pattern = typeof args.pattern === "string" ? args.pattern : "";
        return pattern ? `${pattern}  ·  ${path}` : path;
      }
      return path;
    }

    const command = args.command;
    if (typeof command === "string" && command.trim()) {
      return command.replace(/\s+/g, " ").trim();
    }

    const pattern = args.pattern;
    if (typeof pattern === "string" && pattern.trim()) return pattern.trim();

    if (!call.argsJson) return "waiting for arguments…";
    return call.argsJson.replace(/\s+/g, " ").trim();
  }

  function fileHref(path: string): string {
    const params = new URLSearchParams({ path });
    if (store.session?.id) params.set("sessionId", store.session.id);
    return `/api/file?${params.toString()}`;
  }

  function fmt(ms?: number): string {
    if (!ms) return "";
    return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(ms < 10000 ? 1 : 0)}s`;
  }

  function prettyArgs(): string {
    try {
      return JSON.stringify(JSON.parse(call.argsJson || "{}"), null, 2);
    } catch {
      return call.argsJson;
    }
  }

  function stopLink(e: MouseEvent) {
    e.stopPropagation();
  }
</script>

<section class:running class:error={!!call.isError} class="tool-card">
  <div
    class="tool-head"
    role="button"
    tabindex="0"
    onclick={toggle}
    onkeydown={(e) => {
      if (e.key === "Enter" || e.key === " ") {
        e.preventDefault();
        toggle();
      }
    }}
  >
    <span class="tool-chevron">{open ? "⌄" : "›"}</span>
    <span class="tool-name">{call.name}</span>

    {#if linkedFile}
      <a
        class="tool-file"
        href={fileHref(linkedFile)}
        target="_blank"
        rel="noopener"
        title={linkedFile}
        onclick={stopLink}
      >{linkedFile}</a>
    {:else}
      <span class="tool-summary" title={summary()}>{summary()}</span>
    {/if}

    <span class="tool-spacer"></span>

    {#if running}
      <span class="tool-running-dot" aria-label="running"></span>
    {:else if call.isError}
      <span class="tool-state bad">Failed</span>
    {:else}
      <span class="tool-state good">Done</span>
    {/if}

    {#if call.durationMs}
      <span class="tool-duration">{fmt(call.durationMs)}</span>
    {/if}
  </div>

  {#if open}
    <div class="tool-detail">
      {#if call.argsJson}
        <div class="tool-section-label">Arguments</div>
        <pre class="tool-args">{prettyArgs()}</pre>
      {/if}

      <div class="tool-section-label">Output</div>
      {#if call.result !== undefined}
        <pre class:error={!!call.isError} class="tool-output">{call.result || "(no output)"}</pre>
      {:else}
        <div class="tool-live">
          <span class="tool-running-dot"></span>
          Running…
        </div>
      {/if}
    </div>
  {/if}
</section>
