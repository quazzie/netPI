<script lang="ts">
  import { tick } from "svelte";
  import { store } from "../../store.svelte";
  import { ui } from "../../ui.svelte";
  import type { ToolCall } from "../../types";

  let { call }: { call: ToolCall } = $props();

  // astra-1 G3: disclosure is a 3-state override (unset → the global "keep
  // tool calls open" default; a manual toggle always wins) — the old
  // userOpen || keepToolsOpen could NOT collapse a call while the setting was
  // on. Bounded per-block in the store so the choice survives replays.
  let userOpen = $state<"unset" | "open" | "closed">(
    (store.toolDisclosure[call.id] as "open" | "closed" | undefined) ?? "unset",
  );

  function parsedArgs(): Record<string, unknown> {
    try {
      const value = JSON.parse(call.argsJson || "{}");
      return value && typeof value === "object" ? value : {};
    } catch {
      return {};
    }
  }

  let args = $derived(parsedArgs());
  let linkedFile = $derived(filePath());

  /** astra-1 G3: explicit lifecycle — tool.completed/failed/interruption are
   *  the only signals; streamed output does not complete a call. Legacy
   *  replayed entries without `status` keep the old result-based mapping. */
  let status = $derived(
    call.status ??
      (call.interrupted && call.result === undefined
        ? "interrupted"
        : call.result === undefined
          ? "running"
          : call.isError
            ? "failed"
            : "done"),
  );
  let running = $derived(status === "running");

  // astra-1 G3: the output scroller owns its own follow (G0 pattern) — a
  // streaming tool follows its tail UNLESS the user scrolls up to read;
  // completed output is never force-scrolled.
  let outEl: HTMLElement | null = $state(null);
  let outFollow = $state(true);
  let outPin = { top: 0, at: 0 };
  let wasExpanded = false;

  function pinOut() {
    if (!outEl || !outEl.isConnected || !outFollow) return;
    outEl.scrollTop = outEl.scrollHeight - outEl.clientHeight;
    outPin.top = outEl.scrollTop;
    outPin.at = performance.now();
  }

  function onOutScroll() {
    if (!outEl) return;
    if (performance.now() - outPin.at < 300 && Math.abs(outEl.scrollTop - outPin.top) < 1)
      return; // our own programmatic pin
    const gap = outEl.scrollHeight - outEl.scrollTop - outEl.clientHeight;
    outFollow = gap < 24;
  }

  // Only auto-follow live output; never drag a reader on finished output.
  $effect(() => {
    if (!(expanded && running && call.result !== undefined)) return;
    void tick().then(() => pinOut());
  });

  // Position on first-open: a still-streaming output starts at the tail
  // (follow on); finished output starts at the top (follow off, reading).
  $effect(() => {
    const open = expanded && running && call.result !== undefined;
    if (open && !wasExpanded) {
      outFollow = true;
      void tick().then(() => pinOut());
    }
    wasExpanded = open;
  });

  // Collapsed by default, all tools alike -- including running shell calls
  // (the header still shows "running"). The "keep tool calls open" setting
  // (same pattern as thinking blocks) keeps every call expanded by default;
  // a manual toggle always wins.
  let expanded = $derived(
    userOpen === "unset" ? ui.keepToolsOpen : userOpen === "open",
  );

  function toggle() {
    userOpen = expanded ? "closed" : "open";
    store.setToolDisclosure(call.id, userOpen);
  }

  function openInShell(e: MouseEvent, path: string) {
    // astra-1 G3: always stop the enclosing disclosure toggle; plain click
    // shell-opens, ctrl/meta/middle keeps the in-app /api/file viewer.
    e.stopPropagation();
    const modified = e.ctrlKey || e.metaKey || e.button !== 0;
    if (modified) return; // let the anchor's href (the viewer) take over
    e.preventDefault();
    const params = new URLSearchParams({ path });
    if (store.session?.id) params.set("sessionId", store.session.id);
    // fetch does not reject on HTTP error statuses — check res.ok and
    // surface the server's reason, otherwise a 404 would fail silently.
    fetch(`/api/open?${params.toString()}`, { method: "POST" })
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

  // astra-1 G3: very large outputs render a bounded window with an explicit
  // load-more — never silently lose results (the full text stays in the
  // session history; only the DOM is bounded).
  const MAX_RENDERED = 200_000; // chars (~200 KB of monospace text)
  let outAll = $state(false);
  let outLong = $derived(
    call.result !== undefined && call.result.length > MAX_RENDERED,
  );
  let outShown = $derived(
    !outLong || outAll
      ? (call.result ?? "")
      : (call.result ?? "").slice(0, MAX_RENDERED),
  );

  function k(n: number): string {
    return n >= 1_000_000
      ? (n / 1_000_000).toFixed(1) + " M"
      : n >= 1000
        ? Math.round(n / 1000) + "k"
        : String(n);
  }

  let prettyArgsCache: { key: string; text: string } | null = null;
  /** astra-1 G3: large inputs are formatted once per args revision, and only
   *  while the detail pane is actually expanded (no per-render JSON.parse). */
  function prettyArgs(): string {
    if (prettyArgsCache?.key === call.argsJson) return prettyArgsCache.text;
    let text: string;
    try {
      text = JSON.stringify(JSON.parse(call.argsJson || "{}"), null, 2);
    } catch {
      text = call.argsJson;
    }
    prettyArgsCache = { key: call.argsJson, text };
    return text;
  }

</script>

<section
  class:running={running}
  class:error={status === "failed"}
  class:interrupted={status === "interrupted"}
  class:open={expanded}
  class="tool-card">
  <div
    class="tool-head"
    role="button"
    tabindex="0"
    aria-expanded={expanded}
    onclick={toggle}
    onkeydown={(e) => {
      // astra-1 G3: keyboard events on the nested file link are the link's —
      // keydown bubbles from it here, so Enter/Space on the focused link must
      // NOT also toggle the enclosing disclosure.
      if (e.target !== e.currentTarget) return;
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
    {:else if status === "interrupted"}
      <span class="tool-state interrupted">interrupted</span>
    {:else if status === "failed"}
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
        <pre
          bind:this={outEl}
          onscroll={onOutScroll}
          class:error={status === "failed"}
          class="tool-output">{outShown || "(no output)"}</pre>
        {#if outLong}
          <div class="tool-output-more">
            <button class="tool-more-btn" onclick={() => (outAll = !outAll)}>
              {outAll ? "Show truncated window" : `Show all (${k(call.result.length)} chars)`}
            </button>
            {#if !outAll}
              <span>rendered first {k(MAX_RENDERED)} — full output stays in the session history</span>
            {/if}
          </div>
        {/if}
      {:else}
        <div class="tool-live">Running…</div>
      {/if}
    </div>
  {/if}
</section>
