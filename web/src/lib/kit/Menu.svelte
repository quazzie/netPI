<script>
  import { tick } from 'svelte';
  import Icon from './Icon.svelte';
  /**
   * Dropdown / context menu (fixed position, closes on outside click or Esc).
   *   items: ({ label, icon?, hint?, checked?, danger?, disabled?, onclick } | { divider: true } | { header })[]
   * Use the `trigger` snippet ({ toggle, open }) for a dropdown button, or call openAt(x, y, items?) /
   * openFor(element, items?) on the component instance for a context menu.
   */
  let { items = [], trigger = undefined, placement = 'bottom-end', minWidth = 170 } = $props();

  let open = $state(false);
  let x = $state(0);
  let y = $state(0);
  let menuEl = $state();
  let anchorEl = $state();
  let override = $state.raw(null);
  const list = $derived(override ?? items);

  async function clamp() {
    await tick();
    if (!menuEl) return;
    const r = menuEl.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    if (x + r.width > vw - 6) x = Math.max(6, vw - r.width - 6);
    if (y + r.height > vh - 6) y = Math.max(6, y - r.height - (anchorEl ? anchorEl.getBoundingClientRect().height + 8 : 0));
  }

  export function openAt(px, py, its) {
    override = its ?? null;
    x = px;
    y = py;
    open = true;
    clamp();
  }
  export function openFor(el, its) {
    const r = el.getBoundingClientRect();
    openAt(placement.endsWith('start') ? r.left : Math.max(6, r.right - minWidth), r.bottom + 4, its);
  }
  export function close() {
    open = false;
  }
  function toggle(e) {
    e?.stopPropagation?.();
    if (open) return close();
    openFor(anchorEl);
  }

  $effect(() => {
    if (!open) return;
    const onDown = (e) => {
      if (menuEl?.contains(e.target) || anchorEl?.contains(e.target)) return;
      open = false;
    };
    const onKey = (e) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        open = false;
      }
    };
    const onScroll = (e) => {
      if (!menuEl?.contains(e.target)) open = false;
    };
    window.addEventListener('pointerdown', onDown, true);
    window.addEventListener('keydown', onKey, true);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('blur', close);
    return () => {
      window.removeEventListener('pointerdown', onDown, true);
      window.removeEventListener('keydown', onKey, true);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('blur', close);
    };
  });

  function run(it) {
    if (it.disabled) return;
    open = false;
    it.onclick?.();
  }
</script>

{#if trigger}
  <span class="np-menu-anchor" bind:this={anchorEl}>{@render trigger({ toggle, open })}</span>
{/if}
{#if open}
  <div class="np-menu" role="menu" bind:this={menuEl} style:left="{x}px" style:top="{y}px" style:min-width="{minWidth}px">
    {#each list as it, i (i)}
      {#if it.divider}
        <div class="np-menu-divider"></div>
      {:else if it.header}
        <div class="np-menu-header">{it.header}</div>
      {:else}
        <button type="button" role="menuitem" class="np-menu-item" class:danger={it.danger} disabled={it.disabled} onclick={() => run(it)}>
          <span class="ic">{#if it.checked}<Icon name="check" size={13} />{:else if it.icon}<Icon name={it.icon} size={13} />{/if}</span>
          <span class="label">{it.label}</span>
          {#if it.hint}<span class="hint">{it.hint}</span>{/if}
        </button>
      {/if}
    {/each}
  </div>
{/if}

<style>
  .np-menu-anchor {
    display: inline-flex;
  }
  .np-menu {
    position: fixed;
    z-index: 70;
    padding: 4px;
    border: 1px solid var(--border-strong);
    border-radius: var(--radius);
    background: var(--bg-1);
    box-shadow: var(--shadow);
    max-height: 60vh;
    overflow-y: auto;
    animation: np-menu-in 120ms var(--ease);
  }
  .np-menu-item {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    height: 28px;
    padding: 0 10px 0 6px;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--fg);
    font: inherit;
    font-size: var(--fs);
    text-align: left;
    white-space: nowrap;
    cursor: pointer;
  }
  .np-menu-item:hover:not(:disabled) {
    background: var(--bg-3);
  }
  .np-menu-item:disabled {
    opacity: 0.45;
    cursor: default;
  }
  .np-menu-item.danger {
    color: var(--err);
  }
  .ic {
    display: grid;
    place-items: center;
    width: 16px;
    color: var(--fg-dim);
  }
  .danger .ic {
    color: var(--err);
  }
  .label {
    flex: 1;
  }
  .hint {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
  .np-menu-divider {
    height: 1px;
    margin: 4px 2px;
    background: var(--border);
  }
  .np-menu-header {
    padding: 6px 8px 2px;
    font-size: var(--fs-xs);
    font-weight: 600;
    letter-spacing: 0.05em;
    text-transform: uppercase;
    color: var(--fg-dim);
  }
  @keyframes np-menu-in {
    from {
      opacity: 0;
      transform: translateY(-2px);
    }
  }
</style>
