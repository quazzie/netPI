<script>
  /**
   * The lanes the user sets up for agents (settings lanes.<id> = { model, capacity, use, budget: { limitUsd }, cost }):
   * one card per lane with its model, slots, the note on when to use it, a daily cap and the price (inferred from the
   * catalog, overridable), plus what the lane knows (local/cloud, context, today's spend). "Add lane" starts from a model.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import { setSetting } from '../../lib/settings.js';
  import { app } from '../../lib/state/app.svelte.js';
  import { confirmDialog } from '../../lib/state/ui.svelte.js';
  import { tokens, usd } from '../../lib/format.js';

  let { doc, lanes = [] } = $props();

  const RESERVED = new Set(['pools', 'budgets', 'localDefaultCapacity', 'cloudDefaultCapacity']);
  const configured = $derived(
    Object.entries(doc?.lanes ?? {}).filter(([id, v]) => !RESERVED.has(id) && v && typeof v === 'object' && typeof v.model === 'string'),
  );
  const refOf = (m) => m.ref ?? `${m.provider}/${m.id}`;
  const modelOf = (ref) => app.models.find((m) => refOf(m) === ref);
  const infoOf = (id) => lanes.find((p) => p.key === id);

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

  let adding = $state('');
  function slug(ref) {
    return (
      ref
        .split('/')
        .pop()
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '')
        .slice(0, 24) || 'lane'
    );
  }
  async function add() {
    if (!adding) return;
    const base = slug(adding);
    let id = base;
    for (let n = 2; doc?.lanes?.[id] || RESERVED.has(id); n++) id = `${base}-${n}`;
    const m = modelOf(adding);
    await setSetting(`lanes.${id}`, { model: adding, capacity: m?.isLocal ? (m.concurrency ?? 1) : 1, use: '' });
    adding = '';
  }
  async function remove(id) {
    const ok = await confirmDialog({
      title: `Remove the lane "${id}"?`,
      message: 'Agents can no longer choose it; its model gets an automatic lane again.',
      confirmLabel: 'Remove',
      danger: true,
    });
    if (ok) await setSetting(`lanes.${id}`, null);
  }
</script>

<div class="lanes">
  {#if !configured.length}
    <div class="empty np-dim">
      No lanes yet. Add one per model you want agents to use, with a note on when to use it; they pick among these for
      subagents.
    </div>
  {/if}
  {#each configured as [id, lane] (id)}
    {@const p = infoOf(id)}
    {@const m = modelOf(lane.model)}
    {@const override = lane.cost && typeof lane.cost === 'object' ? lane.cost : null}
    <div class="lane" data-lane={id}>
      <div class="head">
        <span class="id np-mono" title="The id agents pass to agent_spawn">{id}</span>
        <select class="np-input model" value={lane.model} onchange={(e) => setSetting(`lanes.${id}.model`, e.currentTarget.value)} aria-label="Model">
          {#if !m}<option value={lane.model}>{lane.model} (not listed)</option>{/if}
          {#each app.models as x (refOf(x))}<option value={refOf(x)}>{refOf(x)}</option>{/each}
        </select>
        <label class="slots" title="Parallel slots">
          <input
            class="np-input"
            value={lane.capacity ?? ''}
            placeholder={String(p?.capacity ?? 1)}
            inputmode="numeric"
            onchange={(e) => setNum(`lanes.${id}.capacity`, e.currentTarget.value, { int: true, min: 1 })}
            aria-label="Slots"
          />
          <span class="np-dim">slots</span>
        </label>
        <button class="icon" title="Remove the lane" onclick={() => remove(id)}><Icon name="trash" size={13} /></button>
      </div>
      <textarea
        class="np-input use"
        rows="2"
        value={lane.use ?? ''}
        placeholder="When should agents use it? e.g. Free: research, reading code, first drafts."
        onchange={(e) => setSetting(`lanes.${id}.use`, e.currentTarget.value.trim() || null)}
        aria-label="When to use this lane"
      ></textarea>
      <div class="facts np-dim">
        {#if m?.isLocal}<span>local</span>{:else if m}<span>cloud</span>{/if}
        {#if p?.free}<span class="free">free</span>{:else if p?.priceInput != null}<span>{usd(p.priceInput)} / {usd(p.priceOutput)} per Mtok</span>{:else}<span class="warn">price unknown</span>{/if}
        {#if m?.contextWindow}<span>{tokens(m.contextWindow)} context</span>{/if}
        {#if m?.inputModalities?.includes('image')}<span>images</span>{/if}
        {#if p && !p.free}<span>{usd(p.spentTodayUsd ?? 0)} today</span>{/if}
        {#if p}<span>{p.busy}/{p.capacity} busy</span>{/if}
      </div>
      {#if !m?.isLocal}
        <div class="money">
          <label
            >Price in
            <input
              class="np-input"
              value={override?.input ?? ''}
              placeholder={p?.priceSource === 'settings' || p?.priceInput == null ? '?' : String(p.priceInput)}
              inputmode="decimal"
              onchange={(e) => setNum(`lanes.${id}.cost.input`, e.currentTarget.value)}
            /></label
          >
          <label
            >out
            <input
              class="np-input"
              value={override?.output ?? ''}
              placeholder={p?.priceSource === 'settings' || p?.priceOutput == null ? '?' : String(p.priceOutput)}
              inputmode="decimal"
              onchange={(e) => setNum(`lanes.${id}.cost.output`, e.currentTarget.value)}
            /> <span class="np-dim">$ per Mtok</span></label
          >
          <label
            >Daily cap
            <input
              class="np-input"
              value={lane.budget?.limitUsd ?? ''}
              placeholder="none"
              inputmode="decimal"
              onchange={(e) => setNum(`lanes.${id}.budget.limitUsd`, e.currentTarget.value)}
            /> <span class="np-dim">$</span></label
          >
        </div>
      {/if}
    </div>
  {/each}
  <div class="add">
    <select class="np-input" bind:value={adding} aria-label="Model for a new lane">
      <option value="">Add a lane for a model…</option>
      {#each app.models as x (refOf(x))}<option value={refOf(x)}>{refOf(x)}</option>{/each}
    </select>
    <button class="np-btn np-btn-sm" disabled={!adding} onclick={add}><Icon name="plus" size={12} /> Add lane</button>
  </div>
</div>

<style>
  .lanes {
    display: flex;
    flex-direction: column;
    gap: 10px;
    margin-bottom: 12px;
  }
  .empty {
    font-size: var(--fs-sm);
  }
  .lane {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 10px 12px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-1);
  }
  .head {
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }
  .id {
    flex: none;
    font-weight: 600;
    color: var(--fg);
  }
  .model {
    flex: 1;
    min-width: 0;
  }
  .slots {
    display: flex;
    align-items: center;
    gap: 4px;
    flex: none;
    font-size: var(--fs-sm);
  }
  .slots input {
    width: 44px;
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
  .free {
    color: var(--ok);
  }
  .warn {
    color: var(--warn);
  }
  .money {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px 14px;
    font-size: var(--fs-sm);
    color: var(--fg-muted);
  }
  .money input {
    width: 70px;
    text-align: right;
  }
  .icon {
    display: grid;
    place-items: center;
    flex: none;
    width: 26px;
    height: 26px;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--fg-dim);
  }
  .icon:hover {
    background: var(--bg-2);
    color: var(--err);
  }
  .add {
    display: flex;
    gap: 8px;
  }
  .add select {
    flex: 1;
    min-width: 0;
  }
</style>
