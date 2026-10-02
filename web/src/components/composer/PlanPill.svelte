<script>
  /**
   * The chat's plan mode (session meta "planMode", plugins/NetPI.Plan) in the composer bar: off → click to plan first;
   * Planning (the agent explores read-only) → click to leave; Plan ready (a plan waits for the decision, in the chat) and
   * Plan approved are states only. Hidden without the plan plugin and in subagent chats.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import { planState, enterPlan, exitPlan } from '../../lib/state/plans.svelte.js';
  import { hasRpc } from '../../lib/state/app.svelte.js';
  import { toast } from '../../lib/state/ui.svelte.js';

  let { session } = $props();

  const state = $derived(planState(session));
  const title = $derived(session.meta?.planMode?.title);
  const label = $derived(state === 'planning' ? 'Planning' : state === 'awaiting' ? 'Plan ready' : state === 'approved' ? 'Plan approved' : 'Plan');
  const tip = $derived(
    state === 'planning'
      ? 'Plan mode is on: the agent explores read-only and submits a plan. Click to leave.'
      : state === 'awaiting'
        ? `The plan${title ? ` “${title}”` : ''} waits for your decision in the chat`
        : state === 'approved'
          ? `Plan${title ? ` “${title}”` : ''} approved. Click to plan again.`
          : 'Plan first: the agent explores read-only and brings you a plan to approve (/plan)',
  );
  let busy = $state(false);

  async function toggle() {
    if (busy || state === 'awaiting') return;
    busy = true;
    try {
      if (state === 'planning') await exitPlan(session.id);
      else await enterPlan(session.id);
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      busy = false;
    }
  }
</script>

{#if hasRpc('plan.enter') && session.kind !== 'subagent'}
  <button class="pick" data-state={state ?? 'off'} class:on={state === 'planning' || state === 'awaiting'} disabled={busy} onclick={toggle} title={tip} aria-label={tip} aria-pressed={state === 'planning' || state === 'awaiting'}>
    <Icon name={state === 'approved' ? 'circle-check' : 'list-tree'} size={13} />
    <span class="np-ellipsis">{label}</span>
  </button>
{/if}

<style>
  .pick {
    display: flex;
    align-items: center;
    gap: 5px;
    max-width: 140px;
    height: 26px;
    padding: 0 8px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
  }
  .pick:hover:not(:disabled) {
    background: var(--bg-3);
    color: var(--fg);
  }
  .pick[data-state='off'] span {
    color: var(--fg-dim);
  }
  .pick.on {
    background: var(--accent-soft);
    color: var(--accent);
  }
  .pick[data-state='awaiting'] {
    cursor: default;
  }
  .pick[data-state='approved'] {
    color: var(--fg-muted);
  }
</style>
