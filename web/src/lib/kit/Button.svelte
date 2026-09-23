<script>
  import Icon from './Icon.svelte';
  /** variant: default | primary | ghost | danger ; size: md | sm */
  let {
    variant = 'default',
    size = 'md',
    icon = undefined,
    type = 'button',
    disabled = false,
    title = undefined,
    onclick = undefined,
    children,
    class: cls = '',
    ...rest
  } = $props();
</script>

<button
  {type}
  {disabled}
  {title}
  {onclick}
  class="np-btn {cls}"
  class:np-btn-primary={variant === 'primary'}
  class:np-btn-ghost={variant === 'ghost'}
  class:np-btn-danger={variant === 'danger'}
  class:np-btn-sm={size === 'sm'}
  {...rest}
>
  {#if icon}<Icon name={icon} size={size === 'sm' ? 13 : 14} />{/if}
  {@render children?.()}
</button>
