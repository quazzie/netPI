<script>
  import { iconSvg } from './host.js';
  /** name: icon name from the host set, or an inline "<svg …>" string */
  let { name, size = 16, stroke = 1.75, class: cls = '', title = undefined } = $props();
  const inline = $derived(name && String(name).trimStart().startsWith('<'));
  const inner = $derived(inline ? '' : iconSvg(name));
</script>

{#if inline}
  <span class="np-icon {cls}" style:width="{size}px" style:height="{size}px" {title} aria-hidden="true">{@html name}</span>
{:else}
  <svg
    class="np-icon {cls}"
    width={size}
    height={size}
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width={stroke}
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    >{#if title}<title>{title}</title>{/if}{@html inner}</svg
  >
{/if}

<style>
  span.np-icon {
    display: inline-grid;
    place-items: center;
    flex: none;
  }
  span.np-icon :global(svg) {
    width: 100%;
    height: 100%;
  }
</style>
