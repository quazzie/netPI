<script>
  /**
   * The lanes the user sets up for agents (settings lanes.<id>): one row each (model, slots, price, the note), a dialog
   * to edit one (LaneDialog), and "Add lane" to start one from the searchable model list; the id is a slug of the model.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import SettingsRow from './SettingsRow.svelte';
  import LaneDialog from './LaneDialog.svelte';
  import ModelMenu from '../ModelMenu.svelte';
  import { setSetting } from '../../lib/settings.js';
  import { app } from '../../lib/state/app.svelte.js';
  import { usd } from '../../lib/format.js';

  let { doc, lanes = [] } = $props();

  const RESERVED = new Set(['pools', 'budgets', 'localDefaultCapacity', 'cloudDefaultCapacity']);
  const configured = $derived(
    Object.entries(doc?.lanes ?? {}).filter(([id, v]) => !RESERVED.has(id) && v && typeof v === 'object' && typeof v.model === 'string'),
  );
  const infoOf = (id) => lanes.find((p) => p.key === id) ?? null;

  let openId = $state(null);
  let addBtn = $state();
  let adding = $state(false);

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
  async function add(ref) {
    adding = false;
    if (!ref) return;
    const base = slug(ref);
    let id = base;
    for (let n = 2; doc?.lanes?.[id] || RESERVED.has(id); n++) id = `${base}-${n}`;
    const m = app.modelsByRef.get(ref);
    if (await setSetting(`lanes.${id}`, { model: ref, capacity: m?.isLocal ? (m.concurrency ?? 1) : 1, use: '' })) openId = id;
  }

  function badges(lane, info) {
    const m = app.modelsByRef.get(lane.model);
    const slots = lane.capacity ?? info?.capacity ?? 1;
    return [
      { text: `${slots} slot${slots === 1 ? '' : 's'}` },
      info?.free
        ? { text: 'free', tone: 'ok' }
        : info?.priceInput != null
          ? { text: `${usd(info.priceInput)} / ${usd(info.priceOutput)}` }
          : m
            ? { text: 'price unknown', tone: 'warn' }
            : { text: 'not listed', tone: 'warn' },
    ];
  }
</script>

<div class="lanes">
  {#each configured as [id, lane] (id)}
    {@const m = app.modelsByRef.get(lane.model)}
    <SettingsRow
      title={id}
      mono
      subtitle={(m?.displayName || lane.model) + (lane.use ? ` · ${lane.use}` : '')}
      badges={badges(lane, infoOf(id))}
      onclick={() => (openId = id)}
      data-lane={id}
    />
  {:else}
    <div class="empty np-dim">
      No lanes yet. Add one per model you want agents to use, with a note on when to use it; they pick among these for
      subagents.
    </div>
  {/each}
  <div class="add">
    <button class="np-btn np-btn-sm" bind:this={addBtn} onclick={() => (adding = !adding)}><Icon name="plus" size={12} /> Add lane</button>
  </div>
</div>

{#if adding}
  <ModelMenu anchor={addBtn} onchoose={add} onclose={() => (adding = false)} />
{/if}
{#if openId}
  <LaneDialog id={openId} lane={doc?.lanes?.[openId]} info={infoOf(openId)} onclose={() => (openId = null)} />
{/if}

<style>
  .lanes {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin-bottom: 12px;
  }
  .empty {
    font-size: var(--fs-sm);
  }
  .add {
    display: flex;
    margin-top: 4px;
  }
</style>
