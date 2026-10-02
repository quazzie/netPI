<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { offerForCall, answerOffer } from '../../lib/state/plans.svelte.js';
  import { toast } from '../../lib/state/ui.svelte.js';
  import { parseArgs } from '../../lib/tools.js';

  /**
   * The agent's offer of plan mode (plan_enter, plugins/NetPI.Plan) for a large or unclear change: the reason, and Enter plan
   * mode / Not now while it waits; afterwards one line with what was chosen.
   */
  let { item, sessionId } = $props();

  const callId = $derived(item.call.id);
  const offer = $derived(offerForCall(sessionId, callId));
  const r = $derived(item.result);
  const reason = $derived(offer?.reason ?? r?.details?.reason ?? parseArgs(item.call)?.reason ?? '');
  const status = $derived(
    item.preparing ? 'preparing' : offer?.status === 'waiting' ? 'waiting' : (r?.details?.status ?? offer?.status ?? (r?.isError ? 'error' : r ? 'declined' : 'unanswered')),
  );
  const label = $derived(
    { entered: 'plan mode on', declined: 'not now', steered: 'not answered: you wrote a message instead', withdrawn: 'not answered', cancelled: 'not answered: the run stopped', error: 'not offered', unanswered: 'not answered' }[status] ?? status,
  );
  let busy = $state(false);

  async function answer(enter) {
    if (busy || !offer) return;
    busy = true;
    try {
      await answerOffer(offer.id, enter);
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      busy = false;
    }
  }
</script>

{#if status === 'preparing'}
  <div class="line">
    <span class="ic"><span class="np-spinner"></span></span>
    <span class="np-line np-baseline np-grow tx"><span class="lbl">Plan mode</span><span class="dim">preparing…</span></span>
  </div>
{:else if status === 'waiting'}
  <div class="card" role="group" aria-label="The agent suggests plan mode">
    <div class="who"><Icon name="list-tree" size={13} /><span>Plan first?</span></div>
    <div class="why">{reason || 'The agent suggests planning before it changes anything: it explores read-only and brings you a plan to approve.'}</div>
    <div class="foot">
      <button class="np-btn np-btn-sm np-btn-primary" disabled={busy} onclick={() => answer(true)}>Enter plan mode</button>
      <button class="np-btn np-btn-sm" disabled={busy} onclick={() => answer(false)}>Not now</button>
    </div>
  </div>
{:else}
  <div class="line">
    <span class="ic"><Icon name="list-tree" size={14} /></span>
    <span class="np-line np-baseline np-grow tx">
      <span class="lbl">Plan mode</span>
      <span class="qt np-grow">{reason}</span>
      <span class="arrow">→</span>
      <span class="a" class:none={status !== 'entered'}>{label}</span>
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
  .why {
    color: var(--fg);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .foot {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }
</style>
