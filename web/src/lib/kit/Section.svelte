<script>
  import { untrack } from 'svelte';
  import Icon from './Icon.svelte';
  /**
   * A titled block. `actions` snippet renders right-aligned in the title row. With `collapsible`, the title
   * toggles the body (`bind:open`); `storageKey` remembers the state in localStorage. `count` shows a small
   * number (or text) next to the title.
   */
  let {
    title = '',
    count = undefined,
    actions = undefined,
    children,
    class: cls = '',
    flush = false,
    collapsible = false,
    open = $bindable(true),
    storageKey = undefined,
  } = $props();

  const KEY = untrack(() => (storageKey ? `np.section.${storageKey}` : null));
  if (KEY) {
    try {
      const v = localStorage.getItem(KEY);
      if (v != null) open = v === '1';
    } catch {}
  }
  function toggle() {
    open = !open;
    if (KEY)
      try {
        localStorage.setItem(KEY, open ? '1' : '0');
      } catch {}
  }
</script>

<section class="np-section {cls}" class:flush class:np-section-collapsed={collapsible && !open}>
  {#if title || actions}
    <h3 class="np-section-title">
      {#if collapsible}
        <button type="button" class="np-section-toggle" aria-expanded={open} onclick={toggle}>
          <span class="chev" class:open><Icon name="chevron-right" size={11} stroke={2.2} /></span>
          <span>{title}</span>
          {#if count != null && count !== ''}<span class="np-section-count">{count}</span>{/if}
        </button>
      {:else}
        <span>{title}</span>
        {#if count != null && count !== ''}<span class="np-section-count">{count}</span>{/if}
      {/if}
      {#if actions}<span class="np-section-actions">{@render actions()}</span>{/if}
    </h3>
  {/if}
  {#if !collapsible || open}{@render children?.()}{/if}
</section>

<style>
  .flush {
    padding-left: 0;
    padding-right: 0;
  }
  .chev {
    display: inline-grid;
    transition: transform var(--t-fast);
  }
  .chev.open {
    transform: rotate(90deg);
  }
</style>
