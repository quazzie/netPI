<script>
  import Icon from '../../lib/kit/Icon.svelte';

  /** Footer for an assistant message that did not stop normally (error / aborted / length / filtered). */
  let { msg } = $props();
  const sr = $derived(msg.stopReason);
  const err = $derived(msg.meta?.error ?? msg.meta?.message ?? null);
  const text = $derived(
    sr === 'aborted'
      ? 'Stopped by user'
      : sr === 'length'
        ? 'Output hit the max-tokens limit'
        : sr === 'content_filter'
          ? 'Response blocked by the provider’s content filter'
          : 'The model call failed',
  );
</script>

<div class="status" data-reason={sr}>
  <Icon name={sr === 'aborted' ? 'ban' : sr === 'error' ? 'alert' : 'alert-circle'} size={13} />
  <span>{text}</span>
  {#if err}<span class="err np-mono">{err}</span>{/if}
</div>

<style>
  .status {
    display: flex;
    align-items: baseline;
    gap: 7px;
    padding: 5px 10px;
    border-radius: var(--radius-sm);
    font-size: var(--fs-sm);
    color: var(--fg-dim);
    flex-wrap: wrap;
  }
  .status :global(svg) {
    align-self: center;
  }
  .status[data-reason='error'] {
    background: var(--err-soft);
    color: var(--err);
  }
  .status[data-reason='length'],
  .status[data-reason='content_filter'] {
    background: var(--warn-soft);
    color: var(--warn);
  }
  .err {
    font-size: 11.5px;
    overflow-wrap: anywhere;
    opacity: 0.9;
  }
</style>
