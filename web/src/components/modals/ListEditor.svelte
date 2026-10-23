<script>
  /**
   * A list setting one entry per row, over the settings: add, remove, move, a check per entry where the setting has
   * one (an address or CIDR range for `*.allowFrom`), and Browse per row for a list of folders or files. Save writes the list (settings.set); a list equal to the default is
   * the default again (the key is removed), so Save never pins a default.
   */
  import { untrack } from 'svelte';
  import Modal from '../../lib/kit/Modal.svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import { getAt, setSetting } from '../../lib/settings.js';
  import { cidrProblem } from '../../lib/settingsModel.js';
  import { pickFolder, pickFile } from '../../lib/folderPicker.js';

  let { setting: s, doc, onclose } = $props();

  const CHECKS = { 'webhooks.allowFrom': cidrProblem, 'phone.allowFrom': cidrProblem };
  const check = $derived(CHECKS[s.key] ?? null);

  const start = () => {
    const v = getAt(doc, s.key) ?? s.default ?? [];
    return (Array.isArray(v) ? v : []).map((text, i) => ({ id: i, text: String(text) }));
  };
  const initial = untrack(start);
  let rows = $state(initial);
  let next = initial.length;
  let saving = $state(false);
  let list = $state();

  const entries = $derived(rows.map((r) => r.text.trim()).filter(Boolean));
  const problems = $derived(check ? rows.map((r) => (r.text.trim() ? check(r.text) : null)) : rows.map(() => null));
  const bad = $derived(problems.some(Boolean));

  function add() {
    rows.push({ id: next++, text: '' });
    requestAnimationFrame(() => list?.querySelector('li:last-child input')?.focus());
  }
  const paths = $derived(s.type === 'folders' || s.type === 'files');
  async function browse(i) {
    const pick = s.type === 'files' ? pickFile : pickFolder;
    const picked = await pick({ initial: rows[i].text.trim() || null, title: s.label ?? s.key });
    if (picked) rows[i].text = picked;
  }
  function move(i, d) {
    const j = i + d;
    if (j < 0 || j >= rows.length) return;
    [rows[i], rows[j]] = [rows[j], rows[i]];
  }
  async function save() {
    if (bad) return;
    const same = (a, b) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
    const value = same(entries, s.default ?? []) ? null : entries;
    saving = true;
    const current = getAt(doc, s.key) ?? null;
    if (same(value, current) || (await setSetting(s.key, value))) onclose();
    saving = false;
  }
  function onKey(e, i) {
    if (e.key === 'Enter') {
      e.preventDefault();
      if (i === rows.length - 1) add();
      else list?.querySelectorAll('input')[i + 1]?.focus();
    }
  }
</script>

<Modal title={s.label ?? s.key} width={560} {onclose} class="section-dialog">
  {#if s.help}<div class="help np-dim">{s.help}</div>{/if}
  <ol class="rows" bind:this={list} data-testid="list-editor" data-setting={s.key}>
    {#each rows as r, i (r.id)}
      <li class:bad={!!problems[i]}>
        <input class="np-input np-mono" bind:value={r.text} spellcheck="false" autocomplete="off" aria-label="Entry {i + 1}" onkeydown={(e) => onKey(e, i)} />
        {#if paths}<button class="np-btn np-btn-sm" onclick={() => browse(i)}>Browse…</button>{/if}
        <button class="icon" title="Up" aria-label="Move up" disabled={i === 0} onclick={() => move(i, -1)}><Icon name="chevron-up" size={13} /></button>
        <button class="icon" title="Down" aria-label="Move down" disabled={i === rows.length - 1} onclick={() => move(i, 1)}><Icon name="chevron-down" size={13} /></button>
        <button class="icon" title="Remove" aria-label="Remove entry {i + 1}" onclick={() => rows.splice(i, 1)}><Icon name="x" size={13} /></button>
        {#if problems[i]}<div class="msg">{problems[i]}</div>{/if}
      </li>
    {:else}
      <li class="none np-dim">No entries.</li>
    {/each}
  </ol>
  <button class="np-btn np-btn-sm" onclick={add}><Icon name="plus" size={12} /> Add</button>
  {#snippet footer()}
    {#if s.default?.length}<span class="np-dim np-small">Default: {s.default.length} entr{s.default.length === 1 ? 'y' : 'ies'}</span>{/if}
    <span class="np-spacer"></span>
    {#if s.default}<button class="np-btn np-btn-ghost np-btn-sm" onclick={() => (rows = (s.default ?? []).map((text, i) => ({ id: next + i, text })))}>Use the default</button>{/if}
    <button class="np-btn np-btn-ghost np-btn-sm" onclick={onclose}>Cancel</button>
    <button class="np-btn np-btn-primary np-btn-sm" disabled={bad || saving} onclick={save}>{saving ? 'Saving…' : 'Save'}</button>
  {/snippet}
</Modal>

<style>
  .help {
    margin-bottom: 10px;
    font-size: var(--fs-sm);
  }
  .rows {
    display: flex;
    flex-direction: column;
    gap: 4px;
    margin: 0 0 10px;
    padding: 0;
    list-style: none;
  }
  li {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto auto auto auto;
    gap: 2px;
    align-items: center;
  }
  li .np-input {
    width: 100%;
    font-size: var(--fs-sm);
  }
  li.bad .np-input {
    border-color: var(--err);
  }
  .msg {
    grid-column: 1 / -1;
    color: var(--err);
    font-size: var(--fs-xs);
  }
  .none {
    display: block;
    padding: 6px 2px;
    font-size: var(--fs-sm);
  }
  .icon {
    display: grid;
    place-items: center;
    width: 24px;
    height: 24px;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--fg-dim);
  }
  .icon:hover:not(:disabled) {
    background: var(--bg-2);
    color: var(--fg);
  }
  .icon:disabled {
    opacity: 0.35;
  }
</style>
