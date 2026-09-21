<script lang="ts">
  import { tick } from "svelte";
  import type { ThinkingBlock as ThinkingPart } from "../../types";
  import { ui } from "../../ui.svelte";

  let { thinking }: { thinking: ThinkingPart } = $props();

  // Per-block disclosure override: unset (follow the "Keep thinking open"
  // setting) | open | closed. Manual toggling always wins over the setting.
  let userOverride = $state<"unset" | "open" | "closed">("unset");
  // Inner scroll-follow, owned by this body. Kept separate from the outer
  // conversation's follow state: the outer viewport must not drive (or be
  // driven by) the inner 360px scroller.
  let innerFollow = $state(true);
  let bodyEl: HTMLElement | null = $state(null);
  // Last programmatic inner pin, so onBodyScroll() can ignore the scroll event
  // our own pin causes (otherwise a layout landing just after a pin reads the
  // body as "scrolled up" and kills follow permanently).
  let lastPin = { top: 0, at: 0 };
  // Tracks the open/close transition so we can position on first-open. Plain
  // (non-reactive) variable: reading + writing it inside the $effect below
  // must not make the effect depend on itself and re-trigger.
  let wasOpen = false;

  function fmt(ms?: number): string {
    if (!ms) return "";
    return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(1)}s`;
  }

  function latestLine(text: string): string {
    const visible = text.trimEnd();
    if (!visible) return "Thinking…";
    const newline = visible.lastIndexOf("\n");
    const line = (newline < 0 ? visible : visible.slice(newline + 1))
      .replaceAll("**", "")
      .replace(/\s+/g, " ")
      .trim();
    if (line.length <= 220) return line;
    return "…" + line.slice(-219);
  }

  // The "Keep thinking open" setting is the DEFAULT; a manual toggle overrides
  // it per block. Collapsed live preview only shows when both the setting and
  // the override say closed.
  let expanded = $derived(
    userOverride === "unset" ? ui.keepThinkingOpen : userOverride === "open",
  );
  // Render the pill + body (open surface) when done or expanded; otherwise the
  // one-line streaming preview (its appearance is unchanged).
  let useOpen = $derived(thinking.done || expanded);
  let finishedLabel = $derived(
    thinking.durationMs ? `Thinking · ${fmt(thinking.durationMs)}` : "Thinking",
  );

  // Pin the inner body to its bottom edge (the latest reasoning). A direct
  // scrollTop write is cheap and — unlike requestAnimationFrame — is NOT
  // throttled while the window is hidden/backgrounded (WebView2).
  function pinInner() {
    if (!bodyEl || !bodyEl.isConnected || !innerFollow) return;
    bodyEl.scrollTop = bodyEl.scrollHeight - bodyEl.clientHeight;
    lastPin.top = bodyEl.scrollTop;
    lastPin.at = performance.now();
  }

  // A real user scroll-up stops follow; returning near the bottom resumes it.
  // Our own programmatic pin is ignored (windowed by lastPin).
  function onBodyScroll() {
    if (!bodyEl) return;
    if (
      performance.now() - lastPin.at < 300 &&
      Math.abs(bodyEl.scrollTop - lastPin.top) < 1
    )
      return;
    const gap = bodyEl.scrollHeight - bodyEl.scrollTop - bodyEl.clientHeight;
    innerFollow = gap < 24;
  }
  // An explicit wheel/touch scroll-up is an intent to read, even mid-stream.
  function onBodyWheel(e: WheelEvent) {
    if (e.deltaY < 0) innerFollow = false;
  }

  // First-open positioning: a still-streaming body starts at the latest text
  // (follow on); a completed body starts at the top for reading (follow off).
  // This runs on the expanded/useOpen transition, after the body is mounted.
  $effect(() => {
    const open = useOpen && expanded && !!bodyEl;
    if (open && !wasOpen) {
      void tick().then(() => {
        if (!bodyEl || !bodyEl.isConnected) return; // detached/replaced
        if (thinking.done) {
          bodyEl.scrollTop = 0;
          innerFollow = false;
        } else {
          innerFollow = true;
          pinInner();
        }
      });
    }
    wasOpen = open;
  });

  // Follow the stream: on every text revision while expanded + live, settle to
  // the bottom after layout. tick() coalesces to one frame per update and is a
  // microtask (not rAF), so it still works in a backgrounded WebView2 window.
  $effect(() => {
    const rev = thinking.text.length;
    const live = !thinking.done;
    const open = useOpen && expanded;
    if (!open || !live) return;
    void tick().then(() => pinInner());
    return;
  });

  // Mount the body: attach user-scroll + visibility listeners, then tear them
  // down when the body closes, unmounts, or the session (block) changes.
  $effect(() => {
    const el = bodyEl;
    if (!el || !(useOpen && expanded)) return;
    el.addEventListener("scroll", onBodyScroll, { passive: true });
    el.addEventListener("wheel", onBodyWheel, { passive: true });
    const onVis = () => {
      // On visibility restoration, settle the visible streaming body only if it
      // was following; never force-scroll a reader on a focus change.
      if (document.visibilityState === "visible") pinInner();
    };
    document.addEventListener("visibilitychange", onVis);
    return () => {
      el.removeEventListener("scroll", onBodyScroll);
      el.removeEventListener("wheel", onBodyWheel);
      document.removeEventListener("visibilitychange", onVis);
    };
  });
</script>

{#if !useOpen}
  <div class="thinking-stream" title={thinking.text || "Thinking…"} aria-live="polite">
    <span class="thinking-row-icon" aria-hidden="true">◉</span>
    <span class="thinking-row-title">Think</span>
    <span class="thinking-row-sep" aria-hidden="true"></span>
    <span class="thinking-stream-line">{latestLine(thinking.text)}</span>
  </div>
{:else}
  <div class="thinking-done">
    <button
      class="thinking-pill"
      aria-expanded={expanded}
      aria-controls={`thinking-body-${thinking.id}`}
      onclick={() => (userOverride = userOverride === "open" ? "closed" : "open")}
    >
      <span class="thinking-pill-chevron">{expanded ? "⌄" : "›"}</span>
      <span>{finishedLabel}</span>
    </button>

    {#if expanded}
      <div id="thinking-body" class="thinking-body" bind:this={bodyEl}>{thinking.text}</div>
    {/if}
  </div>
{/if}
