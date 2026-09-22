<script lang="ts">
  // astra-1 G2 (client slice): compact context meter beside the composer's
  // model/reasoning controls. Describes the SELECTED session's model context:
  // provider-reported input tokens for the last request plus a labeled
  // estimate for content appended after it. No high-frequency timers — values
  // refresh on usage events, compaction, session switch (F1 makes all of
  // those session-scoped).
  import { store } from "../store.svelte";
  import { ws } from "../ws";

  let open = $state(false);
  let compacting = $state(false);

  const window_ = $derived(store.currentModelInfo?.contextWindow ?? 0);
  const usage = $derived(
    store.lastUsageSession === (store.session?.id ?? null)
      ? store.lastUsage
      : null,
  );
  // Estimated tokens for blocks appended after the last usage report.
  const est = $derived.by(() => {
    const blocks = store.blocks;
    let lastAssistantIdx = -1;
    for (let i = blocks.length - 1; i >= 0; i--)
      if (blocks[i].kind === "assistant") {
        lastAssistantIdx = i;
        break;
      }
    if (lastAssistantIdx < 0) return 0;
    let chars = 0;
    for (let i = lastAssistantIdx + 1; i < blocks.length; i++) {
      const b = blocks[i];
      if (b.kind === "user") chars += b.text.length;
      else if (b.kind === "system") chars += b.text.length;
      else if (b.kind === "tool") chars += b.argsPreview.length + b.output.length;
    }
    return Math.round(chars / 4);
  });
  const currentInput = $derived((usage?.promptTokens ?? 0) + est);
  const pct = $derived(window_ > 0 ? Math.min(1, currentInput / window_) : null);
  const unknown = $derived(!usage || !window_);
  // astra-1 G2: auto-compaction threshold (simple policy: window − reserve) and
  // the room left before it fires. Null when the policy or window is unknown —
  // the popup then says "not reported" / "disabled", never a fabricated zero.
  const policy = $derived(store.compactionPolicy);
  const threshold = $derived(
    policy?.available && window_ > 0 ? window_ - policy.reserveTokens : null,
  );
  const roomBefore = $derived(
    threshold !== null ? Math.max(0, threshold - currentInput) : null,
  );

  let lastCompaction = $derived.by(() => {
    for (let i = store.blocks.length - 1; i >= 0; i--) {
      const b = store.blocks[i];
      if (b.kind === "system" && b.text.startsWith("Compaction:"))
        return b.text;
    }
    return null;
  });

  /**
   * astra-1 G2: category estimates with a DECLARED, NON-OVERLAPPING counting
   * convention. Everything derives from store state (no polling):
   *
   *  unit: estimated tokens = round(chars / 4) per category — the same ¼-token
   *        heuristic the meter's `est` uses; the total is a TRANSCRIPT estimate
   *        (it may differ from the provider-measured prompt tokens).
   *
   *  category mapping (each block's text counts toward EXACTLY ONE category):
   *    project instructions : project_context block `text` (the effective
   *                           instructions snapshot taken at switch/refresh)
   *    conversation         : user block text + assistant text + assistant
   *                           thinking text (thinking is model-visible)
   *    tool results         : tool call argsJson + result on assistant blocks,
   *                           plus standalone tool blocks (argsPreview +
   *                           output); a call lives in one of those places
   *                           per the store's event flow, never both
   *    system & notices     : system block text (system_note / compaction)
   *
   *  NOT reported client-side: the base system prompt, tool definitions and
   *  tool guidelines are layered server-side (PLAN §16-17) and their text never
   *  reaches the browser — that row shows "— (not reported)", never a zero.
   */
  const breakdown = $derived.by(() => {
    let projectChars = 0;
    let conversationChars = 0;
    let toolChars = 0;
    let systemChars = 0;
    for (const b of store.blocks) {
      switch (b.kind) {
        case "user":
          conversationChars += b.text.length;
          break;
        case "assistant":
          conversationChars += b.text.length + (b.thinking?.text.length ?? 0);
          for (const c of b.toolCalls)
            toolChars += c.argsJson.length + (c.result?.length ?? 0);
          break;
        case "tool":
          toolChars += b.argsPreview.length + b.output.length;
          break;
        case "system":
          systemChars += b.text.length;
          break;
        case "project_context":
          projectChars += b.text.length;
          break;
      }
    }
    const est = (chars: number) => Math.round(chars / 4);
    return [
      { label: "project instructions", tokens: est(projectChars) },
      { label: "conversation", tokens: est(conversationChars) },
      { label: "tool results", tokens: est(toolChars) },
      { label: "system & notices", tokens: est(systemChars) },
      // Server-side layers (base prompt, tool definitions, guidelines) —
      // the client never sees their text, so this stays honest.
      { label: "system prompt + tools", tokens: null },
    ];
  });

  function fmt(n: number): string {
    return n.toLocaleString("en-US");
  }

  function compactNow() {
    if (compacting || store.busy) return;
    compacting = true;
    ws.request("session.compact", { sessionId: store.session?.id })
      .catch((e) => store.setError(String(e)))
      .finally(() => (compacting = false));
  }
</script>

<div class="ctx" class:unknown>
  <button
    class="ctx-circle"
    aria-label={
      unknown
        ? "context usage: unknown (no usage reported for this session yet)"
        : `context usage ${fmt(currentInput)} of ${fmt(window_)} tokens (${Math.round((pct ?? 0) * 100)}%)`
    }
    aria-expanded={open}
    title="Context usage"
    onclick={() => (open = !open)}
  >
    <svg viewBox="0 0 20 20" aria-hidden="true">
      <circle cx="10" cy="10" r="8" class="ctx-ring-bg" />
      <circle
        cx="10"
        cy="10"
        r="8"
        class="ctx-ring"
        stroke-dasharray="50.27"
        stroke-dashoffset={pct == null ? 50.27 : 50.27 * (1 - pct)}
      />
    </svg>
  </button>

  {#if open}
    <div class="ctx-popup" role="dialog" aria-label="Context usage" tabindex="-1"
         onkeydown={(e) => { if (e.key === "Escape") open = false; }}>
      <div class="ctx-title">Context — {store.session?.title ?? "session"}</div>
      {#if unknown}
        <div class="ctx-row">
          <span>usage</span><span>Unknown — no usage reported for this session yet</span>
        </div>
      {:else}
        <div class="ctx-row">
          <span>context</span><span>{fmt(currentInput)} / {fmt(window_)} tokens</span>
        </div>
        <div class="ctx-row">
          <span>used</span><span>{Math.round((pct ?? 0) * 100)}%</span>
        </div>
        <div class="ctx-row">
          <span>last measured</span><span>{fmt(usage!.promptTokens ?? 0)} input tokens</span>
        </div>
        {#if est > 0}
          <div class="ctx-row">
            <span>estimate</span><span>+{fmt(est)} since last request (estimate)</span>
          </div>
        {/if}
      {/if}
      <div class="ctx-breakdown">
          <div class="ctx-row ctx-breakdown-head">
            <span>estimate by category</span>
            <span>tokens (chars ÷ 4)</span>
          </div>
          {#each breakdown as row}
            <div class="ctx-row">
              <span>{row.label}</span>
              <span>{row.tokens === null ? "— (not reported)" : fmt(row.tokens)}</span>
            </div>
          {/each}
          <div class="ctx-row">
            <span>transcript total</span>
            <span>{fmt(breakdown.reduce((s, r) => s + (r.tokens ?? 0), 0))} (estimate)</span>
          </div>
        </div>
      <div class="ctx-row">
        {#if threshold !== null}
          <span>auto-compact</span>
          <span>threshold {fmt(threshold)} · {roomBefore === 0 ? "at limit" : roomBefore + " left"}</span>
        {:else}
          <span>auto-compact</span>
          <span>{policy?.available ? "threshold not reported" : "disabled"}</span>
        {/if}
      </div>
      {#if lastCompaction}
        <div class="ctx-row">
          <span>last compaction</span><span>yes (this transcript)</span>
        </div>
      {:else}
        <div class="ctx-row">
          <span>last compaction</span><span>none</span>
        </div>
      {/if}
      <button
        class="ctx-compact"
        disabled={store.busy || compacting || unknown}
        title={store.busy ? "Run in progress — compaction applies at a turn boundary" : "Compact context now"}
        onclick={compactNow}
      >
        {compacting ? "Compacting…" : "Compact now"}
      </button>
    </div>
  {/if}
</div>
