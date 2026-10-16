<script>
  /**
   * One agent (settings agents.<id> = { model, instances, use, disabled, cost, budget: { limitUsd } }) in its own dialog:
   * its name, on/off, the model (searchable), instances, the note on when to use it, the price (inferred, overridable) and
   * a daily cap, plus its state (ready, busy, not loaded) and what the model offers. Every field saves on its own; a new
   * name moves the agent (chats on the old name take another agent on its model).
   */
  import Modal from '../../lib/kit/Modal.svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import ModelSelect from '../ModelSelect.svelte';
  import { setSetting } from '../../lib/settings.js';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { bus } from '../../lib/bus.js';
  import { app } from '../../lib/state/app.svelte.js';
  import { confirmDialog, toast } from '../../lib/state/ui.svelte.js';
  import { agentState, cleanAgentId } from '../../lib/agents.js';
  import { tokens, usd } from '../../lib/format.js';

  let { id, agent, info = null, localSlots = 2, taken = [], onrename, onclose } = $props();

  const m = $derived(agent?.model ? app.modelsByRef.get(agent.model) : null);
  const override = $derived(agent?.cost && typeof agent.cost === 'object' ? agent.cost : null);
  const st = $derived(agentState(info));
  const modelCapacity = $derived(info?.resourceCapacity ?? (m?.concurrency > 0 ? m.concurrency : Math.max(1, localSlots)));
  const defaultInstances = $derived(m?.isLocal ? modelCapacity : 1);
  // agents on one local model share its slots: warn when their instances add up to more than it serves
  const sharing = $derived.by(() => {
    if (!m?.isLocal) return null;
    const on = app.slots.filter((p) => p.configured && p.model === agent?.model && !p.disabled);
    const total = on.reduce((n, p) => n + (p.key === id ? (agent?.instances ?? p.capacity) : p.capacity), 0);
    return total > modelCapacity ? { total, slots: modelCapacity, names: on.map((p) => p.key) } : null;
  });

  // what this agent has done: this budget period and all together (usage.history, per agent)
  let usage = $state.raw(null);
  async function loadUsage() {
    try {
      usage = await rpc('usage.history', { days: 1 });
    } catch {
      usage = null;
    }
  }
  $effect(() => {
    loadUsage();
    return bus.on('usage.changed', loadUsage);
  });
  const figures = (r) => (r ? { calls: r.calls ?? 0, input: r.inputTokens ?? 0, output: r.outputTokens ?? 0, cost: r.costUsd ?? 0 } : null);
  const period = $derived(figures((usage?.agents ?? []).find((r) => r.agent === id)));
  const total = $derived(figures((usage?.allTimeAgents ?? []).find((r) => r.agent === id)));

  const num = (text) => {
    const t = String(text ?? '').trim();
    if (!t) return null;
    const n = Number(t);
    return Number.isFinite(n) && n >= 0 ? n : undefined;
  };
  async function setNum(key, text, { int = false, min = 0 } = {}) {
    const n = num(text);
    if (n === undefined || (n != null && (n < min || (int && !Number.isInteger(n))))) return;
    await setSetting(key, n);
  }

  async function rename(text, input) {
    const next = cleanAgentId(text);
    if (!next || next === id) {
      input.value = id;
      return;
    }
    if (taken.includes(next)) {
      toast(`There is an agent “${next}” already`, 'warn');
      input.value = id;
      return;
    }
    if (!(await setSetting(`agents.${next}`, $state.snapshot(agent)))) return;
    await setSetting(`agents.${id}`, null);
    onrename?.(next);
  }

  async function remove() {
    // The dialog's own id (captured now: onclose() below nulls openId, which flows back into this prop,
    // and the remove must still target the agent it was opened for)
    const idToRemove = id;
    const ok = await confirmDialog({
      title: `Remove the agent "${idToRemove}"?`,
      message: 'Chats on it take another agent on its model; with none, they stop with a notice until you choose one.',
      confirmLabel: 'Remove',
      danger: true,
    });
    if (!ok) return;
    onclose?.();
    await setSetting(`agents.${idToRemove}`, null);
  }
</script>

{#snippet foot()}
  <button class="np-btn np-btn-danger remove" data-testid="remove-agent" onclick={remove}><Icon name="trash" size={13} /> Remove agent</button>
  <span class="spacer"></span>
  <button class="np-btn np-btn-primary" onclick={() => onclose?.()}>Done</button>
{/snippet}

<Modal title="Agent {id}" width={560} {onclose} footer={foot} class="agent-dialog">
  {#if agent}
    <div class="grid">
      <span class="lbl">Name</span>
      <input
        class="np-input name np-mono"
        value={id}
        aria-label="Name"
        spellcheck="false"
        onchange={(e) => rename(e.currentTarget.value, e.currentTarget)}
      />

      <span class="lbl">Active</span>
      <div class="row">
        <label class="switch">
          <input type="checkbox" class="np-switch" checked={!agent.disabled} aria-label="Agent on" onchange={(e) => setSetting(`agents.${id}.disabled`, e.currentTarget.checked ? null : true)} />
          {agent.disabled ? 'Off' : 'On'}
        </label>
        {#if info}<span class="state"><span class="np-dot" data-status={st.dot}></span> {st.text}</span>{/if}
      </div>

      <span class="lbl">Model</span>
      <ModelSelect value={agent.model} onchange={(ref) => ref && setSetting(`agents.${id}.model`, ref)} />

      <span class="lbl">Instances</span>
      <div class="row">
        <input
          class="np-input count"
          value={agent.instances ?? ''}
          placeholder={String(defaultInstances)}
          inputmode="numeric"
          aria-label="Instances"
          onchange={(e) => setNum(`agents.${id}.instances`, e.currentTarget.value, { int: true, min: 1 })}
        />
        <span class="np-dim">runs at once{m?.isLocal ? ` (shared model capacity: ${modelCapacity})` : ''}</span>
      </div>
      {#if sharing}
        <span></span>
        <div class="warn-line">
          <Icon name="alert" size={12} /> The agents on {m.displayName || m.id} ({sharing.names.join(', ')}) have {sharing.total} instances;
          the model serves {sharing.slots} at once, so the others wait.
        </div>
      {/if}

      <span class="lbl top">When to use it</span>
      <textarea
        class="np-input use"
        rows="3"
        value={agent.use ?? ''}
        placeholder="e.g. Free: research, reading code, first drafts. / Costs money: only for hard problems."
        aria-label="When to use this agent"
        onchange={(e) => setSetting(`agents.${id}.use`, e.currentTarget.value.trim() || null)}
      ></textarea>

      <span class="lbl">Facts</span>
      <div class="facts np-dim">
        {#if m?.isLocal}<span>local</span>{:else if m}<span>cloud</span>{:else}<span class="warn">not in the model list</span>{/if}
        {#if info?.free}<span class="free">free</span>{:else if info?.priceInput != null}<span>{usd(info.priceInput)} / {usd(info.priceOutput)} per Mtok</span>{:else}<span class="warn">price unknown</span>{/if}
        {#if m?.contextWindow}<span>{tokens(m.contextWindow)} context</span>{/if}
        {#if m?.inputModalities?.includes('image')}<span>images</span>{/if}
        {#if info && !info.free}<span>{usd(info.spentTodayUsd ?? 0)} today</span>{/if}
      </div>

      <span class="lbl">Usage</span>
      <div class="usage np-dim">
        {#each [['This period', period, usage?.period], ['In total', total, usage?.allTime?.since]] as [label, u, since] (label)}
          <span class="when">{label}</span>
          <span class="figs">
            {#if u}
              <span>{u.calls.toLocaleString()} calls</span>
              <span>{tokens(u.input) || 0} in</span>
              <span>{tokens(u.output) || 0} out</span>
              {#if u.cost > 0}<span>{usd(u.cost)}</span>{/if}
              {#if since}<span>since {since}</span>{/if}
            {:else}
              <span>no calls</span>
            {/if}
          </span>
        {/each}
      </div>

      {#if !m?.isLocal}
        <span class="lbl">Price</span>
        <div class="row">
          <label
            >in <input
              class="np-input money"
              value={override?.input ?? ''}
              placeholder={info?.priceSource === 'settings' || info?.priceInput == null ? '?' : String(info.priceInput)}
              inputmode="decimal"
              aria-label="Input price"
              onchange={(e) => setNum(`agents.${id}.cost.input`, e.currentTarget.value)}
            /></label
          >
          <label
            >out <input
              class="np-input money"
              value={override?.output ?? ''}
              placeholder={info?.priceSource === 'settings' || info?.priceOutput == null ? '?' : String(info.priceOutput)}
              inputmode="decimal"
              aria-label="Output price"
              onchange={(e) => setNum(`agents.${id}.cost.output`, e.currentTarget.value)}
            /></label
          >
          <span class="np-dim">$ per Mtok (empty: the catalog's)</span>
        </div>

        <span class="lbl">Daily cap</span>
        <div class="row">
          <input
            class="np-input money"
            value={agent.budget?.limitUsd ?? ''}
            placeholder="none"
            inputmode="decimal"
            aria-label="Daily cap"
            onchange={(e) => setNum(`agents.${id}.budget.limitUsd`, e.currentTarget.value)}
          />
          <span class="np-dim">$ a day for this agent, besides the budget</span>
        </div>
      {/if}
    </div>
  {:else}
    <div class="np-dim">This agent was removed.</div>
  {/if}
</Modal>

<style>
  .grid {
    display: grid;
    grid-template-columns: 110px minmax(0, 1fr);
    gap: 10px 12px;
    align-items: center;
  }
  .lbl {
    color: var(--fg-muted);
    font-size: var(--fs-sm);
  }
  .lbl.top {
    align-self: start;
    padding-top: 6px;
  }
  .row {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px 12px;
    font-size: var(--fs-sm);
  }
  .name {
    max-width: 240px;
  }
  .switch {
    display: inline-flex;
    align-items: center;
    gap: 6px;
  }
  .state {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
  }
  .count {
    width: 64px;
    text-align: right;
  }
  .money {
    width: 76px;
    text-align: right;
  }
  .use {
    width: 100%;
    height: auto;
    padding: 6px 8px;
    line-height: 1.45;
    resize: vertical;
  }
  .facts {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 12px;
    font-size: var(--fs-xs);
  }
  .usage {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    gap: 4px 12px;
    font-size: var(--fs-xs);
  }
  .figs {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 12px;
  }
  .warn-line {
    display: flex;
    gap: 6px;
    align-items: baseline;
    color: var(--warn);
    font-size: var(--fs-xs);
    line-height: 1.45;
  }
  .free {
    color: var(--ok);
  }
  .warn {
    color: var(--warn);
  }
  .spacer {
    flex: 1;
  }
  .remove {
    margin-right: auto;
  }
  @media (max-width: 520px) {
    .grid {
      grid-template-columns: minmax(0, 1fr);
    }
  }
</style>
