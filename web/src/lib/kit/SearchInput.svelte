<script>
  import Icon from './Icon.svelte';
  /** Search box with icon and clear button. Esc clears. */
  let { value = $bindable(''), placeholder = 'Search', onkeydown = undefined, class: cls = '', ...rest } = $props();
</script>

<label class="np-search {cls}">
  <Icon name="search" size={13} />
  <input
    bind:value
    {placeholder}
    spellcheck="false"
    onkeydown={(e) => {
      if (e.key === 'Escape' && value) {
        value = '';
        e.stopPropagation();
      }
      onkeydown?.(e);
    }}
    {...rest}
  />
  {#if value}
    <button type="button" class="np-search-clear" aria-label="Clear" onclick={() => (value = '')}>
      <Icon name="x" size={11} stroke={2} />
    </button>
  {/if}
</label>
