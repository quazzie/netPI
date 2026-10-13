<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { proposalForCall, closedProposalForCall, decideProposal } from '../../lib/state/schedules.svelte.js';
  import { toast } from '../../lib/state/ui.svelte.js';
  import { parseArgs, cadenceWords } from '../../lib/tools.js';

  /**
   * A schedule the agent proposed (a schedules add, plugins/NetPI.Schedules): the proposal, and Allow (it runs from
   * then on) / Discard (it is deleted) while it waits; afterwards one line with what was chosen. A subagent's add has
   * no card (nobody watches its chat): its result says the proposal stays in the Schedules tab, and this card shows
   * that as its one line.
   */
  let { item, sessionId } = $props();

  const CLOCK =
    "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.6' stroke-linecap='round'><circle cx='12' cy='12' r='9'/><path d='M12 7v5l3 2'/></svg>";

  const callId = $derived(item.call.id);
  // The call id is the model's, and two chats can have the same one: the proposal's own id (with this chat) says which is which.
  const pending = $derived(proposalForCall(sessionId, callId));
  const closed = $derived(closedProposalForCall(sessionId, callId));
  const r = $derived(item.result);
  const d = $derived(r && !r.isError ? r.details : null);
  const a = $derived(parseArgs(item.call));
  const schedule = $derived(pending?.schedule ?? d?.schedule ?? null);
  const name = $derived(schedule?.name ?? a?.name ?? firstLine(a?.prompt) ?? 'New schedule');
  const when = $derived(schedule?.when ?? cadenceWords(a?.cadence));
  const prompt = $derived(schedule?.prompt ?? a?.prompt ?? '');
  const project = $derived(d?.project ?? schedule?.projectId ?? a?.projectId ?? '');
  const agent = $derived(schedule?.agent ?? a?.agent ?? '');
  // preparing | waiting | allowed | discarded | steered | withdrawn | cancelled | proposal (no decision: the old way,
  // the Schedules tab) | unanswered (no result: NetPI restarted) | error.
  const status = $derived(
    item.preparing ? 'preparing' : pending ? 'waiting' : (d?.status ?? closed?.status ?? (r?.isError ? 'error' : r ? 'proposal' : 'unanswered')),
  );
  const label = $derived(
    {
      allowed: 'allowed',
      discarded: 'discarded',
      steered: 'not decided: you wrote a message instead',
      withdrawn: 'not decided',
      cancelled: 'not decided: the run stopped',
      proposal: 'proposal in the Schedules tab',
      unanswered: 'not decided',
      error: 'not added',
    }[status] ?? status,
  );
  let busy = $state(false);

  async function decide(decision) {
    if (busy || !pending) return;
    busy = true;
    try {
      await decideProposal(pending.id, decision);
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      busy = false;
    }
  }

  function firstLine(s) {
    return String(s ?? '').trim().split('\n')[0];
  }
</script>

{#if status === 'preparing'}
  <div class="line">
    <span class="ic"><span class="np-spinner"></span></span>
    <span class="np-line np-baseline np-grow tx"><span class="lbl">Schedule</span><span class="dim">preparing…</span></span>
  </div>
{:else if status === 'waiting'}
  <div class="card" role="group" aria-label="A new schedule the agent proposed">
    <div class="who"><Icon name={CLOCK} size={13} /><span>A new schedule</span></div>
    <div class="name">
      {name}
      {#if when}<span class="when np-mono">{when}</span>{/if}
    </div>
    {#if prompt}<div class="prompt">{prompt}</div>{/if}
    <div class="meta">
      {#if project}<span>project <span class="np-mono">{project}</span></span>{/if}
      {#if agent}<span>agent <span class="np-mono">{agent}</span></span>{/if}
    </div>
    <div class="foot">
      <span class="hint">Allowed, it runs on its own with every tool</span>
      <button class="np-btn np-btn-sm np-btn-primary" disabled={busy} onclick={() => decide('allow')}>Allow</button>
      <button class="np-btn np-btn-sm" disabled={busy} onclick={() => decide('discard')}>Discard</button>
    </div>
  </div>
{:else}
  <div class="line">
    <span class="ic"><Icon name={CLOCK} size={14} /></span>
    <span class="np-line np-baseline np-grow tx">
      <span class="lbl">Schedule</span>
      <span class="qt np-grow">{name}{when ? ` · ${when}` : ''}</span>
      <span class="arrow">→</span>
      <span class="a" class:none={status !== 'allowed'}>{label}</span>
    </span>
  </div>
{/if}

<style>
  .line {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    height: 28px;
    padding: 0 8px 0 4px;
    margin-left: -4px;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    min-width: 0;
  }
  .ic {
    display: grid;
    place-items: center;
    width: 16px;
    flex: none;
    color: var(--accent);
  }
  .tx {
    height: 1lh;
  }
  .lbl {
    color: var(--fg);
    font-weight: 500;
  }
  .tx .qt {
    flex: 1 1 0;
    min-width: 3em;
    color: var(--fg-muted);
  }
  .arrow,
  .dim {
    color: var(--fg-dim);
  }
  .a {
    flex: 0 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--fg);
  }
  .a.none {
    color: var(--fg-dim);
  }
  /* waiting: the proposal and its decision, open */
  .card {
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 10px 12px 12px;
    border: 1px solid var(--accent-line);
    border-radius: 8px;
    background: var(--bg-1);
  }
  .who {
    display: flex;
    align-items: center;
    gap: 6px;
    color: var(--accent);
    font-size: var(--fs-xs);
    font-weight: 600;
  }
  .name {
    color: var(--fg);
    font-weight: 500;
    overflow-wrap: anywhere;
  }
  .when {
    margin-left: 8px;
    font-size: var(--fs-sm);
    color: var(--fg-muted);
  }
  .prompt {
    margin: 0;
    padding: 6px 10px;
    border-radius: 6px;
    background: var(--bg-2);
    color: var(--fg);
    font-size: var(--fs-sm);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    max-height: 9em;
    overflow: auto;
  }
  .meta {
    display: flex;
    flex-wrap: wrap;
    gap: 6px 14px;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
  }
  .foot {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
  }
  .hint {
    flex: 1;
    min-width: 0;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
</style>
