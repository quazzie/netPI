<script>
  import { duration } from '../format.js';
  import { secondNow } from './ticker.svelte.js';
  /** Live elapsed time since `since` (until `until` when given). */
  let { since, until = undefined, class: cls = '' } = $props();
  const ms = $derived.by(() => {
    const a = since ? Date.parse(since) : NaN;
    if (!Number.isFinite(a)) return null;
    const b = until ? Date.parse(until) : secondNow();
    return Math.max(0, b - a);
  });
</script>

{#if ms != null}<span class="np-elapsed {cls}">{ms < 1000 ? '0s' : duration(ms < 60_000 ? Math.floor(ms / 1000) * 1000 : ms)}</span>{/if}

<style>
  .np-elapsed {
    font-variant-numeric: tabular-nums;
  }
</style>
