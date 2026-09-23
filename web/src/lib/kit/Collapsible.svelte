<script>
  import Icon from './Icon.svelte';
  /** Disclosure block. Use `title` (string) or a `header` snippet; `open` is bindable. */
  let { title = '', header = undefined, open = $bindable(false), children, class: cls = '' } = $props();
</script>

<div class="np-collapsible {cls}">
  <button type="button" class="np-collapsible-head" aria-expanded={open} onclick={() => (open = !open)}>
    <span class="chev" class:open><Icon name="chevron-right" size={12} /></span>
    {#if header}{@render header()}{:else}<span class="np-ellipsis">{title}</span>{/if}
  </button>
  {#if open}
    <div class="np-collapsible-body">{@render children?.()}</div>
  {/if}
</div>

<style>
  .np-collapsible-head {
    display: flex;
    align-items: center;
    gap: 6px;
    width: 100%;
    min-height: 26px;
    padding: 2px 6px;
    border: 0;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--fg-muted);
    text-align: left;
    font-size: var(--fs);
  }
  .np-collapsible-head:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .chev {
    display: inline-grid;
    color: var(--fg-dim);
    transition: transform var(--t-fast);
  }
  .chev.open {
    transform: rotate(90deg);
  }
  .np-collapsible-body {
    padding: 4px 6px 6px 24px;
  }
</style>
