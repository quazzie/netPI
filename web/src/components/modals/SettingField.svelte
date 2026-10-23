<script>
  /**
   * One host setting from settings.schema as a control: toggle, number, text, text area, secret, choice, list, model,
   * folder, file, folders / files (lists of paths, browsed per row), pick / pickText (names chosen in order from what
   * the options offer: SettingPick), agents (the agent picker; "free" is every free agent); a string with options offers
   * them as quick choices beside the field. Saves with settings.set on change (text and numbers when the field is left); an unset key shows its
   * default (a text with a built-in default shows that text, and stays unset while it is unchanged); "Reset" removes the
   * key so the default applies again. The help shows its first sentence ("More" shows the rest); the ⋯ menu names the
   * key, copies it, shows it in settings.json (the dialog's `settings` context) and resets it. A
   * list also opens the list editor (one entry per line).
   */
  import { getContext } from 'svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import Menu from '../../lib/kit/Menu.svelte';
  import { getAt, setSetting, parseList } from '../../lib/settings.js';
  import { helpParts } from '../../lib/settingsModel.js';
  import { toast } from '../../lib/state/ui.svelte.js';
  import ModelSelect from '../ModelSelect.svelte';
  import AgentSelect from '../../lib/kit/AgentSelect.svelte';
  import { agentList } from '../../lib/agents.js';
  import ListEditor from './ListEditor.svelte';
  import SettingPick from './SettingPick.svelte';
  import SkillsDialog from './SkillsDialog.svelte';
  import { pickFolder, pickFile } from '../../lib/folderPicker.js';

  /** disabled: its section is switched off. highlight: the field the dialog was opened on (it flashes once). */
  let { setting: s, doc, disabled = false, highlight = false } = $props();
  const dialog = getContext('settings') ?? null;

  const value = $derived(getAt(doc, s.key));
  const isSet = $derived(value !== undefined && value !== null);
  const current = $derived(isSet ? value : s.default);
  const stacked = $derived(s.type === 'text' || s.type === 'pick' || s.type === 'pickText' || (s.type === 'string' && s.options?.length > 0));
  const isList = $derived(s.type === 'list' || s.type === 'folders' || s.type === 'files');
  // a text setting whose default is a built-in text (the opening of the system prompt, the AGENTS.md guidance)
  const textDefault = $derived(s.type === 'text' && typeof s.default === 'string' ? s.default : null);
  const shown = () => (textDefault != null && !isSet ? textDefault : toText(value));
  const help = $derived(helpParts(s.help));

  let draft = $state('');
  let focused = $state(false);
  let show = $state(false);
  let invalid = $state('');
  let more = $state(false);
  let editing = $state(false);

  function toText(v) {
    if (v === undefined || v === null) return '';
    if (Array.isArray(v)) return v.join(', ');
    return typeof v === 'object' ? JSON.stringify(v) : String(v);
  }
  // show the saved value unless the user is typing
  $effect(() => {
    value;
    if (!focused) draft = shown();
  });

  const placeholder = $derived(textDefault != null ? '' : (s.placeholder ?? (s.default == null ? '' : toText(s.default))));
  const defaultText = $derived(s.default == null || textDefault != null ? '' : toText(s.default) + (s.unit ? ` ${s.unit}` : ''));

  async function commit() {
    focused = false;
    invalid = '';
    const t = s.type === 'text' ? draft : draft.trim();
    let next;
    // unchanged built-in text: stays unset; emptied: an explicit empty text (e.g. no AGENTS.md guidance)
    if (textDefault != null) next = t.trim() === textDefault.trim() ? null : t.trim();
    else if (t.trim() === '') next = null;
    else if (s.type === 'int' || s.type === 'number') {
      const n = Number(t);
      if (!Number.isFinite(n) || (s.type === 'int' && !Number.isInteger(n))) return (invalid = s.type === 'int' ? 'a whole number' : 'a number');
      if (s.min != null && n < s.min) return (invalid = `at least ${s.min}`);
      if (s.max != null && n > s.max) return (invalid = `at most ${s.max}`);
      next = n;
    } else if (isList) next = parseList(t);
    else next = t;
    if (JSON.stringify(next) === JSON.stringify(isSet ? value : null)) return;
    await setSetting(s.key, next);
  }

  function onKey(e) {
    if (e.key === 'Enter' && s.type !== 'text') e.currentTarget.blur();
    if (e.key === 'Escape') {
      draft = shown();
      e.currentTarget.blur();
    }
  }

  async function browse() {
    const pick = s.type === 'file' ? pickFile : pickFolder;
    const picked = await pick({ initial: toText(value) || s.placeholder || null, title: s.label ?? s.key });
    if (picked) await setSetting(s.key, picked);
  }

  async function copyKey() {
    try {
      await navigator.clipboard.writeText(s.key);
      toast(`Copied ${s.key}`);
    } catch {
      toast(s.key);
    }
  }

  const menu = $derived([
    { label: 'Copy key', hint: s.key, icon: 'copy', onclick: copyKey },
    ...(dialog?.showJson ? [{ label: 'Show in settings.json', icon: 'file', onclick: () => dialog.showJson(s.key) }] : []),
    { label: isSet ? 'Reset to the default' : 'Is the default', hint: defaultText, icon: 'refresh', disabled: !isSet, onclick: () => setSetting(s.key, null) },
  ]);
</script>

<div class="field" data-setting={s.key} class:stacked class:disabled class:flash={highlight}>
  <div class="lbl">
    <div class="name">
      {s.label ?? s.key}
      {#if s.applies}<span class="applies">{s.applies === 'restart' ? 'after a restart' : 'for new sessions'}</span>{/if}
      {#if textDefault != null && !isSet}<span class="applies">default text</span>{/if}
      {#if isSet}<span class="dot" title="Changed from the default"></span>{/if}
    </div>
    {#if s.help}
      <div class="help">
        {help.first}{#if help.rest && more}{' '}{help.rest}{/if}
        {#if help.rest}<button class="more" onclick={() => (more = !more)} aria-expanded={more}>{more ? 'Less' : 'More'}</button>{/if}
      </div>
    {/if}
  </div>
  <div class="ctl">
    {#if s.type === 'bool'}
      <input type="checkbox" class="check" checked={!!current} {disabled} onchange={(e) => setSetting(s.key, e.currentTarget.checked)} aria-label={s.label ?? s.key} />
    {:else if s.type === 'choice'}
      <div class="np-seg">
        {#each s.options ?? [] as o (o)}
          <button aria-pressed={current === o} {disabled} onclick={() => current !== o && setSetting(s.key, o)}>{o}</button>
        {/each}
      </div>
    {:else if s.type === 'pick' || s.type === 'pickText'}
      <SettingPick setting={s} {value} {disabled} />
    {:else if s.type === 'agents'}
      <div class="sel">
        <AgentSelect
          agents={agentList()}
          multiple
          value={Array.isArray(current) ? current : []}
          options={[{ value: 'free', label: 'Any free agent', hint: 'every agent whose model costs nothing' }]}
          placeholder={s.placeholder ? `None (${s.placeholder})` : 'None'}
          label={s.label ?? s.key}
          {disabled}
          onchange={(list) => setSetting(s.key, list.length ? list : null)}
        />
      </div>
    {:else if s.type === 'model'}
      <div class="sel"><ModelSelect value={isSet ? value : null} none={s.placeholder ?? 'the default'} label={s.label ?? s.key} onchange={(ref) => setSetting(s.key, ref)} /></div>
    {:else if s.type === 'text'}
      <textarea
        class="np-input area"
        rows="4"
        bind:value={draft}
        {placeholder}
        {disabled}
        onfocus={() => (focused = true)}
        onblur={commit}
        onkeydown={onKey}
        aria-label={s.label ?? s.key}
      ></textarea>
    {:else}
      {#if s.type === 'string' && s.options?.length}
        <div class="np-seg quick">
          {#each s.options as o (o)}
            <button aria-pressed={current === o} {disabled} onclick={() => current !== o && setSetting(s.key, o === s.default ? null : o)}>{o}</button>
          {/each}
        </div>
      {/if}
      <div class="inline" class:num={s.type === 'int' || s.type === 'number'}>
        <input
          class="np-input"
          class:bad={!!invalid}
          aria-invalid={!!invalid}
          type={s.type === 'secret' && !show ? 'password' : 'text'}
          inputmode={s.type === 'int' || s.type === 'number' ? 'decimal' : undefined}
          bind:value={draft}
          {placeholder}
          {disabled}
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
        {#if s.type === 'folder' || s.type === 'file'}
          <button class="np-btn np-btn-sm" onclick={browse} {disabled}>Browse…</button>
        {/if}
        {#if isList}
          <button class="np-btn np-btn-sm" onclick={() => (editing = true)} {disabled} data-testid="edit-list" title={s.key === 'skills.disabled' ? 'Choose from the skills found' : 'One entry per line'}>{s.key === 'skills.disabled' ? 'Choose…' : 'Edit…'}</button>
        {/if}
      </div>
    {/if}
    {#if isSet && s.type !== 'bool'}
      <button class="reset" data-testid="reset-setting" title={defaultText ? `Remove the setting: the default (${defaultText}) applies again` : 'Remove the setting: the default applies again'} onclick={() => setSetting(s.key, null)}>Reset</button>
    {:else if isSet && s.type === 'bool' && s.default != null && value !== s.default}
      <button class="reset" data-testid="reset-setting" title="Back to the default" onclick={() => setSetting(s.key, null)}>Reset</button>
    {/if}
    <Menu items={menu} minWidth={240}>
      {#snippet trigger({ toggle, open })}
        <button class="icon menu-btn" class:open title="More" aria-label="More about {s.label ?? s.key}" onclick={toggle}><Icon name="more" size={13} /></button>
      {/snippet}
    </Menu>
  </div>
  {#if invalid}<div class="bad-msg">Must be {invalid}.</div>{/if}
</div>

{#if editing && s.key === 'skills.disabled'}
  <SkillsDialog {doc} onclose={() => (editing = false)} />
{:else if editing}
  <ListEditor setting={s} {doc} onclose={() => (editing = false)} />
{/if}

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
  .dot {
    display: inline-block;
    width: 6px;
    height: 6px;
    margin-left: 6px;
    border-radius: 50%;
    background: var(--accent);
    vertical-align: middle;
  }
  .help {
    margin-top: 2px;
    color: var(--fg-dim);
    font-size: var(--fs-sm);
  }
  .more {
    padding: 0;
    border: 0;
    background: transparent;
    color: var(--accent);
    font-size: inherit;
    cursor: pointer;
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
    flex-wrap: wrap;
    justify-content: flex-start;
  }
  .quick {
    flex-wrap: wrap;
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
    width: 300px;
    max-width: 100%;
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
  .menu-btn {
    opacity: 0.5;
  }
  .field:hover .menu-btn,
  .menu-btn:focus-visible,
  .menu-btn.open {
    opacity: 1;
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
  .disabled .lbl {
    opacity: 0.55;
  }
  .flash {
    animation: flash 1.8s var(--ease);
  }
  @keyframes flash {
    0%,
    45% {
      background: var(--accent-soft);
    }
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
  @media (max-width: 640px) {
    .field {
      grid-template-columns: minmax(0, 1fr);
    }
    .ctl {
      justify-content: flex-start;
      flex-wrap: wrap;
    }
    .inline .np-input {
      width: min(240px, 60vw);
    }
  }
</style>
