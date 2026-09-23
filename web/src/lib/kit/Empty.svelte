<script>
  import Icon from './Icon.svelte';
  let { icon = undefined, children } = $props();
</script>

<div class="np-empty">
  {#if icon}<Icon name={icon} size={22} />{/if}
  {@render children?.()}
</div>
