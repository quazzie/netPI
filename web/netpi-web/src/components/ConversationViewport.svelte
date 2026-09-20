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
  let scrollFrame = 0;

  const nearBottom = () => {
    if (!viewport) return true;
    return viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight < 96;
  };

  function scheduleBottom(force = false) {
    if (!viewport || (!stick && !force)) return;
    cancelAnimationFrame(scrollFrame);
    scrollFrame = requestAnimationFrame(() => {
      if (!viewport) return;
      viewport.scrollTop = viewport.scrollHeight;
      stick = true;
      unseen = false;
    });
  }

  function onScroll() {
    if (!viewport) return;
    const nowSticky = nearBottom();
    if (nowSticky) unseen = false;
    stick = nowSticky;

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
    if (stick) scheduleBottom();
    else if (store.busy) unseen = true;
  });

  // Markdown rendering, tool expansion, fonts and streamed shell output can all
  // change row height after the store mutation. ResizeObserver catches those
  // layout changes and keeps "follow output" reliable.
  $effect(() => {
    if (!list) return;
    const observer = new ResizeObserver(() => {
      if (stick) scheduleBottom();
    });
    observer.observe(list);
    return () => observer.disconnect();
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
    scheduleBottom(true);
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
