<script>
  import Icon from './Icon.svelte';
  /** Two-step button for quick destructive actions: the first click arms it ("Kill?"), the second confirms. */
  let { icon = 'trash', label = '', confirmLabel = 'Sure?', title = undefined, onconfirm, disabled = false } = $props();
  let armed = $state(false);
  let timer = 0;
  function click(e) {
    e.stopPropagation();
    if (armed) {
      clearTimeout(timer);
      armed = false;
      onconfirm?.();
    } else {
      armed = true;
      clearTimeout(timer);
      timer = setTimeout(() => (armed = false), 2500);
    }
  }
</script>

<button
  type="button"
  class="np-confirm"
  class:armed
  class:icon-only={!label && !armed}
  {title}
  aria-label={title ?? label}
  {disabled}
  onclick={click}
  onblur={() => (armed = false)}
>
  {#if !armed}<Icon name={icon} size={13} />{/if}
  {#if armed}{confirmLabel}{:else if label}{label}{/if}
</button>

<style>
  .np-confirm {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 4px;
    height: 22px;
    min-width: 22px;
    padding: 0 6px;
    border: 1px solid transparent;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--fg-dim);
    font: inherit;
    font-size: var(--fs-xs);
    white-space: nowrap;
    cursor: pointer;
    flex: none;
  }
  .np-confirm.icon-only {
    padding: 0;
  }
  .np-confirm:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  .np-confirm.armed {
    border-color: color-mix(in srgb, var(--err) 45%, transparent);
    background: var(--err-soft);
    color: var(--err);
    font-weight: 600;
  }
  .np-confirm:disabled {
    opacity: 0.4;
    pointer-events: none;
  }
</style>
