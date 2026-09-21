<script lang="ts">
  import { store } from "../../store.svelte";
  import { ui } from "../../ui.svelte";
  import type { ToolCall } from "../../types";

  let { call }: { call: ToolCall } = $props();

  let userOpen = $state(false);

  function parsedArgs(): Record<string, unknown> {
    try {
      const value = JSON.parse(call.argsJson || "{}");
      return value && typeof value === "object" ? value : {};
    } catch {
      return {};
    }
  }

  let args = $derived(parsedArgs());
  let running = $derived(call.result === undefined && !call.interrupted);
  let interrupted = $derived(!!call.interrupted && call.result === undefined);
  let linkedFile = $derived(filePath());

  // Collapsed by default, all tools alike -- including running shell calls
  // (the header still shows "running"). The "keep tool calls open" setting
  // (same pattern as thinking blocks) keeps every call expanded by default;
  // a manual toggle always wins.
  let expanded = $derived(userOpen || ui.keepToolsOpen);

  function toggle() {
    userOpen = !expanded;
  }

  function openInShell(e: MouseEvent, path: string) {
    e.preventDefault();
    e.stopPropagation();
    const params = new URLSearchParams({ path });
    if (store.session?.id) params.set("sessionId", store.session.id);
    // fetch does not reject on HTTP error statuses — check res.ok and
    // surface the server's reason, otherwise a 404 would fail silently.
    fetch(`/api/open?${params.toString()}`)
      .then(async (res) => {
        if (!res.ok)
          store.setError(`Could not open ${path}: ${(await res.text().catch(() => "")) || res.statusText}`);
      })
      .catch((err) => store.setError(String(err)));
  }

  function displayName(): string {
    switch (call.name.toLowerCase()) {
      case "bash": return "Bash";
      case "powershell": return "PowerShell";
      case "read": return "Read";
      case "write": return "Write";
      case "edit": return "Edit";
      case "grep": return "Grep";
      default: return call.name.replace(/(^|[_-])(\w)/g, (_, __, c) => c.toUpperCase());
    }
  }

  function glyph(): string {
    switch (call.name.toLowerCase()) {
      case "bash":
      case "powershell": return ">_";
      case "read": return "▤";
      case "write":
      case "edit": return "✎";
      case "grep": return "⌕";
      default: return "◇";
    }
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
        return pattern ? `${pattern} · ${path}` : path;
      }
      return path;
    }

    const command = args.command;
    if (typeof command === "string" && command.trim())
      return command.replace(/\s+/g, " ").trim();

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

</script>

<section class:running class:error={!!call.isError} class:interrupted class:open={expanded} class="tool-card">
  <div
    class="tool-head"
    role="button"
    tabindex="0"
    aria-expanded={expanded}
    onclick={toggle}
    onkeydown={(e) => {
      if (e.key === "Enter" || e.key === " ") {
        e.preventDefault();
        toggle();
      }
    }}
  >
    <span class="tool-leading" aria-hidden="true">
      <span class="tool-glyph">{glyph()}</span>
      <span class="tool-chevron">{expanded ? "⌄" : "›"}</span>
    </span>

    <span class="tool-name">{displayName()}</span>
    <span class="tool-sep" aria-hidden="true"></span>

    {#if linkedFile}
      <a
        class="tool-file"
        href={fileHref(linkedFile)}
        target="_blank"
        rel="noopener"
        title={`Open ${linkedFile} in its default application`}
        onclick={(e) => openInShell(e, linkedFile)}
      >{linkedFile}</a>
    {:else}
      <span class="tool-summary" title={summary()}>{summary()}</span>
    {/if}

    <span class="tool-spacer"></span>

    {#if running}
      <span class="tool-running-label">running</span>
    {:else if interrupted}
      <span class="tool-state interrupted">interrupted</span>
    {:else if call.isError}
      <span class="tool-state bad">failed</span>
    {:else if call.durationMs}
      <span class="tool-duration">{fmt(call.durationMs)}</span>
    {/if}
  </div>

  {#if expanded}
    <div class="tool-detail">
      {#if call.argsJson}
        <div class="tool-section-label">Input</div>
        <pre class="tool-args">{prettyArgs()}</pre>
      {/if}

      <div class="tool-section-label">Output</div>
      {#if call.result !== undefined}
        <pre class:error={!!call.isError} class="tool-output">{call.result || "(no output)"}</pre>
      {:else}
        <div class="tool-live">Running…</div>
      {/if}
    </div>
  {/if}
</section>
