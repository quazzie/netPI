<script lang="ts">
  import { createVirtualizer } from "@tanstack/svelte-virtual";
  import { store } from "../store.svelte";
  import BlockRenderer from "./conversation/BlockRenderer.svelte";
  import { ws } from "../ws";

  let viewport: HTMLElement | null = $state(null);

  // TanStack virtualizer as a Svelte store ($ auto-subscribes).
  const virtualizer = createVirtualizer<HTMLElement, HTMLElement>({
    count: 0,
    getScrollElement: () => viewport,
    estimateSize: () => 88,
    overscan: 6,
    getItemKey: (i) => store.blocks[i]?.id ?? i,
    initialRect: { height: 600, width: 800 },
  });

  let v = $derived($virtualizer);
  let rows = $derived(v ? v.getVirtualItems() : []);
  let total = $derived(v ? v.getTotalSize() : 0);

  // Keep the virtualizer in sync with the block list and the viewport element.
  $effect(() => {
    const cur = $virtualizer;
    if (cur) cur.setOptions({ count: store.blocks.length });
  });
  $effect(() => {
    if (viewport) {
      const cur = $virtualizer;
      if (cur) cur.setOptions({ getScrollElement: () => viewport });
    }
  });

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
  // compensate the scroll so the block the user was looking at stays in place.
  let lastVersion = store.prependVersion;
  let totalAtVersion = total;
  $effect(() => {
    if (store.prependVersion !== lastVersion) {
      const delta = total - totalAtVersion;
      if (viewport && delta > 0) viewport.scrollTop += delta;
      lastVersion = store.prependVersion;
      totalAtVersion = total;
    } else {
      totalAtVersion = total;
    }
  });
</script>

<div class="conversation" bind:this={viewport} onscroll={onScroll}>
  {#if store.blocks.length === 0}
    <div class="empty">netPI — send a message to start</div>
  {:else}
    <div
      class="virtual-list"
      style="height: {total}px; position: relative"
    >
      {#each rows as row (row.key)}
        <div
          class="vrow"
          style="position: absolute; left: 0; right: 0; top: 0; transform: translateY({row.start}px)"
        >
          <BlockRenderer block={store.blocks[row.index]} />
        </div>
      {/each}
    </div>
  {/if}
</div>
