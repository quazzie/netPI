<script>
  /**
   * The model providers the owner has added (settings `providers.instances`, keyed by provider id). It starts empty:
   * "Add provider" offers the kinds the plugins describe (schema sections with a providerType), adds one and opens it for
   * configuration; a row opens the provider's own dialog (name, the kind's settings, Remove). Fields save as they change,
   * like every setting; the providers follow live.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import Modal from '../../lib/kit/Modal.svelte';
  import ConfirmButton from '../../lib/kit/ConfirmButton.svelte';
  import SettingsRow from './SettingsRow.svelte';
  import SettingField from './SettingField.svelte';
  import ProviderModelsDialog from './ProviderModelsDialog.svelte';
  import { app } from '../../lib/state/app.svelte.js';
  import { getAt, setSetting } from '../../lib/settings.js';

  let { schema, doc } = $props();

  const types = $derived((schema ?? []).filter((s) => s.providerType).sort((a, b) => (a.order ?? 0) - (b.order ?? 0)));
  const typeOf = (t) => types.find((s) => s.providerType === t) ?? null;
  const instances = $derived(Object.entries(getAt(doc, 'providers.instances') ?? {}).filter(([, v]) => v && typeof v === 'object'));

  let choosing = $state(false);
  let openId = $state(null);
  let perModelId = $state(null); // the provider whose per-model overrides are open
  const PER_MODEL = new Set(['aiproxy', 'openai-compatible', 'openrouter']);
  const open = $derived(openId ? instances.find(([id]) => id === openId) ?? null : null);
  const openType = $derived(open ? typeOf(open[1].type) : null);

  const modelsOf = (id) => app.models.filter((m) => m.provider === id).length;

  function row([id, v]) {
    const t = typeOf(v.type);
    const off = v.enabled === false;
    const n = modelsOf(id);
    return {
      title: v.name || id,
      subtitle: `${t?.title ?? v.type}${v.name ? ` · ${id}` : ''}${off ? '' : ` · ${n} model${n === 1 ? '' : 's'}`}`,
      badges: [off ? { text: 'off', tone: 'warn' } : null, !t ? { text: 'plugin missing', tone: 'warn' } : null],
    };
  }

  function freeId(base) {
    const taken = new Set(instances.map(([id]) => id));
    let id = base;
    for (let i = 2; taken.has(id); i++) id = `${base}-${i}`;
    return id;
  }

  async function add(t) {
    const id = freeId(t.providerType);
    if (await setSetting(`providers.instances.${id}`, { type: t.providerType })) {
      choosing = false;
      openId = id;
    }
  }

  async function remove(id) {
    if (await setSetting(`providers.instances.${id}`, null)) openId = null;
  }

  const field = (id, st) => ({ ...st, key: `providers.instances.${id}.${st.key}` });
  const nameField = (id) => ({ key: `providers.instances.${id}.name`, type: 'string', label: 'Name', help: 'Shown in the model lists.', placeholder: id });
</script>

<section class="sec" data-section="providers">
  <div class="sec-title">Providers</div>
  <div class="sec-help np-dim">Where models come from. Add one, choose its kind and configure it; add as many as you like, also of the same kind.</div>
  <div class="rows">
    {#each instances as inst (inst[0])}
      {@const r = row(inst)}
      <SettingsRow title={r.title} subtitle={r.subtitle} badges={r.badges} onclick={() => (openId = inst[0])} data-provider={inst[0]} />
    {:else}
      <div class="none np-dim">No providers yet: no models are available until you add one.</div>
    {/each}
  </div>
  <button class="np-btn np-btn-sm add" onclick={() => (choosing = true)} disabled={!types.length}><Icon name="plus" size={12} /> Add provider</button>
</section>

{#if choosing}
  <Modal title="Add provider" width={520} onclose={() => (choosing = false)} class="section-dialog">
    <div class="sec-help np-dim">Choose a kind of provider; you configure it next.</div>
    <div class="rows">
      {#each types as t (t.id)}
        <SettingsRow title={t.title} subtitle={t.help ?? ''} onclick={() => add(t)} data-provider-type={t.providerType} />
      {/each}
    </div>
  </Modal>
{/if}

{#if open}
  <Modal title={open[1].name || open[0]} width={620} onclose={() => (openId = null)} class="section-dialog">
    <div class="sec-help np-dim">{openType?.title ?? open[1].type} · id <span class="np-mono">{open[0]}</span> (models are <span class="np-mono">{open[0]}/…</span>)</div>
    {#if openType?.help}<div class="sec-help np-dim">{openType.help}</div>{/if}
    <SettingField setting={nameField(open[0])} {doc} />
    {#each openType?.settings ?? [] as st (st.key)}<SettingField setting={field(open[0], st)} {doc} />{/each}
    <div class="foot">
      {#if PER_MODEL.has(open[1].type)}
        <button class="np-btn np-btn-sm" onclick={() => (perModelId = open[0])} data-testid="provider-models">Models… <span class="np-dim">{modelsOf(open[0])}</span></button>
      {/if}
      <span class="np-spacer"></span>
      <ConfirmButton icon="trash" label="Remove provider" confirmLabel="Remove it?" onconfirm={() => remove(open[0])} />
    </div>
  </Modal>
{/if}

{#if perModelId && instances.find(([id]) => id === perModelId)}
  {@const inst = instances.find(([id]) => id === perModelId)}
  <ProviderModelsDialog id={inst[0]} type={inst[1].type} title={inst[1].name || inst[0]} {doc} onclose={() => (perModelId = null)} />
{/if}

<style>
  .sec {
    margin-bottom: 22px;
  }
  .sec-title {
    margin: 10px 0 2px;
    font-size: 13.5px;
    font-weight: 600;
  }
  .sec-help {
    margin-bottom: 6px;
    font-size: var(--fs-sm);
  }
  .rows {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .none {
    padding: 10px 2px;
    font-size: var(--fs-sm);
  }
  .add {
    margin-top: 8px;
  }
  .foot {
    display: flex;
    align-items: center;
    gap: 8px;
    padding-top: 12px;
  }
</style>
