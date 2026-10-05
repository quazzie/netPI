<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import { plans, planForCall, answerPlan } from '../../lib/state/plans.svelte.js';
  import { replaceTab, openSession } from '../../lib/state/app.svelte.js';
  import { toast, prefs } from '../../lib/state/ui.svelte.js';
  import { parseArgs } from '../../lib/tools.js';
  import TurnLine from './TurnLine.svelte';

  /**
   * The plan the agent submitted (plan_submit, plugins/NetPI.Plan) as an item of its own in the chat. While the user has not
   * decided (the plugin holds the plan as awaiting) the card is open with the buttons: Approve, Approve in a new chat
   * (the new chat takes this one's place), Revise (the feedback goes back to the agent), and Save as idea / Save as file /
   * Cancel. Once decided it is one line (plan → outcome) that opens to the plan itself.
   */
  let { item, sessionId } = $props();

  const asList = (v) => (Array.isArray(v) ? v : typeof v === 'string' && v.trim() ? [v] : []);
  // the arguments as the model wrote them, while the plugin has not stored the plan yet (preparing) or lost it (NetPI restarted)
  function fromArgs(a) {
    return {
      title: String(a?.title ?? 'Plan'),
      summary: String(a?.summary ?? ''),
      steps: asList(a?.steps).map((s) => (typeof s === 'string' ? { text: s } : { text: String(s?.text ?? s?.step ?? ''), detail: s?.detail ?? s?.description })),
      files: asList(a?.files).map((f) => (typeof f === 'string' ? { path: f } : { path: String(f?.path ?? f?.file ?? ''), note: f?.note })),
      risks: asList(a?.risks).map(String),
      tests: asList(a?.tests).map(String),
      openQuestions: asList(a?.openQuestions ?? a?.questions).map(String),
    };
  }

  const callId = $derived(item.call.id);
  // the plugin knows the latest submission's call id: an older card (a revision since) shows what its own result said
  const live = $derived(planForCall(sessionId, callId));
  const r = $derived(item.result);
  const d = $derived(r && !r.isError && r.details?.kind === 'plan' ? r.details : null);
  const plan = $derived(live?.plan ?? d?.plan ?? fromArgs(parseArgs(item.call)));
  const revision = $derived(live?.revision ?? d?.revision ?? 1);
  const failed = (res) => (/^Skipped:/.test(res.content ?? '') ? 'steered' : /^(Aborted:|Not executed)/.test(res.content ?? '') ? 'cancelled' : 'error');
  // preparing | waiting | submitted (no decision yet, no buttons: the plugin has not said it waits) | approved | revised |
  // cancelled | steered | withdrawn | replaced | error | unanswered
  const status = $derived(
    item.preparing ? 'preparing' : live?.status === 'awaiting' ? 'waiting' : (d?.status ?? (r?.isError ? failed(r) : r ? 'unanswered' : 'submitted')),
  );
  const newChatId = $derived(d?.newSessionId ?? null);

  let busy = $state(null);
  let revising = $state(false);
  let feedback = $state('');
  let open = $state(false);

  async function act(decision, extra = {}) {
    if (busy || !live) return;
    busy = decision + (extra.newChat ? '-new' : '');
    try {
      const res = await answerPlan(live.id, decision, extra);
      // The answer says what the plan is now ({ planId, status, ideaId? … }): the card shows it at once instead of waiting
      // for plan.changed → plan.list, which still follows and confirms it (the plugin's record stays canonical).
      const known = plans.byId.get(live.id);
      if (known && res?.status) plans.byId.set(live.id, { ...known, status: res.status, ...(res.ideaId ? { ideaId: res.ideaId } : {}) });
      if (decision === 'save') toast(res?.ideaId ? `Saved as idea ${res.ideaId}` : 'Not saved: the ideas plugin is not running', res?.ideaId ? 'info' : 'error');
      else if (decision === 'file') toast(`Saved ${res?.path}`);
      else if (decision === 'revise') {
        revising = false;
        feedback = '';
      } else if (decision === 'approve' && res?.newSessionId) await replaceTab(sessionId, res.newSessionId);
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      busy = null;
    }
  }

  const endsTurn = $derived(item.msg?.stopReason === 'tool_use' && item.msg.parts.findLast((p) => p.type === 'tool_call')?.id === callId);
  const outcome = $derived.by(() => {
    switch (status) {
      case 'approved':
        return newChatId ? 'approved · carried out in a new chat' : 'approved';
      case 'revised':
        return `changes asked${d?.feedback ? `: ${d.feedback}` : ''}`;
      case 'cancelled':
        return 'cancelled';
      case 'steered':
        return 'not decided: you wrote a message instead';
      case 'withdrawn':
        return 'not decided: the plan tool restarted';
      case 'replaced':
        return 'submitted again';
      case 'error':
        return `not submitted: ${(r?.content ?? '').split('\n')[0]}`;
      case 'submitted':
        return 'waiting…';
      default:
        return 'no decision';
    }
  });
</script>

{#snippet body(p)}
  {#if p.summary}<div class="summary">{p.summary}</div>{/if}
  <ol class="steps">
    {#each p.steps ?? [] as s, i (i)}
      <li><span class="stext">{s.text}</span>{#if s.detail}<span class="sdetail">{s.detail}</span>{/if}</li>
    {/each}
  </ol>
  {#if p.files?.length}
    <div class="sect">Files</div>
    <ul class="files">
      {#each p.files as f, i (i)}
        <li><span class="np-mono path">{f.path}</span>{#if f.note}<span class="note">{f.note}</span>{/if}</li>
      {/each}
    </ul>
  {/if}
  {#each [['Risks', p.risks], ['Tests', p.tests], ['Open questions', p.openQuestions]] as [label, list] (label)}
    {#if list?.length}
      <div class="sect">{label}</div>
      <ul class="bullets">
        {#each list as x, i (i)}<li>{x}</li>{/each}
      </ul>
    {/if}
  {/each}
{/snippet}

{#if status === 'preparing'}
  <div class="line">
    <span class="ic"><span class="np-spinner"></span></span>
    <span class="np-line np-baseline np-grow tx"><span class="lbl">Plan</span><span class="dim">writing…</span></span>
  </div>
{:else if status === 'waiting' || status === 'submitted'}
  <div class="card" role="group" aria-label="The agent's plan">
    <div class="who">
      <Icon name="list-tree" size={13} /><span>Plan for your decision</span>
      {#if revision > 1}<span class="rev">revision {revision}</span>{/if}
    </div>
    <div class="title">{plan.title}</div>
    <div class="plan np-scroll">{@render body(plan)}</div>
    {#if status === 'waiting'}
      {#if revising}
        <div class="revise">
          <textarea bind:value={feedback} rows="3" placeholder="What should change?" aria-label="What should change"></textarea>
          <div class="foot">
            <button class="np-btn np-btn-sm np-btn-primary" disabled={!feedback.trim() || !!busy} onclick={() => act('revise', { feedback })}>Send changes</button>
            <button class="np-btn np-btn-sm" onclick={() => (revising = false)}>Back</button>
          </div>
        </div>
      {:else}
        <div class="foot">
          <button class="np-btn np-btn-sm np-btn-primary" disabled={!!busy} onclick={() => act('approve')} title="Approve: save it as an idea, end plan mode and let the agent carry it out here"
            >Approve</button
          >
          <button class="np-btn np-btn-sm" disabled={!!busy} onclick={() => act('approve', { newChat: true })} title="Approve in a new chat: it starts from the plan and takes this chat's place; this chat is archived"
            >Approve in new chat</button
          >
          <button class="np-btn np-btn-sm" disabled={!!busy} onclick={() => (revising = true)}>Revise…</button>
        </div>
        <div class="foot more">
          <button class="link" disabled={!!busy} onclick={() => act('save')}>Save as idea</button>
          <button class="link" disabled={!!busy} onclick={() => act('file')} title="Write docs/plans/<date>-<title>.md in the workspace">Save as file</button>
          <button class="link danger" disabled={!!busy} onclick={() => act('cancel')} title="Drop the plan and leave plan mode">Cancel</button>
        </div>
      {/if}
    {:else}
      <div class="hint">waiting for the plan tool…</div>
    {/if}
  </div>
{:else}
  <button class="line done" class:open aria-expanded={open} onclick={() => (open = !open)}>
    <span class="ic"><Icon name="list-tree" size={14} /></span>
    <span class="np-line np-baseline np-grow tx">
      <span class="lbl">Plan</span>
      <span class="qt np-grow">{plan.title}</span>
      <span class="arrow">→</span>
      <span class="a" class:none={status !== 'approved'}>{outcome}</span>
    </span>
    <span class="chev" class:open><Icon name="chevron-right" size={12} /></span>
  </button>
  {#if open}
    <div class="opened">
      {@render body(plan)}
      {#if d?.ideaId}<div class="meta">Saved as idea <span class="np-mono">{d.ideaId}</span></div>{/if}
      {#if newChatId}<button class="np-btn np-btn-sm" onclick={() => openSession(newChatId)}>Open the new chat</button>{/if}
    </div>
  {/if}
  {#if prefs.turnDetails && endsTurn}<TurnLine msg={item.msg} />{/if}
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
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    font: inherit;
    font-size: var(--fs-sm);
    text-align: left;
    min-width: 0;
  }
  .done {
    cursor: pointer;
  }
  .done:hover,
  .done.open {
    background: var(--bg-2);
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
  .chev {
    display: inline-grid;
    flex: none;
    color: var(--fg-dim);
    transition: transform var(--t-fast);
  }
  .chev.open {
    transform: rotate(90deg);
  }
  .opened {
    display: flex;
    flex-direction: column;
    gap: 4px;
    margin: 2px 0 6px 20px;
    padding: 8px 10px;
    border-radius: 6px;
    background: var(--bg-1);
    font-size: var(--fs-sm);
  }
  .meta {
    color: var(--fg-muted);
    font-size: var(--fs-xs);
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
  .rev {
    margin-left: auto;
    color: var(--fg-dim);
    font-weight: 400;
  }
  .title {
    color: var(--fg);
    font-weight: 600;
    overflow-wrap: anywhere;
  }
  .plan {
    display: flex;
    flex-direction: column;
    gap: 4px;
    max-height: 420px;
    overflow-y: auto;
    font-size: var(--fs-sm);
  }
  .summary {
    color: var(--fg-muted);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    margin-bottom: 4px;
  }
  .steps {
    margin: 0;
    padding-left: 20px;
    display: flex;
    flex-direction: column;
    gap: 3px;
  }
  .steps li {
    overflow-wrap: anywhere;
  }
  .stext {
    color: var(--fg);
  }
  .sdetail,
  .note {
    display: block;
    color: var(--fg-muted);
    white-space: pre-wrap;
  }
  .sect {
    margin-top: 6px;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .files,
  .bullets {
    margin: 0;
    padding-left: 18px;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .path {
    overflow-wrap: anywhere;
  }
  .foot {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px;
  }
  .foot.more {
    gap: 12px;
  }
  .link {
    padding: 0;
    border: 0;
    background: none;
    color: var(--fg-muted);
    font: inherit;
    font-size: var(--fs-xs);
    cursor: pointer;
  }
  .link:hover:not(:disabled) {
    color: var(--fg);
    text-decoration: underline;
  }
  .link.danger:hover:not(:disabled) {
    color: var(--danger, var(--fg));
  }
  .link:disabled {
    opacity: 0.5;
    cursor: default;
  }
  .revise {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .revise textarea {
    width: 100%;
    resize: vertical;
    padding: 6px 8px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg);
    color: var(--fg);
    font: inherit;
  }
  .hint {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
</style>
