<script>
  /**
   * One provider's per-model overrides (`providers.instances.<id>.models.<modelId>`): the name shown, hidden, the context
   * window, the output limit and (local kinds) the runs at once. A model id may hold dots (qwen3.8-27b), so Save writes
   * the provider's whole `models` map, keeping the keys this dialog does not show (transport, replayReasoning…). Hidden
   * models are not in the model list, so the ones the map names are listed too.
   */
  import Modal from '../../lib/kit/Modal.svelte';
  import SearchInput from '../../lib/kit/SearchInput.svelte';
  import { app } from '../../lib/state/app.svelte.js';
  import { mayClose } from '../../lib/state/ui.svelte.js';
  import { getAt, setSetting } from '../../lib/settings.js';

  let { id, type, title = id, doc, onclose } = $props();

  /** The keys each kind reads per model (docs/SETTINGS.md). */
  const FIELDS = {
    aiproxy: ['displayName', 'hidden', 'contextWindow', 'maxOutputTokens', 'concurrency'],
    'openai-compatible': ['displayName', 'hidden', 'contextWindow', 'maxOutputTokens', 'concurrency'],
    openrouter: ['displayName', 'hidden', 'contextWindow', 'maxOutputTokens'],
  };
  const NUM = ['contextWindow', 'maxOutputTokens', 'concurrency'];
  const fields = $derived(FIELDS[type] ?? []);

  const saved = () => structuredClone(getAt(doc, `providers.instances.${id}.models`) ?? {});
  let draft = $state(saved());
  let q = $state('');
  let saving = $state(false);
  let invalid = $state({}); // `model|key` of the number fields whose text is not a whole number above 0: marked, and Save waits
  const LIMIT = 300; // rows shown at once; the filter reaches the others

  const listed = $derived(app.models.filter((m) => m.provider === id));
  const rows = $derived.by(() => {
    const ids = new Set([...listed.map((m) => m.id), ...Object.keys(draft)]);
    const query = q.trim().toLowerCase();
    return [...ids]
      .filter((mid) => !query || mid.toLowerCase().includes(query) || String(draft[mid]?.displayName ?? '').toLowerCase().includes(query))
      .sort((a, b) => Number(!!draft[b] && Object.keys(draft[b]).length) - Number(!!draft[a] && Object.keys(draft[a]).length) || a.localeCompare(b))
      .map((mid) => ({ id: mid, model: listed.find((m) => m.id === mid) ?? null }));
  });

  const shown = $derived(rows.slice(0, LIMIT));

  function set(mid, key, raw) {
    const entry = { ...(draft[mid] ?? {}) };
    let v = raw;
    if (NUM.includes(key)) {
      const t = String(raw ?? '').trim();
      v = t === '' ? undefined : Number(t);
      const bad = v !== undefined && !(Number.isInteger(v) && v > 0);
      if (bad) invalid[`${mid}|${key}`] = true;
      else delete invalid[`${mid}|${key}`];
      if (bad) return;
    } else if (key === 'displayName') v = String(raw ?? '').trim() || undefined;
    else if (key === 'hidden') v = raw ? true : undefined;
    if (v === undefined) delete entry[key];
    else entry[key] = v;
    if (Object.keys(entry).length) draft[mid] = entry;
    else delete draft[mid];
  }

  const dirty = $derived(JSON.stringify(draft) !== JSON.stringify(saved()));
  async function close() {
    if (await mayClose(dirty)) onclose();
  }
  const anyInvalid = $derived(Object.keys(invalid).length > 0);
  async function save() {
    saving = true;
    const value = Object.keys(draft).length ? $state.snapshot(draft) : null;
    if (await setSetting(`providers.instances.${id}.models`, value)) onclose();
    saving = false;
  }
</script>

<Modal title="Models of {title}" width={760} onclose={close} class="section-dialog">
  <div class="help np-dim">
    Overrides for single models of <span class="np-mono">{id}</span>: the name in the lists, hidden (left out of every list), the
    context window and output limit where the backend reports none or a wrong one{#if fields.includes('concurrency')}, and how
    many runs a local model serves at once{/if}. Empty fields use what the provider reports.
  </div>
  <div class="bar"><SearchInput bind:value={q} placeholder="Filter models" /></div>
  <div class="grid" style:--cols={fields.length}>
    <div class="h">Model</div>
    {#each fields as f (f)}<div class="h">{({ displayName: 'Name', hidden: 'Hidden', contextWindow: 'Context', maxOutputTokens: 'Max output', concurrency: 'At once' })[f]}</div>{/each}
    {#each shown as r (r.id)}
      <div class="mid np-mono np-ellipsis" title={r.id}>{r.id}{#if !r.model}<span class="np-dim"> · not listed</span>{/if}</div>
      {#each fields as f (f)}
        {#if f === 'hidden'}
          <input type="checkbox" class="ck" checked={!!draft[r.id]?.hidden} onchange={(e) => set(r.id, 'hidden', e.currentTarget.checked)} aria-label="Hide {r.id}" />
        {:else}
          <input
            class="np-input"
            class:num={NUM.includes(f)}
            class:bad={invalid[`${r.id}|${f}`]}
            aria-invalid={!!invalid[`${r.id}|${f}`]}
            value={draft[r.id]?.[f] ?? ''}
            placeholder={f === 'displayName' ? (r.model?.displayName ?? '') : NUM.includes(f) ? String(r.model?.[f] ?? '') : ''}
            inputmode={NUM.includes(f) ? 'numeric' : undefined}
            spellcheck="false"
            onchange={(e) => set(r.id, f, e.currentTarget.value)}
            aria-label="{f} of {r.id}"
          />
        {/if}
      {/each}
    {:else}
      <div class="np-empty full">No models.</div>
    {/each}
  </div>
  {#if rows.length > LIMIT}<div class="np-dim np-small more">{rows.length - LIMIT} more models: filter to narrow the list.</div>{/if}
  {#snippet footer()}
    {#if anyInvalid}<span class="np-small err">A limit must be a whole number above 0.</span>{/if}
    <span class="np-dim np-small">{Object.keys(draft).length} with overrides</span>
    <span class="np-spacer"></span>
    <button class="np-btn np-btn-ghost np-btn-sm" onclick={onclose}>Cancel</button>
    <button class="np-btn np-btn-primary np-btn-sm" disabled={!dirty || saving || anyInvalid} onclick={save}>{saving ? 'Saving…' : 'Save'}</button>
  {/snippet}
</Modal>

<style>
  .help {
    margin-bottom: 10px;
    font-size: var(--fs-sm);
  }
  .bar {
    margin-bottom: 8px;
  }
  .grid {
    display: grid;
    grid-template-columns: minmax(140px, 1.4fr) repeat(calc(var(--cols)), minmax(0, auto));
    gap: 4px 8px;
    align-items: center;
  }
  .h {
    color: var(--fg-muted);
    font-size: var(--fs-xs);
    font-weight: 600;
  }
  .mid {
    font-size: var(--fs-sm);
  }
  .np-input {
    width: 150px;
    height: 26px;
    font-size: var(--fs-sm);
  }
  .np-input.num {
    width: 90px;
    text-align: right;
  }
  .ck {
    accent-color: var(--accent);
    justify-self: center;
  }
  .np-input.bad {
    border-color: var(--err);
  }
  .err {
    color: var(--err);
  }
  .more {
    margin-top: 6px;
  }
  .full {
    grid-column: 1 / -1;
  }
</style>
