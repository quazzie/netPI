<script>
  import Icon from './Icon.svelte';
  let {
    icon,
    title,
    size = 'md',
    pressed = undefined,
    disabled = false,
    onclick = undefined,
    class: cls = '',
    ...rest
  } = $props();
</script>

<button
  type="button"
  class="np-icon-btn {cls}"
  class:np-btn-sm={size === 'sm'}
  {title}
  aria-label={title}
  aria-pressed={pressed}
  {disabled}
  {onclick}
  {...rest}
>
  <Icon name={icon} size={size === 'sm' ? 13 : 15} />
</button>
