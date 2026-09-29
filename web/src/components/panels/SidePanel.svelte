<script>
  import { SvelteSet } from 'svelte/reactivity';
  import { untrack } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import PluginTabHost from './PluginTabHost.svelte';
  import { layout, saveLayout } from '../../lib/state/ui.svelte.js';
  import { tabs } from '../../lib/state/tabs.svelte.js';

  /** side: 'left' | 'right'. The vertical tab strip sits on the outer edge. */
  let { side } = $props();

  const st = $derived(layout[side]);
  const list = $derived(side === 'left' ? tabs.left : tabs.right);
  const active = $derived(list.find((t) => t.key === st.active) ?? list[0] ?? null);
  const open = $derived(!!active && !st.collapsed);

  // Tabs stay mounted (hidden) once visited so their state survives switching. Core tabs (the built-ins, which have
  // their own component) are cheap, so they stay mounted for good; plugin tabs hold real state, so they are bounded:
  // a plugin tab keeps its state only while it stays within the last 3 visited plugin tabs — beyond that it is
  // unmounted and rebuilt when it is visited again.
  const KEEP_PLUGIN_TABS = 3;
  const visited = new SvelteSet(); // core tab keys, once seen
  let recentPlugins = $state.raw([]); // plugin tab keys, most recent first
  $effect(() => {
    if (!open || !active) return;
    if (active.component) visited.add(active.key);
    // untrack: the effect writes recentPlugins and would otherwise also read it, which Svelte takes for a
    // self-dependency and reports as effect_update_depth_exceeded.
    else recentPlugins = untrack(() => [active.key, ...recentPlugins.filter((k) => k !== active.key)].slice(0, KEEP_PLUGIN_TABS));
  });
  const mounted = $derived(
    // the active tab is always mounted (the visit may have been recorded only now)
    list.filter((t) => t.key === active?.key || (t.component ? visited.has(t.key) : recentPlugins.includes(t.key))),
  );

  function clickTab(t) {
    if (active?.key === t.key && !st.collapsed) st.collapsed = true;
    else {
      st.active = t.key;
      st.collapsed = false;
    }
    saveLayout();
  }

  // ---- resize
  const MIN = 200;
  let dragging = $state(false);
  function onResizeStart(e) {
    e.preventDefault();
    const startX = e.clientX;
    const startW = st.width;
    const max = Math.max(MIN, Math.min(720, window.innerWidth * 0.5));
    dragging = true;
    const handle = e.currentTarget;
    handle.setPointerCapture(e.pointerId);
    const move = (ev) => {
      const dx = ev.clientX - startX;
      st.width = Math.round(Math.max(MIN, Math.min(max, side === 'left' ? startW + dx : startW - dx)));
    };
    const up = () => {
      dragging = false;
      handle.removeEventListener('pointermove', move);
      handle.removeEventListener('pointerup', up);
      handle.removeEventListener('pointercancel', up);
      saveLayout();
    };
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', up);
    handle.addEventListener('pointercancel', up);
  }
  function onResizeKey(e) {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    e.preventDefault();
    const d = (e.key === 'ArrowRight' ? 16 : -16) * (side === 'left' ? 1 : -1);
    st.width = Math.max(MIN, Math.min(720, st.width + d));
    saveLayout();
  }
</script>

{#if list.length}
  <aside class="panel {side}" class:open class:dragging>
    <div class="strip" role="tablist" aria-orientation="vertical" aria-label="{side} panel">
      {#each list as t (t.key)}
        <button
          class="strip-tab"
          role="tab"
          aria-selected={open && active?.key === t.key}
          title={t.title}
          onclick={() => clickTab(t)}
        >
          <Icon name={t.icon ?? 'puzzle'} size={15} />
          <span class="strip-label">{t.title}</span>
        </button>
      {/each}
    </div>

    <div class="body" style:width="{st.width}px" hidden={!open}>
      {#each mounted as t (t.key)}
        <div class="pane" hidden={active?.key !== t.key}>
          {#if t.component}
            <t.component visible={open && active?.key === t.key} />
          {:else}
            <PluginTabHost tab={t.plugin} visible={open && active?.key === t.key} />
          {/if}
        </div>
      {/each}
      <!-- svelte-ignore a11y_no_noninteractive_element_interactions, a11y_no_noninteractive_tabindex -->
      <div
        class="resizer"
        role="separator"
        aria-orientation="vertical"
        aria-label="Resize panel"
        tabindex="0"
        onpointerdown={onResizeStart}
        onkeydown={onResizeKey}
        ondblclick={() => {
          st.width = side === 'left' ? 272 : 360;
          saveLayout();
        }}
      ></div>
    </div>
  </aside>
{/if}

<style>
  .panel {
    display: flex;
    flex: none;
    min-height: 0;
    background: var(--bg-1);
    position: relative;
  }
  .panel.left {
    flex-direction: row;
    border-right: 1px solid var(--border);
  }
  .panel.right {
    flex-direction: row-reverse;
    border-left: 1px solid var(--border);
  }
  .panel.dragging {
    user-select: none;
  }
  .strip {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 2px;
    width: var(--strip-w);
    padding: 6px 0;
    flex: none;
    background: var(--bg-1);
  }
  .panel.left.open .strip {
    border-right: 1px solid var(--border);
  }
  .panel.right.open .strip {
    border-left: 1px solid var(--border);
  }
  .strip-tab {
    display: flex;
    align-items: center;
    gap: 7px;
    width: 26px;
    padding: 9px 0;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-dim);
    writing-mode: vertical-rl;
    position: relative;
    transition:
      background var(--t-fast),
      color var(--t-fast);
  }
  .panel.left .strip-tab {
    transform: rotate(180deg);
  }
  /* the left strip is rotated 180° (labels read bottom-to-top): keep icons upright */
  .panel.left .strip-tab :global(svg) {
    transform: rotate(180deg);
  }
  .strip-tab:hover {
    color: var(--fg);
    background: var(--bg-2);
  }
  .strip-tab[aria-selected='true'] {
    color: var(--fg);
    background: var(--bg-3);
  }
  .strip-tab[aria-selected='true']::before {
    content: '';
    position: absolute;
    top: 8px;
    bottom: 8px;
    width: 2px;
    border-radius: 2px;
    background: var(--accent);
    right: -4px; /* outer edge on both sides (the left strip is rotated) */
  }
  .strip-label {
    font-size: 11.5px;
    font-weight: 500;
    letter-spacing: 0.02em;
    white-space: nowrap;
  }
  .body {
    position: relative;
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0;
  }
  .pane {
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
  }
  .pane[hidden] {
    display: none;
  }
  .resizer {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 7px;
    z-index: 5;
    cursor: col-resize;
    outline: none;
  }
  .panel.left .resizer {
    right: -4px;
  }
  .panel.right .resizer {
    left: -4px;
  }
  .resizer::after {
    content: '';
    position: absolute;
    top: 0;
    bottom: 0;
    left: 3px;
    width: 1px;
    background: transparent;
    transition: background var(--t);
  }
  .resizer:hover::after,
  .resizer:focus-visible::after,
  .panel.dragging .resizer::after {
    background: var(--accent);
    width: 2px;
    left: 2.5px;
  }
</style>
