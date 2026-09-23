<script>
  /** A titled block. `actions` snippet renders right-aligned in the title row. */
  let { title = '', actions = undefined, children, class: cls = '', flush = false } = $props();
</script>

<section class="np-section {cls}" class:flush>
  {#if title || actions}
    <h3 class="np-section-title">
      <span>{title}</span>
      {#if actions}<span class="np-section-actions">{@render actions()}</span>{/if}
    </h3>
  {/if}
  {@render children?.()}
</section>

<style>
  .flush {
    padding-left: 0;
    padding-right: 0;
  }
</style>
