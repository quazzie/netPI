<script lang="ts">
  import { tick } from "svelte";
  import { store, REVEAL_STEP } from "../store.svelte";
  import BlockRenderer from "./conversation/BlockRenderer.svelte";
  import AgentActivity from "./AgentActivity.svelte";
  import { ws } from "../ws";

  let viewport: HTMLElement | null = $state(null);
  let list: HTMLElement | null = $state(null);
  let stick = $state(true);
  let unseen = $state(false);
  let prependAnchorHeight = 0;
  let lastPrependVersion = 0;

  const nearBottom = () => {
    if (!viewport) return true;
    return viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight < 96;
  };

  // Pin directly instead of via requestAnimationFrame: rAF callbacks are
  // heavily throttled or paused while the window is backgrounded/hidden
  // (WebView2 included), which made the transcript fall behind long thinking
  // or text streams and then jump forward in chunks to catch up. A direct
  // scrollTop write is cheap; callers already sit post-layout (store effect,
  // ResizeObserver frame).
  // Tracks our last programmatic pin so onScroll() can ignore the scroll
  // event it causes. Without this, a layout that lands right after a pin
  // (streaming deltas, session replay bursts) puts the viewport >96px from
  // the bottom, nearBottom() reads false, and the follow died permanently.
  let pinTarget = 0;
  let pinAt = 0;

  function pinBottom(force = false) {
    if (!viewport || (!stick && !force)) return;
    const target = Math.max(0, viewport.scrollHeight - viewport.clientHeight);
    viewport.scrollTop = target;
    pinTarget = viewport.scrollTop;
    pinAt = performance.now();
    stick = true;
    unseen = false;
  }

  // astra-1 G1: remember follow intent per session so switching tabs back
  // restores where the user was reading (bottom-stuck or scrolled up).
  $effect(() => {
    const sid = store.session?.id ?? null;
    stick = (sid ? store.scrollStick[sid] : undefined) ?? true;
  });

  function onScroll() {
    if (!viewport) return;
    if (performance.now() - pinAt < 500 && Math.abs(viewport.scrollTop - pinTarget) < 1)
      return; // our own pin -- keep stick
    const nowSticky = nearBottom();
    if (nowSticky) unseen = false;
    stick = nowSticky;
    store.setScrollStick(store.session?.id ?? null, nowSticky);

    if (
      viewport.scrollTop <= 40 &&
      store.moreAvailable &&
      !store.olderLoading
    ) {
      prependAnchorHeight = viewport.scrollHeight;
      ws.loadOlder();
    }
  }

  // Streaming mutates the tail message without changing blocks.length. Track the
  // actual tail content so text/thinking/tool deltas keep a pinned transcript
  // following the response.
  let tailSignal = $derived.by(() => {
    const last = store.blocks[store.blocks.length - 1];
    if (!last) return `empty:${store.activity ?? ""}`;
    if (last.kind !== "assistant")
      return `${store.blocks.length}:${last.id}`;

    const tools = last.toolCalls
      .map((t) => `${t.id}:${t.argsJson.length}:${t.result?.length ?? -1}:${t.durationMs ?? 0}`)
      .join("|");
    return [
      store.blocks.length,
      last.id,
      last.text.length,
      last.thinking?.text.length ?? 0,
      last.thinking?.done ? 1 : 0,
      last.done ? 1 : 0,
      tools,
      store.activity ?? "",
    ].join(":");
  });

  $effect(() => {
    tailSignal;
    if (stick) pinBottom();
    else if (store.busy) unseen = true;
  });

  // Markdown rendering, tool expansion, fonts and streamed shell output can all
  // change row height after the store mutation. ResizeObserver catches those
  // layout changes and keeps "follow output" reliable.
  $effect(() => {
    if (!list) return;
    const observer = new ResizeObserver(() => {
      if (stick) pinBottom();
    });
    observer.observe(list);
    return () => observer.disconnect();
  });

  // Follow guarantee while the agent produces output. The store effect and
  // ResizeObserver cover the fast path, but any missed frame (layout
  // batching, WebView2 throttling) left the transcript behind the stream --
  // worst case, "not scrolling at all" for open thinking bodies. A fixed
  // cadence re-pin while sticky+busy keeps the viewport glued regardless.
  let followTimer = 0;
  $effect(() => {
    if (!store.busy) {
      if (followTimer) {
        clearInterval(followTimer);
        followTimer = 0;
        if (stick) pinBottom(); // final settle when the stream ends
      }
      return;
    }
    if (!followTimer)
      followTimer = window.setInterval(() => {
        if (stick) pinBottom();
      }, 30);
    return () => {
      if (followTimer) {
        clearInterval(followTimer);
        followTimer = 0;
      }
    };
  });

  // Preserve the user's visual anchor when older history is prepended.
  $effect(() => {
    const version = store.prependVersion;
    if (!viewport || version === lastPrependVersion) return;
    lastPrependVersion = version;
    void tick().then(() => {
      if (!viewport || prependAnchorHeight <= 0) return;
      viewport.scrollTop += viewport.scrollHeight - prependAnchorHeight;
      prependAnchorHeight = 0;
    });
  });

  function loadMore() {
    if (store.hidden > 0) store.revealMore(REVEAL_STEP);
    else if (store.moreAvailable && !store.olderLoading) ws.loadOlder();
  }

  function jumpToLatest() {
    stick = true;
    pinBottom(true);
  }
</script>

<div class="conversation" bind:this={viewport} onscroll={onScroll}>
  {#if store.blocks.length === 0 && !store.activity}
    <div class="empty">
      <div class="empty-mark">π</div>
      <div class="empty-title">What are we building?</div>
      <div class="empty-sub">Send a message, use <kbd>/</kbd> for commands or <kbd>@</kbd> to reference files.</div>
    </div>
  {:else}
    <div class="msg-list" bind:this={list}>
      {#if store.hidden > 0 || (store.blocks.length > 0 && store.moreAvailable)}
        <button class="load-earlier" onclick={loadMore} disabled={store.olderLoading && store.hidden === 0}>
          {store.hidden > 0 ? `↑ Load earlier (${store.hidden} hidden)` : "↑ Load older history"}
        </button>
      {/if}
      {#each store.revealed as b (b.id)}
        <BlockRenderer block={b} />
      {/each}
      <AgentActivity />
    </div>
  {/if}

  {#if !stick && unseen}
    <button class="jump-latest" onclick={jumpToLatest}>
      <span>↓</span> New output
    </button>
  {/if}
</div>
