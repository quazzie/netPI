<script lang="ts">
  import { store } from "../store.svelte";
  import BlockRenderer from "./conversation/BlockRenderer.svelte";
  import { ws } from "../ws";

  let viewport: HTMLElement | null = $state(null);

  // NOTE: the TanStack virtualizer was removed — it desynced (count stuck at 0)
  // and its store-driven options caused Svelte effect loops. A chat transcript
  // is short enough that a plain list renders instantly and never desyncs.

  // Auto-scroll to bottom when new content appears, unless the user scrolled up.
  let stick = $state(true);
  function onScroll() {
    if (!viewport) return;
    stick =
      viewport.scrollTop + viewport.clientHeight >= viewport.scrollHeight - 60;
    // PLAN §38: scrolling to the top triggers loading older entries.
    if (viewport.scrollTop <= 40) ws.loadOlder();
  }
  $effect(() => {
    const n = store.blocks.length;
    if (stick && viewport && n) viewport.scrollTop = viewport.scrollHeight;
  });

  // PLAN §38: when an older page is prepended the list grows at the top —
  // compensate the scroll so the block the user was looking at stays put.
  let lastVersion = store.prependVersion;
  let prevH = 0;
  $effect(() => {
    const v = store.prependVersion;
    const h = viewport ? viewport.scrollHeight : 0;
    if (v !== lastVersion && viewport) {
      viewport.scrollTop += h - prevH;
      lastVersion = v;
    }
    prevH = h;
  });
</script>

<div class="conversation" bind:this={viewport} onscroll={onScroll}>
  {#if store.blocks.length === 0}
    <div class="empty">netPI — send a message to start</div>
  {:else}
    <div class="msg-list">
      {#each store.blocks as b (b.id)}
        <BlockRenderer block={b} />
      {/each}
    </div>
  {/if}
</div>
