<script>
  /** tone: ok | warn | err | info | accent (default neutral) */
  let { tone = undefined, title = undefined, children } = $props();
</script>

<span class="np-badge" data-tone={tone} {title}>{@render children?.()}</span>
