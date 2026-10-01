<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { dequeue, resendQueued, isBusy } from '../../lib/state/app.svelte.js';
  import { truncate } from '../../lib/format.js';

  /**
   * The person's own inputs waiting for the running agent (agent.queue): removable with agent.dequeue, and
   * sendable as the next turn once the agent is idle — a stop leaves a queued input with no run left to run
   * in, so the chip's send action starts one (resendQueued). Internal inputs (a subagent's report, a harness
   * notice) are not shown: they are the agents' business and must not be removed or resent.
   */
  let { chat } = $props();

  const busy = $derived(isBusy(chat.id));
</script>

<div class="queue">
  {#each chat.ownQueue as q (q.id)}
    <div class="chip" data-mode={q.mode} title={q.text}>
      <Icon name={q.mode === 'steer' ? 'steer' : 'queue'} size={12} />
      <span class="mode">{q.mode === 'steer' ? 'steer' : 'queued'}</span>
      <span class="text">{truncate(q.text.replace(/\s+/g, ' '), 80)}</span>
      {#if !busy}
        <button
          class="go"
          title="Send it now as the next message"
          aria-label="Send queued message"
          onclick={() => resendQueued(chat.id, q.id, q.text)}
        >
          <Icon name="arrow-up" size={11} stroke={2} />
        </button>
      {/if}
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
  .go {
    display: grid;
    place-items: center;
    width: 18px;
    height: 18px;
    padding: 0;
    border: 0;
    border-radius: 50%;
    background: transparent;
    color: var(--accent);
    flex: none;
  }
  .go:hover {
    background: var(--accent-soft);
  }
  @keyframes chip-in {
    from {
      opacity: 0;
      transform: translateY(4px);
    }
  }
</style>
