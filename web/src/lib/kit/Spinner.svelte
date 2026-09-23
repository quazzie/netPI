<script>
  let { size = 12, title = 'Working…' } = $props();
</script>

<span class="np-spinner" style:width="{size}px" style:height="{size}px" role="status" aria-label={title}></span>
