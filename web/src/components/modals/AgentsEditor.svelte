<script>
  /**
   * The agents (settings agents.<id>): one row each (model, state, instances, price, the note), a dialog to edit one
   * (AgentDialog), and "Add agent" to start one from the searchable model list; the id is a slug of the model's id.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import SettingsRow from './SettingsRow.svelte';
  import AgentDialog from './AgentDialog.svelte';
  import ModelMenu from '../ModelMenu.svelte';
  import { app } from '../../lib/state/app.svelte.js';
  import { RESERVED, createAgent } from '../../lib/agents.js';
  import { usd } from '../../lib/format.js';

  let { doc } = $props();

  const configured = $derived(
    Object.entries(doc?.agents ?? {}).filter(([id, v]) => !RESERVED.has(id) && v && typeof v === 'object' && typeof v.model === 'string'),
  );
  const infoOf = (id) => app.slots.find((p) => p.configured && p.key === id) ?? null;

  let openId = $state(null);
  let addBtn = $state();
  let adding = $state(false);

  async function add(ref) {
    adding = false;
    if (!ref) return;
    const id = await createAgent(ref, Object.keys(doc?.agents ?? {}));
    if (id) openId = id;
  }

  function badges(agent, info) {
    const m = app.modelsByRef.get(agent.model);
    const n = info?.capacity ?? agent.instances ?? 1;
    return [
      agent.disabled
        ? { text: 'off', tone: 'warn' }
        : info && !info.available
          ? { text: /isn't loaded/.test(info.unavailable ?? '') ? 'not loaded' : 'not active', tone: 'warn' }
          : info?.busy
            ? { text: `${info.busy}/${info.capacity} busy`, tone: 'ok' }
            : null,
      { text: `${n} instance${n === 1 ? '' : 's'}` },
      m?.isLocal && info?.resourceCapacity ? { text: `${info.resourceCapacity} shared model slots` } : null,
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

<div class="agents">
  {#each configured as [id, agent] (id)}
    {@const m = app.modelsByRef.get(agent.model)}
    {@const info = infoOf(id)}
    <SettingsRow
      title={id}
      mono
      subtitle={(m?.displayName || agent.model) + (agent.use ? ` · ${agent.use}` : '')}
      badges={badges(agent, info)}
      onclick={() => (openId = id)}
      data-agent={id}
    />
  {:else}
    <div class="empty np-dim">
      No agents yet. Add one per model you want to use: chats and subagents run on agents. Two agents on one local model
      share its slots.
    </div>
  {/each}
  <div class="add">
    <button class="np-btn np-btn-sm" bind:this={addBtn} onclick={() => (adding = !adding)}><Icon name="plus" size={12} /> Add agent</button>
  </div>
</div>

{#if adding}
  <ModelMenu anchor={addBtn} onchoose={add} onclose={() => (adding = false)} />
{/if}
{#if openId}
  <AgentDialog
    id={openId}
    agent={doc?.agents?.[openId]}
    info={infoOf(openId)}
    localSlots={doc?.models?.localSlots ?? 2}
    taken={Object.keys(doc?.agents ?? {})}
    onrename={(id) => (openId = id)}
    onclose={() => (openId = null)}
  />
{/if}

<style>
  .agents {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin-bottom: 12px;
  }
  .empty {
    font-size: var(--fs-sm);
    line-height: 1.45;
  }
  .add {
    display: flex;
    margin-top: 4px;
  }
</style>
