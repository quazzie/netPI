<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { dequeue } from '../../lib/state/app.svelte.js';
  import { truncate } from '../../lib/format.js';

  /** Inputs waiting for the running agent (agent.queue); removable with agent.dequeue. */
  let { chat } = $props();
</script>

<div class="queue">
  {#each chat.queue as q (q.id)}
    <div class="chip" data-mode={q.mode} title={q.text}>
      <Icon name={q.mode === 'steer' ? 'steer' : 'queue'} size={12} />
      <span class="mode">{q.mode === 'steer' ? 'steer' : 'queued'}</span>
      <span class="text">{truncate(q.text.replace(/\s+/g, ' '), 80)}</span>
      <button class="x" title="Remove from queue" aria-label="Remove" onclick={() => dequeue(chat.id, q.id)}>
        <Icon name="x" size={11} stroke={2} />
      </button>
    </div>
  {/each}
</div>

<style>
  .queue {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
    padding: 0 2px 8px;
  }
  .chip {
    display: flex;
    align-items: center;
    gap: 6px;
    max-width: 100%;
    height: 26px;
    padding: 0 4px 0 9px;
    border: 1px solid var(--border-strong);
    border-radius: 13px;
    background: var(--bg-2);
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    animation: chip-in var(--t) var(--ease);
  }
  .chip[data-mode='steer'] {
    border-color: var(--accent-line);
  }
  .chip[data-mode='steer'] :global(svg:first-child),
  .chip[data-mode='steer'] .mode {
    color: var(--accent);
  }
  .mode {
    font-size: var(--fs-xs);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }
  .text {
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    color: var(--fg);
  }
  .x {
    display: grid;
    place-items: center;
    width: 18px;
    height: 18px;
    padding: 0;
    border: 0;
    border-radius: 50%;
    background: transparent;
    color: var(--fg-dim);
    flex: none;
  }
  .x:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  @keyframes chip-in {
    from {
      opacity: 0;
      transform: translateY(4px);
    }
  }
</style>
