<script>
  /**
   * One lane (settings lanes.<id> = { model, capacity, use, cost, budget: { limitUsd } }) in its own dialog: the model
   * (searchable), slots, the note on when to use it, the price (inferred, overridable) and a daily cap, plus what the
   * lane knows (local/cloud, context, today's spend). Every field saves on its own.
   */
  import Modal from './Modal.svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import ModelSelect from '../ModelSelect.svelte';
  import { setSetting } from '../../lib/settings.js';
  import { app } from '../../lib/state/app.svelte.js';
  import { confirmDialog } from '../../lib/state/ui.svelte.js';
  import { tokens, usd } from '../../lib/format.js';

  let { id, lane, info = null, onclose } = $props();

  const m = $derived(lane?.model ? app.modelsByRef.get(lane.model) : null);
  const override = $derived(lane?.cost && typeof lane.cost === 'object' ? lane.cost : null);

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

  async function remove() {
    const ok = await confirmDialog({
      title: `Remove the lane "${id}"?`,
      message: 'Agents can no longer choose it; its model gets an automatic lane again.',
      confirmLabel: 'Remove',
      danger: true,
    });
    if (!ok) return;
    onclose?.();
    await setSetting(`lanes.${id}`, null);
  }
</script>

{#snippet foot()}
  <button class="np-btn np-btn-danger remove" onclick={remove}><Icon name="trash" size={13} /> Remove lane</button>
  <span class="spacer"></span>
  <button class="np-btn np-btn-primary" onclick={() => onclose?.()}>Done</button>
{/snippet}

<Modal title="Lane {id}" width={560} {onclose} footer={foot} class="lane-dialog">
  {#if lane}
    <div class="grid">
      <span class="lbl">Model</span>
      <ModelSelect value={lane.model} onchange={(ref) => ref && setSetting(`lanes.${id}.model`, ref)} />

      <span class="lbl">Slots</span>
      <div class="row">
        <input
          class="np-input slots"
          value={lane.capacity ?? ''}
          placeholder={String(info?.capacity ?? 1)}
          inputmode="numeric"
          aria-label="Slots"
          onchange={(e) => setNum(`lanes.${id}.capacity`, e.currentTarget.value, { int: true, min: 1 })}
        />
        <span class="np-dim">parallel model calls</span>
      </div>

      <span class="lbl top">When to use it</span>
      <textarea
        class="np-input use"
        rows="3"
        value={lane.use ?? ''}
        placeholder="e.g. Free: research, reading code, first drafts. / Costs money: only for hard problems."
        aria-label="When to use this lane"
        onchange={(e) => setSetting(`lanes.${id}.use`, e.currentTarget.value.trim() || null)}
      ></textarea>

      <span class="lbl">Facts</span>
      <div class="facts np-dim">
        {#if m?.isLocal}<span>local</span>{:else if m}<span>cloud</span>{/if}
        {#if info?.free}<span class="free">free</span>{:else if info?.priceInput != null}<span>{usd(info.priceInput)} / {usd(info.priceOutput)} per Mtok</span>{:else}<span class="warn">price unknown</span>{/if}
        {#if m?.contextWindow}<span>{tokens(m.contextWindow)} context</span>{/if}
        {#if m?.inputModalities?.includes('image')}<span>images</span>{/if}
        {#if info && !info.free}<span>{usd(info.spentTodayUsd ?? 0)} today</span>{/if}
        {#if info}<span>{info.busy}/{info.capacity} busy</span>{/if}
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
              onchange={(e) => setNum(`lanes.${id}.cost.input`, e.currentTarget.value)}
            /></label
          >
          <label
            >out <input
              class="np-input money"
              value={override?.output ?? ''}
              placeholder={info?.priceSource === 'settings' || info?.priceOutput == null ? '?' : String(info.priceOutput)}
              inputmode="decimal"
              aria-label="Output price"
              onchange={(e) => setNum(`lanes.${id}.cost.output`, e.currentTarget.value)}
            /></label
          >
          <span class="np-dim">$ per Mtok (empty: the catalog's)</span>
        </div>

        <span class="lbl">Daily cap</span>
        <div class="row">
          <input
            class="np-input money"
            value={lane.budget?.limitUsd ?? ''}
            placeholder="none"
            inputmode="decimal"
            aria-label="Daily cap"
            onchange={(e) => setNum(`lanes.${id}.budget.limitUsd`, e.currentTarget.value)}
          />
          <span class="np-dim">$ a day for this lane, besides the budget</span>
        </div>
      {/if}
    </div>
  {:else}
    <div class="np-dim">This lane was removed.</div>
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
  .slots {
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
