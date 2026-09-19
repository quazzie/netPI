<script lang="ts">
  import { store } from "../store.svelte";

  let ctx = $derived(
    store.stats.contextTokens && store.stats.contextLimit
      ? `${fmtK(store.stats.contextTokens)} / ${fmtK(store.stats.contextLimit)}`
      : "",
  );
  let ctxClass = $derived.by(() => {
    if (!store.stats.contextTokens || !store.stats.contextLimit) return "";
    const ratio = store.stats.contextTokens / store.stats.contextLimit;
    return ratio > 0.9 ? "ctx crit" : ratio > 0.75 ? "ctx warn" : "ctx";
  });
  function fmtK(n: number): string {
    return n >= 1000 ? `${(n / 1000).toFixed(n >= 100000 ? 0 : 1)}k` : `${n}`;
  }
</script>

<div class="statusline">
  <span>{store.stats.turns} turns</span>
  <span>{store.stats.toolSteps} tool steps</span>
  {#if store.stats.tokensPerSec}
    <span>{store.stats.tokensPerSec} tok/s</span>
  {/if}
  {#if store.lastUsage?.cacheHit}
    <span>cache {store.lastUsage.cacheHit}</span>
  {/if}
  {#if ctx}
    <span class={ctxClass}>{ctx}</span>
  {/if}
</div>
