<script>
  import Icon from './Icon.svelte';
  /** Segmented control. options: { value, label, icon?, count?, title? }[] */
  let { options = [], value = $bindable(), class: cls = '', onchange = undefined } = $props();
</script>

<div class="np-seg {cls}" role="group">
  {#each options as o (o.value)}
    <button
      type="button"
      aria-pressed={value === o.value}
      title={o.title}
      onclick={() => {
        value = o.value;
        onchange?.(o.value);
      }}
    >
      {#if o.icon}<Icon name={o.icon} size={12} />{/if}
      {o.label}
      {#if o.count != null}<span class="np-seg-count">{o.count}</span>{/if}
    </button>
  {/each}
</div>
