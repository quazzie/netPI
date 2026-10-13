<script>
  /**
   * A `records` setting: an array of objects the owner adds and removes (extra providers). One card per entry with a text
   * field per schema field (a `list` field is typed comma-separated and saved as an array); "Add" appends an empty entry,
   * which is saved once it has an id, and "Remove" drops the entry. Saved with settings.set on leaving a field.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import { setSetting, parseList } from '../../lib/settings.js';

  let { setting: s, value } = $props();

  const saved = $derived(Array.isArray(value) ? value : []);
  let draftNew = $state(null); // an entry not saved yet (no id)

  const show = (v) => (Array.isArray(v) ? v.join(', ') : (v ?? ''));
  const parse = (f, text) => (f.list ? parseList(text) : text.trim());
  const idKey = $derived(s.fields?.[0]?.key ?? 'id');

  async function change(index, f, text) {
    const next = saved.map((e) => ({ ...e }));
    const v = parse(f, text);
    if (v === '' || (Array.isArray(v) && !v.length)) delete next[index][f.key];
    else next[index][f.key] = v;
    await setSetting(s.key, next);
  }
  async function remove(index) {
    const next = saved.filter((_, i) => i !== index);
    await setSetting(s.key, next.length ? next : null);
  }
  async function commitNew() {
    const entry = {};
    for (const f of s.fields ?? []) {
      const v = parse(f, draftNew[f.key] ?? '');
      if (v !== '' && !(Array.isArray(v) && !v.length)) entry[f.key] = v;
    }
    if (!entry[idKey]) return;
    if (await setSetting(s.key, [...saved, entry])) draftNew = null;
  }
  const startNew = () => (draftNew = {});
</script>

<div class="records">
  {#each saved as entry, i (i)}
    <div class="card">
      {#each s.fields ?? [] as f (f.key)}
        <label>
          <span>{f.label}</span>
          <input class="np-input" value={show(entry[f.key])} placeholder={f.placeholder ?? ''} spellcheck="false" autocomplete="off"
            onchange={(e) => change(i, f, e.currentTarget.value)} />
        </label>
      {/each}
      <button class="np-btn np-btn-sm" onclick={() => remove(i)}><Icon name="x" size={12} /> Remove</button>
    </div>
  {/each}
  {#if draftNew}
    <div class="card">
      {#each s.fields ?? [] as f (f.key)}
        <label>
          <span>{f.label}</span>
          <input class="np-input" bind:value={draftNew[f.key]} placeholder={f.placeholder ?? ''} spellcheck="false" autocomplete="off" />
        </label>
      {/each}
      <div class="row">
        <button class="np-btn np-btn-sm" onclick={commitNew} disabled={!(draftNew[idKey] ?? '').trim()}>Save</button>
        <button class="np-btn np-btn-sm np-btn-ghost" onclick={() => (draftNew = null)}>Cancel</button>
      </div>
    </div>
  {:else}
    <button class="np-btn np-btn-sm" onclick={startNew}><Icon name="plus" size={12} /> Add</button>
  {/if}
</div>

<style>
  .records {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 8px;
    width: 100%;
  }
  .card {
    display: flex;
    flex-direction: column;
    gap: 6px;
    width: 100%;
    padding: 8px;
    border: 1px solid var(--border);
    border-radius: 6px;
  }
  label {
    display: grid;
    grid-template-columns: 130px minmax(0, 1fr);
    align-items: center;
    gap: 8px;
    font-size: var(--fs-sm);
    color: var(--fg-dim);
  }
  .row {
    display: flex;
    gap: 6px;
  }
</style>
