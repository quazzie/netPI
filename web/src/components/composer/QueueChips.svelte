<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { dequeue, resendQueued, promoteQueued, isBusy } from '../../lib/state/app.svelte.js';
  import { truncate, duration, toMs } from '../../lib/format.js';

  /**
   * The person's own inputs waiting for the running agent (agent.queue): removable with agent.dequeue, and
   * sendable as the next turn once the agent is idle — a stop leaves a queued input with no run left to run
   * in, so the chip's send action starts one (resendQueued). Internal inputs (a subagent's report, a harness
   * notice) are not shown: they are the agents' business and must not be removed or resent.
   */
  let { chat } = $props();

  const busy = $derived(isBusy(chat.id));

  // A follow-up waits for the whole run, and a worker's run can last its whole package (1–2 h): one that has waited
  // this long behind a running agent is flagged with its age, and "steer now" (agent.promote) has it read at the run's
  // next step instead (idea-equiw4). The same 10 minutes as the CLI's warning on a queued send.
  const LONG_WAIT_MS = 10 * 60_000;
  let now = $state(Date.now());
  $effect(() => {
    if (!busy || !chat.ownQueue.some((q) => q.mode !== 'steer')) return;
    now = Date.now();
    const timer = setInterval(() => (now = Date.now()), 30_000);
    return () => clearInterval(timer);
  });
  const waited = (q) => (q.createdAt ? Math.max(0, now - toMs(q.createdAt)) : 0);
  const longWait = (q) => busy && q.mode !== 'steer' && waited(q) >= LONG_WAIT_MS;
</script>

<div class="queue">
  {#each chat.ownQueue as q (q.id)}
    <div class="chip" data-mode={q.mode} data-long={longWait(q) || undefined} title={q.text}>
      <Icon name={q.mode === 'steer' ? 'steer' : 'queue'} size={12} />
      <span class="mode">{q.mode === 'steer' ? 'steer' : 'queued'}</span>
      {#if longWait(q)}
        <span class="age" title="Waiting {duration(waited(q))} for the run to end: a queued message is read only after the run">{duration(waited(q))}</span>
      {/if}
      <span class="text">{truncate(q.text.replace(/\s+/g, ' '), 80)}</span>
      {#if longWait(q)}
        <button class="steer" title="Have the agent read it at its next step instead of after the run" onclick={() => promoteQueued(chat.id, q.id)}>
          steer now
        </button>
      {/if}
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
  .chip[data-long] {
    border-color: var(--warn);
  }
  .age {
    color: var(--warn);
    font-size: var(--fs-xs);
    font-variant-numeric: tabular-nums;
    flex: none;
  }
  .steer {
    flex: none;
    height: 18px;
    padding: 0 7px;
    border: 1px solid var(--accent-line);
    border-radius: 9px;
    background: transparent;
    color: var(--accent);
    font-size: var(--fs-xs);
    white-space: nowrap;
  }
  .steer:hover {
    background: var(--accent-soft);
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
