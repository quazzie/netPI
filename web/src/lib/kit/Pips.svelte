<script>
  /** Slot usage: busy/capacity pips (a bar when capacity is large), plus a queued count. */
  let { busy = 0, capacity = 0, queued = 0, max = 8 } = $props();
  const n = $derived(Math.max(0, Math.min(capacity ?? 0, max)));
  const tip = $derived(`${busy}/${capacity} busy${queued ? ` · ${queued} queued` : ''}`);
</script>

<span class="np-pips" title={tip} aria-label={tip}>
  {#if capacity > max}
    <span class="bar"><span class="fill" style:width="{Math.min(100, (busy / capacity) * 100)}%"></span></span>
  {:else}
    {#each { length: n } as _, i (i)}<span class="pip" class:on={i < busy}></span>{/each}
  {/if}
  {#if queued}<span class="q">+{queued}</span>{/if}
</span>

<style>
  .np-pips {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    flex: none;
  }
  .pip {
    width: 8px;
    height: 8px;
    border-radius: 2px;
    background: var(--bg-3);
    box-shadow: inset 0 0 0 1px var(--border-strong);
  }
  .pip.on {
    background: var(--accent);
    box-shadow: none;
  }
  .bar {
    width: 48px;
    height: 6px;
    border-radius: 3px;
    background: var(--bg-3);
    overflow: hidden;
  }
  .fill {
    display: block;
    height: 100%;
    background: var(--accent);
  }
  .q {
    margin-left: 2px;
    padding: 0 4px;
    border-radius: 6px;
    background: var(--warn-soft);
    color: var(--warn);
    font-size: 10.5px;
    font-weight: 600;
    line-height: 14px;
    font-variant-numeric: tabular-nums;
  }
</style>
