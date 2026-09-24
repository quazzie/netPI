<script>
  /**
   * One host setting from settings.schema as a control: toggle, number, text, text area, secret, choice, list, model,
   * folder or file. Saves with settings.set on change (text and numbers when the field is left); an unset key shows its
   * default; "Reset" removes the key so the default applies again.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import { getAt, setSetting, parseList } from '../../lib/settings.js';
  import { app } from '../../lib/state/app.svelte.js';
  import { pickFolder } from '../../lib/folderPicker.js';

  let { setting: s, doc } = $props();

  const value = $derived(getAt(doc, s.key));
  const isSet = $derived(value !== undefined && value !== null);
  const current = $derived(isSet ? value : s.default);
  const stacked = $derived(s.type === 'text');

  let draft = $state('');
  let focused = $state(false);
  let show = $state(false);
  let invalid = $state('');

  function toText(v) {
    if (v === undefined || v === null) return '';
    if (Array.isArray(v)) return v.join(', ');
    return typeof v === 'object' ? JSON.stringify(v) : String(v);
  }
  // show the saved value unless the user is typing
  $effect(() => {
    const v = value;
    if (!focused) draft = toText(v);
  });

  const placeholder = $derived(s.placeholder ?? (s.default == null ? '' : toText(s.default)));

  async function commit() {
    focused = false;
    invalid = '';
    const t = s.type === 'text' ? draft : draft.trim();
    let next;
    if (t.trim() === '') next = null;
    else if (s.type === 'int' || s.type === 'number') {
      const n = Number(t);
      if (!Number.isFinite(n) || (s.type === 'int' && !Number.isInteger(n))) return (invalid = s.type === 'int' ? 'a whole number' : 'a number');
      if (s.min != null && n < s.min) return (invalid = `at least ${s.min}`);
      if (s.max != null && n > s.max) return (invalid = `at most ${s.max}`);
      next = n;
    } else if (s.type === 'list') next = parseList(t);
    else next = t;
    if (JSON.stringify(next) === JSON.stringify(isSet ? value : null)) return;
    await setSetting(s.key, next);
  }

  function onKey(e) {
    if (e.key === 'Enter' && s.type !== 'text') e.currentTarget.blur();
    if (e.key === 'Escape') {
      draft = toText(value);
      e.currentTarget.blur();
    }
  }

  async function browse() {
    const picked = await pickFolder({ initial: toText(value) || null, title: s.label ?? s.key });
    if (picked) await setSetting(s.key, picked);
  }

  const models = $derived(app.models.map((m) => ({ ref: m.ref ?? `${m.provider}/${m.id}`, name: m.displayName || m.id })));
</script>

<div class="field" class:stacked>
  <div class="lbl">
    <div class="name">
      {s.label ?? s.key}
      {#if s.applies}<span class="applies">{s.applies === 'restart' ? 'after a restart' : 'for new sessions'}</span>{/if}
    </div>
    {#if s.help}<div class="help">{s.help}</div>{/if}
  </div>
  <div class="ctl">
    {#if s.type === 'bool'}
      <input type="checkbox" class="check" checked={!!current} onchange={(e) => setSetting(s.key, e.currentTarget.checked)} aria-label={s.label ?? s.key} />
    {:else if s.type === 'choice'}
      <div class="np-seg">
        {#each s.options ?? [] as o (o)}
          <button aria-pressed={current === o} onclick={() => current !== o && setSetting(s.key, o)}>{o}</button>
        {/each}
      </div>
    {:else if s.type === 'model'}
      <select class="np-input sel" value={isSet ? value : ''} onchange={(e) => setSetting(s.key, e.currentTarget.value || null)} aria-label={s.label ?? s.key}>
        <option value="">{s.placeholder ?? 'default'}</option>
        {#if isSet && !models.some((m) => m.ref === value)}<option value={value}>{value}</option>{/if}
        {#each models as m (m.ref)}<option value={m.ref}>{m.ref}</option>{/each}
      </select>
    {:else if s.type === 'text'}
      <textarea
        class="np-input area"
        rows="4"
        bind:value={draft}
        {placeholder}
        onfocus={() => (focused = true)}
        onblur={commit}
        onkeydown={onKey}
        aria-label={s.label ?? s.key}
      ></textarea>
    {:else}
      <div class="inline" class:num={s.type === 'int' || s.type === 'number'}>
        <input
          class="np-input"
          class:bad={!!invalid}
          type={s.type === 'secret' && !show ? 'password' : 'text'}
          inputmode={s.type === 'int' || s.type === 'number' ? 'decimal' : undefined}
          bind:value={draft}
          {placeholder}
          spellcheck="false"
          autocomplete="off"
          onfocus={() => (focused = true)}
          onblur={commit}
          onkeydown={onKey}
          aria-label={s.label ?? s.key}
        />
        {#if s.unit}<span class="unit">{s.unit}</span>{/if}
        {#if s.type === 'secret'}
          <button class="icon" title={show ? 'Hide' : 'Show'} onclick={() => (show = !show)}><Icon name={show ? 'eye-off' : 'eye'} size={13} /></button>
        {/if}
        {#if s.type === 'folder'}
          <button class="np-btn np-btn-sm" onclick={browse}>Browse…</button>
        {/if}
      </div>
    {/if}
    {#if isSet && s.type !== 'bool'}
      <button class="reset" title="Remove the setting: the default applies again" onclick={() => setSetting(s.key, null)}>Reset</button>
    {:else if isSet && s.type === 'bool' && s.default != null && value !== s.default}
      <button class="reset" title="Back to the default" onclick={() => setSetting(s.key, null)}>Reset</button>
    {/if}
  </div>
  {#if invalid}<div class="bad-msg">Must be {invalid}.</div>{/if}
</div>

<style>
  .field {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 4px 16px;
    align-items: center;
    padding: 10px 0;
    border-bottom: 1px solid var(--border);
  }
  .field:last-child {
    border-bottom: 0;
  }
  .field.stacked {
    grid-template-columns: minmax(0, 1fr);
  }
  .name {
    font-size: var(--fs);
  }
  .applies {
    margin-left: 6px;
    padding: 0 5px;
    border-radius: 4px;
    background: var(--bg-2);
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
  .help {
    margin-top: 2px;
    color: var(--fg-dim);
    font-size: var(--fs-sm);
  }
  .ctl {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 6px;
    min-width: 0;
  }
  .stacked .ctl {
    justify-content: stretch;
    align-items: flex-start;
  }
  .inline {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .inline .np-input {
    width: 240px;
  }
  .inline.num .np-input {
    width: 110px;
    text-align: right;
    font-variant-numeric: tabular-nums;
  }
  .unit {
    color: var(--fg-dim);
    font-size: var(--fs-sm);
  }
  .sel {
    max-width: 260px;
  }
  .area {
    width: 100%;
    height: auto;
    padding: 6px 8px;
    line-height: 1.5;
    resize: vertical;
  }
  .check {
    accent-color: var(--accent);
    width: 16px;
    height: 16px;
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
  .icon:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .reset {
    border: 0;
    background: transparent;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    text-decoration: underline;
    cursor: pointer;
  }
  .reset:hover {
    color: var(--fg);
  }
  .np-input.bad {
    border-color: var(--err);
  }
  .bad-msg {
    grid-column: 1 / -1;
    color: var(--err);
    font-size: var(--fs-xs);
    text-align: right;
  }
</style>
