<script>
  /** status: running | queued | yielded | idle | unread | error | ok | warn | loaded | unloaded | stopped | offline … */
  let { status = 'idle', title = undefined } = $props();
</script>

<span class="np-dot" data-status={status} title={title ?? status} role="img" aria-label={title ?? status}></span>
