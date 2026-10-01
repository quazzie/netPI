<script>
  /**
   * The session's goal (session meta "goal", plugins/NetPI.Goal) above the composer: status, objective, automatic runs
   * and tokens, and pause / resume / edit / clear. Why it paused, or the completion summary, shows underneath.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import { goalOf, editGoal, pauseGoal, resumeGoal, clearGoal } from '../../lib/goal.js';
  import { isBusy, hasRpc } from '../../lib/state/app.svelte.js';
  import { tokens } from '../../lib/format.js';
  let { session } = $props();

  const g = $derived(goalOf(session));
  // active but nothing running for a while (NetPI restarted, or a run failed before it started): offer to continue.
  // Between two automatic runs the agent is idle for a moment, hence the delay.
  const busy = $derived(isBusy(session.id));
  let stalled = $state(false);
  $effect(() => {
    stalled = false;
    if (g?.status !== 'active' || busy) return;
    const t = setTimeout(() => (stalled = true), 1500);
    return () => clearTimeout(t);
  });
  const label = $derived(
    !g ? '' : g.status === 'execution-unavailable' ? 'Execution unavailable' : g.status === 'active' ? 'Goal' : g.status === 'paused' ? 'Paused' : g.status === 'blocked' ? 'Needs you' : 'Achieved',
  );
  const used = $derived(
    g?.tokensUsed ? `${tokens(g.tokensUsed)}${g.tokenBudget ? ` / ${tokens(g.tokenBudget)}` : ''} tok` : '',
  );
  let open = $state(false);
</script>

{#if g}
  <div class="goal" data-status={g.status}>
    <div class="bar">
      <button class="main" onclick={() => (open = !open)} aria-expanded={open} title={open ? 'Hide the goal' : g.objective}>
        <Icon name="target" size={13} />
        <span class="state">{label}</span>
        <span class="np-ellipsis obj">{g.objective}</span>
      </button>
      {#if g.continuations || used}
        <span class="meta np-mono" title="Automatic runs · tokens (input not read from the cache, plus output)"
          >{#if g.continuations}{g.continuations}×{/if}{#if g.continuations && used}{' · '}{/if}{used}</span
        >
      {/if}
      {#if g.status === 'active'}
        {#if stalled}
          <button class="act" title="Continue: start the agent on the goal again" onclick={() => resumeGoal(session.id)}
            ><Icon name="play" size={12} /></button
          >
        {/if}
        <button class="act" title="Pause: no new run starts (the current one finishes; Esc stops it)" onclick={() => pauseGoal(session.id)}
          ><Icon name="pause" size={12} /></button
        >
      {:else if g.status !== 'complete'}
        <button class="act" title={hasRpc('agent.send') ? 'Resume' : 'Enable Runtime to resume'} disabled={!hasRpc('agent.send')} onclick={() => resumeGoal(session.id)}><Icon name="play" size={12} /></button>
      {/if}
      {#if g.status !== 'complete'}
        <button class="act" title="Edit the goal" onclick={() => editGoal(session.id)}><Icon name="pencil" size={12} /></button>
      {/if}
      <button class="act" title={g.status === 'complete' ? 'Dismiss' : 'Remove the goal'} onclick={() => clearGoal(session.id)}
        ><Icon name="x" size={12} /></button
      >
    </div>
    {#if open}<div class="full np-scroll">{g.objective}</div>{/if}
    {#if g.reason && g.status !== 'active'}<div class="reason np-scroll">{g.reason}</div>{/if}
  </div>
{/if}

<style>
  .goal {
    margin: 0 0 8px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-1);
    overflow: hidden;
  }
  .goal[data-status='active'] {
    border-color: var(--accent-line);
  }
  .bar {
    display: flex;
    align-items: center;
    gap: 4px;
    height: 30px;
    padding: 0 4px 0 0;
    min-width: 0;
  }
  .main {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 8px;
    height: 100%;
    padding: 0 6px 0 10px;
    border: 0;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    text-align: left;
  }
  .main:hover {
    background: var(--bg-2);
  }
  .goal[data-status='active'] .main :global(svg) {
    color: var(--accent);
  }
  .state {
    flex: none;
    font-weight: 550;
    color: var(--fg);
  }
  .goal[data-status='blocked'] .state {
    color: var(--warn);
  }
  .goal[data-status='complete'] .state {
    color: var(--ok);
  }
  .obj {
    flex: 1;
    color: var(--fg-muted);
  }
  .meta {
    flex: none;
    color: var(--fg-dim);
    font-size: 11px;
    padding: 0 4px;
  }
  .act {
    flex: none;
    display: grid;
    place-items: center;
    width: 24px;
    height: 24px;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--fg-dim);
  }
  .act:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .full,
  .reason {
    max-height: 160px;
    padding: 6px 12px 8px;
    border-top: 1px solid var(--border);
    font-size: var(--fs-sm);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .full {
    color: var(--fg);
  }
  .reason {
    color: var(--fg-muted);
  }
</style>
