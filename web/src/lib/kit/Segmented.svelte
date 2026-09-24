<script>
  import { flushSync } from 'svelte';
  import Icon from './Icon.svelte';
  /**
   * Segmented control. options: { value, label, icon?, count?, title?, tone? }[]
   * `fit` (default true) keeps it inside its container's width, stepping down until it fits: icons + labels →
   * labels only → icons + the selected option's label → icons only (the last two need an `icon` on every option;
   * hidden labels move into the tooltip). `tone: 'err'` colors the count (e.g. failed plugins).
   */
  let { options = [], value = $bindable(), class: cls = '', onchange = undefined, fit = true } = $props();

  let el = $state();
  let mode = $state('full'); // full | text | active | icons
  const canIcons = $derived(options.every((o) => o.icon));

  function measure() {
    if (!el || !fit) return;
    const steps = canIcons ? ['full', 'text', 'active', 'icons'] : ['full'];
    for (const m of steps) {
      mode = m;
      flushSync();
      if (el.scrollWidth <= el.clientWidth + 1) break;
    }
  }
  $effect(() => {
    if (!fit || !el) return;
    // the observer fires once right away and again whenever the available width or the selection changes
    const ro = new ResizeObserver(() => measure());
    ro.observe(el.parentElement ?? el);
    return () => ro.disconnect();
  });
  $effect(() => {
    void value;
    if (fit && el) requestAnimationFrame(measure);
  });
  const showLabel = (o) => mode === 'full' || mode === 'text' || (mode === 'active' && value === o.value) || !o.icon;
  const showIcon = (o) => o.icon && mode !== 'text';
</script>

<div class="np-seg {cls}" class:np-seg-fit={fit} data-mode={mode} role="group" bind:this={el}>
  {#each options as o (o.value)}
    <button
      type="button"
      aria-pressed={value === o.value}
      aria-label={o.label}
      data-value={o.value}
      title={o.title ?? (showLabel(o) ? undefined : o.label)}
      onclick={() => {
        value = o.value;
        onchange?.(o.value);
      }}
    >
      {#if showIcon(o)}<Icon name={o.icon} size={13} />{/if}
      {#if showLabel(o)}<span class="np-seg-label">{o.label}</span>{/if}
      {#if o.count != null && o.count !== ''}<span class="np-seg-count" data-tone={o.tone}>{o.count}</span>{/if}
    </button>
  {/each}
</div>
