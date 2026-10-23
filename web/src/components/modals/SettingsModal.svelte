<script>
  /**
   * The settings dialog. The nav lists the pages under their headings (settingsModel.js PAGES), with a search above it
   * that finds every setting, UI preference, page, section, agent, profile, provider and plugin; a result is the live
   * control, under a breadcrumb that opens its place. The Changed and Restart filters list what differs from its default
   * and what applies after a restart. A small section is shown in its page; a bigger one is a row that opens it in
   * place (breadcrumb, Back, Esc), with its on/off switch on the row. Editors of one item (an agent, a profile, a
   * provider, a list, the tool exposure) are dialogs over this one.
   *
   * target: true (the place it was left at), a page id, or { page?, section?, key?, q? } (modals.settings).
   */
  import { setContext, tick, untrack } from 'svelte';
  import Modal from '../../lib/kit/Modal.svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import SearchInput from '../../lib/kit/SearchInput.svelte';
  import ConfirmButton from '../../lib/kit/ConfirmButton.svelte';
  import SettingField from './SettingField.svelte';
  import SettingsSection from './SettingsSection.svelte';
  import PrefField from './PrefField.svelte';
  import AgentsEditor from './AgentsEditor.svelte';
  import ProfilesEditor from './ProfilesEditor.svelte';
  import SettingsRow from './SettingsRow.svelte';
  import PluginSwitches from './PluginSwitches.svelte';
  import ProvidersPanel from './ProvidersPanel.svelte';
  import ToolExposureDialog from './ToolExposureDialog.svelte';
  import { prefs, toast, modals, mayClose } from '../../lib/state/ui.svelte.js';
  import { app } from '../../lib/state/app.svelte.js';
  import { settingsDoc } from '../../lib/state/settingsDoc.svelte.js';
  import { rpc, conn } from '../../lib/rpc.svelte.js';
  import { bus } from '../../lib/bus.js';
  import { load as loadLocal, save as saveLocal } from '../../lib/persist.js';
  import { setSetting } from '../../lib/settings.js';
  import {
    getAt,
    isSet,
    pagesOf,
    navOf,
    settingDefault,
    sectionState,
    sectionInline,
    sectionOff,
    enabledSettingOf,
    resolveTarget,
    unknownKeys,
  } from '../../lib/settingsModel.js';
  import { buildIndex, search, groupResults } from '../../lib/settingsSearch.js';
  import { PREF_SECTIONS, prefsFor, prefChanged } from '../../lib/uiPrefs.js';

  let { onclose, target = true } = $props();

  const PLACE_KEY = 'netpi.settingsPlace';
  const first = untrack(() => (target === true || target == null ? (loadLocal(PLACE_KEY, null) ?? { page: 'general' }) : target));

  // the schema (host + plugins) and the document; the agents' states are app.slots
  let schema = $state([]);
  let schemaLoaded = $state(false);
  const doc = $derived(settingsDoc.doc);
  const layout = $derived(pagesOf(schema));
  const nav = $derived(navOf(layout.pages));
  const desktop = $derived(!!app.info?.desktop);
  const uiPrefs = $derived(prefsFor(desktop));
  const prefSections = $derived(PREF_SECTIONS.filter((s) => desktop || !s.desktop));

  let pageId = $state(typeof first === 'string' ? first : (first.page ?? 'general'));
  let sectionId = $state(null);
  let reveal = $state(null);
  let q = $state(typeof first === 'object' ? (first.q ?? '') : '');
  let onlyChanged = $state(false);
  let onlyRestart = $state(false);
  let pending = untrack(() => (typeof first === 'object' && (first.section || first.key) ? first : null));
  let searchEl = $state();
  let content = $state();
  let exposureOpen = $state(false);
  let plugins = $state([]);
  // where the dialog has been, for Back (the arrow, Alt+Left, the mouse's back button): each move to another page,
  // section or search remembers the place it left
  let trail = $state.raw([]);

  const page = $derived(layout.pages.find((p) => p.id === pageId) ?? layout.pages[0]);
  const section = $derived(sectionId ? (layout.sections.get(sectionId)?.section ?? null) : null);
  // the current opening of the system prompt: context.customPrompt, else the built-in text
  const opening = $derived(getAt(doc, 'context.customPrompt') || settingDefault(schema, 'context.customPrompt') || '');

  $effect(() => settingsDoc.follow());
  function loadSchema() {
    return rpc('settings.schema')
      .then((s) => (schema = s ?? []))
      .catch(() => (schema = []))
      .finally(() => (schemaLoaded = true));
  }
  function loadPlugins() {
    rpc('plugins.list')
      .then((p) => (plugins = p ?? []))
      .catch(() => {});
  }
  $effect(() => {
    loadSchema();
    loadPlugins();
    const offs = [bus.on('plugins.changed', () => (loadSchema(), loadPlugins()))];
    return () => offs.forEach((off) => off());
  });

  // a target with a section or a key waits for the schema that says where it is
  $effect(() => {
    if (!schemaLoaded || !pending) return;
    const t = pending;
    pending = null;
    untrack(() => go(resolveTarget(t, layout, uiPrefs.map((p) => p.key))));
  });
  // remember the place for the next opening
  $effect(() => {
    saveLocal(PLACE_KEY, { page: pageId, section: sectionId });
  });
  // a section that went away (its plugin unloaded) leaves its page
  $effect(() => {
    if (schemaLoaded && sectionId && !layout.sections.has(sectionId)) untrack(() => (sectionId = null));
  });

  const here = () => ({ page: pageId, section: sectionId, q, onlyChanged, onlyRestart });
  const same = (a, b) => a.page === b.page && a.section === b.section && a.q === b.q && a.onlyChanged === b.onlyChanged && a.onlyRestart === b.onlyRestart;
  /** Remember the place being left (once; at most 50 back). */
  function remember() {
    const h = here();
    if (trail.length && same(trail.at(-1), h)) return;
    trail = [...trail, h].slice(-50);
  }
  const canBack = $derived(!!sectionId || trail.some((p) => !same(p, here())));

  /** Back to the place before: the last one remembered that is not this one, else from a section to its page. */
  async function goBack() {
    const h = here();
    let t = trail;
    while (t.length && same(t.at(-1), h)) t = t.slice(0, -1);
    if (!t.length) {
      trail = t;
      return back();
    }
    const p = t.at(-1);
    trail = t.slice(0, -1);
    const left = sectionId;
    pageId = p.page;
    sectionId = p.section;
    q = p.q;
    onlyChanged = p.onlyChanged;
    onlyRestart = p.onlyRestart;
    reveal = null;
    await tick();
    if (content) content.scrollTop = 0;
    if (left && !sectionId) document.querySelector(`.settings-layout [data-section-row="${CSS.escape(left)}"]`)?.focus();
    return true;
  }
  /** A search or a filter that starts remembers where it started from. */
  function searching() {
    if (!filtering) remember();
  }
  function mouseBack(e) {
    if (e.button !== 3) return;
    e.preventDefault(); // the browser's (WebView's) own back
    if (e.type === 'mouseup') goBack();
  }

  /** Open a place: a page, a section (in place when it is a row, else its page), a key (scrolled to and flashed). */
  async function go({ page: p = null, section: s = null, key = null } = {}) {
    remember();
    q = '';
    onlyChanged = onlyRestart = false;
    const where = s ? layout.sections.get(s) : null;
    pageId = p ?? where?.page.id ?? pageId;
    sectionId = where && !sectionInline(where.section) ? s : null;
    reveal = key;
    await tick();
    if (content) content.scrollTop = 0;
    // a key: its field, in the middle; a section shown in its page: its start
    if (key) document.querySelector(`.settings-layout [data-setting="${CSS.escape(key)}"], .settings-layout [data-pref="${CSS.escape(key)}"]`)?.scrollIntoView({ block: 'center' });
    else if (s && !sectionId) content?.querySelector(`section[data-section="${CSS.escape(s)}"]`)?.scrollIntoView({ block: 'start' });
    if (key) setTimeout(() => (reveal === key ? (reveal = null) : null), 2000);
  }
  function openPage(id) {
    if (id !== pageId || sectionId || filtering) remember();
    q = '';
    onlyChanged = onlyRestart = false;
    pageId = id;
    sectionId = null;
    reveal = null;
    tick().then(() => content && (content.scrollTop = 0));
  }
  function back() {
    if (sectionId) {
      const s = sectionId;
      sectionId = null;
      tick().then(() => document.querySelector(`.settings-layout [data-section-row="${CSS.escape(s)}"]`)?.focus());
      return true;
    }
    return false;
  }
  /** Esc: clears the search and filters, then leaves a section, then closes the dialog. */
  function onescape() {
    if (q || onlyChanged || onlyRestart) {
      q = '';
      onlyChanged = onlyRestart = false;
      return true;
    }
    return back();
  }
  function onKey(e) {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f' && page?.id !== 'json') {
      e.preventDefault();
      searchEl?.querySelector('input')?.focus();
    } else if (e.altKey && e.key === 'ArrowLeft') {
      e.preventDefault();
      goBack();
    }
  }

  // ---------------------------------------------------------------- search
  const items = $derived.by(() => {
    const out = [];
    for (const [id, v] of Object.entries(doc?.agents ?? {}))
      if (v && typeof v === 'object' && typeof v.model === 'string')
        out.push({ kind: 'item', id: `agent:${id}`, page: 'agents', group: 'Agents', label: id, sub: `${v.model} ${v.use ?? ''}`, icon: 'bot', open: () => (modals.agent = id) });
    for (const [id, v] of Object.entries(doc?.profiles ?? {}))
      if (v && typeof v === 'object') out.push({ kind: 'item', id: `profile:${id}`, page: 'profiles', group: 'Profiles', label: v.name || id, sub: `profile ${id}`, icon: 'user' });
    for (const [id, v] of Object.entries(getAt(doc, 'providers.instances') ?? {}))
      if (v && typeof v === 'object') out.push({ kind: 'item', id: `provider:${id}`, page: 'models', group: 'Providers', label: v.name || id, sub: `provider ${id} ${v.type ?? ''}`, icon: 'cpu' });
    for (const p of plugins) out.push({ kind: 'item', id: `plugin:${p.id}`, page: 'plugins', group: 'Plugins', label: p.name, sub: `plugin ${p.id} ${p.description ?? ''}`, icon: 'puzzle' });
    out.push({ kind: 'item', id: 'tool-exposure', page: 'tools', group: 'Tools', label: 'Tool exposure', sub: 'tools off deferred disabled tool_search every chat tools.disabled tools.deferred', icon: 'wrench', open: () => ((pageId = 'tools'), (exposureOpen = true)) });
    out.push({ kind: 'item', id: 'unknown-keys', page: 'advanced', group: 'Advanced', label: 'Unknown keys', sub: 'typo leftover undeclared settings.json', icon: 'alert', open: () => go({ page: 'advanced', section: null }) });
    return out;
  });
  const index = $derived(buildIndex(layout, { prefs: uiPrefs, prefSections, items }));
  const filtering = $derived(!!q.trim() || onlyChanged || onlyRestart);
  const filter = $derived.by(() => {
    if (!onlyChanged && !onlyRestart) return null;
    return (e) =>
      (e.kind === 'setting' || e.kind === 'pref') &&
      (!onlyChanged || (e.kind === 'setting' ? isSet(doc, e.key) : prefChanged(e.pref, prefs))) &&
      (!onlyRestart || e.setting?.applies === 'restart');
  });
  const results = $derived(filtering ? search(index, q, { filter }) : []);
  const grouped = $derived(groupResults(results));
  const fieldCount = $derived(results.filter((e) => e.kind === 'setting' || e.kind === 'pref').length);
  const resultLine = $derived(
    [`${fieldCount} setting${fieldCount === 1 ? '' : 's'}`, onlyChanged && 'changed', onlyRestart && 'that apply after a restart', q.trim() && `for “${q.trim()}”`]
      .filter(Boolean)
      .join(' '),
  );
  const changedCount = $derived(index.filter((e) => (e.kind === 'setting' ? isSet(doc, e.key) : e.kind === 'pref' && prefChanged(e.pref, prefs))).length);

  function disabledFor(e) {
    if (e.kind !== 'setting') return false;
    const s = layout.sections.get(e.section)?.section;
    return !!s && sectionOff(s, doc) && enabledSettingOf(s)?.key !== e.key;
  }
  function openEntry(e) {
    if (e.open) return e.open();
    if (e.kind === 'section') return go({ section: e.section });
    if (e.kind === 'page' || e.kind === 'item') return openPage(e.page);
    go({ page: e.page, section: e.kind === 'setting' ? e.section : null, key: e.key });
  }
  /** How many settings of a page are set (its nav dot). */
  function pageChanged(p) {
    let n = 0;
    for (const s of p.sections) n += sectionState(s, doc).changed;
    if (p.id === 'general') n += uiPrefs.filter((x) => prefChanged(x, prefs)).length;
    return n;
  }

  // ---------------------------------------------------------------- section rows
  function rowOf(s) {
    const { changed, off } = sectionState(s, doc);
    return {
      subtitle: s.help || s.settings.map((st) => st.label ?? st.key).join(' · '),
      badges: [off ? { text: 'off', tone: 'warn' } : null, changed ? { text: `${changed} changed` } : null],
    };
  }

  // ---------------------------------------------------------------- unknown keys (Advanced)
  const unknown = $derived(schemaLoaded ? unknownKeys(doc, schema) : []);
  const preview = (v) => {
    const t = JSON.stringify(v);
    return t && t.length > 80 ? t.slice(0, 79) + '…' : t;
  };

  // ---------------------------------------------------------------- settings.json
  let raw = $state('');
  let original = $state('');
  let path = $state('');
  let loading = $state(false);
  let saving = $state(false);
  let loadError = $state('');
  let editor = $state();
  let jsonKey = null;

  function fill() {
    path = settingsDoc.path;
    raw = original = JSON.stringify(settingsDoc.doc ?? {}, null, 2);
  }
  async function load() {
    loading = true;
    loadError = '';
    try {
      await settingsDoc.refresh(); // a Reload reads the file again, not the copy in memory
      fill();
    } catch (e) {
      loadError = e.message;
    } finally {
      loading = false;
    }
  }
  $effect(() => {
    if (pageId === 'json' && !original && !loading && !loadError) load();
  });

  /** Open settings.json with the cursor on a key (a field's ⋯ menu). */
  async function showJson(key) {
    jsonKey = key;
    openPage('json');
    if (!original) await load();
    await tick();
    selectKey();
  }
  setContext('settings', { showJson });

  function selectKey() {
    const key = jsonKey;
    jsonKey = null;
    if (!key || !editor) return;
    // walk the path's names in order: each is looked for after the one before
    let at = 0;
    let found = -1;
    for (const part of key.split('.')) {
      found = raw.indexOf(JSON.stringify(part) + ':', at);
      if (found < 0) break;
      at = found + 1;
    }
    editor.focus();
    if (found < 0) {
      toast(`${key} is not in settings.json: it has its default`);
      return;
    }
    const last = JSON.stringify(key.split('.').at(-1));
    editor.setSelectionRange(found, found + last.length);
    const line = raw.slice(0, found).split('\n').length;
    editor.scrollTop = Math.max(0, (line - 6) * 19.4);
  }

  /** Nothing matched: settings.json with the text selected where it first occurs. */
  async function lookInJson() {
    const text = q.trim();
    openPage('json');
    if (!original) await load();
    await tick();
    const i = raw.toLowerCase().indexOf(text.toLowerCase());
    if (i < 0 || !editor) return toast(`“${text}” is not in settings.json`);
    editor.focus();
    editor.setSelectionRange(i, i + text.length);
    editor.scrollTop = Math.max(0, (raw.slice(0, i).split('\n').length - 6) * 19.4);
  }

  const parseError = $derived.by(() => {
    if (!raw.trim()) return 'Settings must be a JSON object';
    try {
      const v = JSON.parse(raw);
      if (!v || typeof v !== 'object' || Array.isArray(v)) return 'Settings must be a JSON object';
      return null;
    } catch (e) {
      const m = /position (\d+)/.exec(e.message);
      if (m) {
        const pos = +m[1];
        const before = raw.slice(0, pos);
        const line = before.split('\n').length;
        const col = pos - before.lastIndexOf('\n');
        return `${e.message.replace(/ in JSON at position \d+.*$/, '')} (line ${line}, column ${col})`;
      }
      return e.message;
    }
  });
  const dirty = $derived(raw !== original);
  // settings.json edited and not saved: closing asks (leaving the page with Esc keeps the text, closing the dialog drops it)
  async function close() {
    if (await mayClose(dirty)) onclose();
  }

  async function save() {
    if (parseError) return;
    saving = true;
    try {
      // base = the document as loaded (or last saved): if it changed in the meantime the host answers with a
      // conflict instead of silently clobbering the other change.
      await rpc('settings.replace', { settings: JSON.parse(raw), base: JSON.parse(original) });
      original = raw = JSON.stringify(JSON.parse(raw), null, 2);
      settingsDoc.refresh();
      toast('Settings saved');
    } catch (e) {
      toast(`Save failed: ${e.message}`, 'error');
    } finally {
      saving = false;
    }
  }

  function onEditorKey(e) {
    if (e.key === 'Tab') {
      e.preventDefault();
      const ta = e.currentTarget;
      const s = ta.selectionStart;
      raw = raw.slice(0, s) + '  ' + raw.slice(ta.selectionEnd);
      requestAnimationFrame(() => ta.setSelectionRange(s + 2, s + 2));
    } else if ((e.ctrlKey || e.metaKey) && e.key === 's') {
      e.preventDefault();
      save();
    }
  }

  // settings.json changed elsewhere (another client, an editor, a plugin): show it unless there are unsaved edits here
  let seenVersion = settingsDoc.version;
  $effect(() => {
    const v = settingsDoc.version;
    if (v === seenVersion) return;
    seenVersion = v;
    if (original && raw === original && !saving && !loading) fill();
  });
</script>

{#snippet backButton()}
  <button class="back" onclick={goBack} disabled={!canBack} data-testid="settings-back" title="Back (Alt+←, the mouse's back button)" aria-label="Back"><Icon name="arrow-left" size={14} /></button>
{/snippet}

{#snippet sectionRow(s)}
  {@const r = rowOf(s)}
  {@const en = enabledSettingOf(s)}
  <div class="srow">
    <SettingsRow title={s.title} subtitle={r.subtitle} badges={r.badges} onclick={() => go({ section: s.id })} data-section-row={s.id} data-section={s.id} />
    {#if en}
      <input
        type="checkbox"
        class="np-switch"
        checked={(getAt(doc, en.key) ?? en.default) !== false}
        onchange={(e) => setSetting(en.key, e.currentTarget.checked === en.default ? null : e.currentTarget.checked)}
        aria-label="{s.title} on"
        title={en.label ?? en.key}
      />
    {/if}
  </div>
{/snippet}

{#snippet sections(list)}
  {@const inline = list.filter((s) => sectionInline(s))}
  {@const rows = list.filter((s) => !sectionInline(s))}
  {#if rows.length}
    <div class="rows">
      {#each rows as s (s.id)}{@render sectionRow(s)}{/each}
    </div>
  {/if}
  {#each inline as s (s.id)}<SettingsSection section={s} {doc} {reveal} />{/each}
{/snippet}

<Modal title="Settings" width={960} padded={false} onclose={close} {onescape}>
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="layout settings-layout" onkeydown={onKey} onmousedown={mouseBack} onmouseup={mouseBack}>
    <nav class="nav">
      <div class="search" bind:this={searchEl}>
        <SearchInput bind:value={q} placeholder="Search settings" data-testid="settings-search" aria-label="Search settings" onbeforeinput={searching} />
      </div>
      <div class="chips">
        <button class="chip" aria-pressed={onlyChanged} onclick={() => (searching(), (onlyChanged = !onlyChanged))} data-testid="filter-changed">Changed{#if changedCount}<span class="n">{changedCount}</span>{/if}</button>
        <button class="chip" aria-pressed={onlyRestart} onclick={() => (searching(), (onlyRestart = !onlyRestart))} data-testid="filter-restart" title="Settings that apply after a restart">Restart</button>
      </div>
      <div class="pages">
        {#each nav as g (g.heading)}
          {#if g.heading}<div class="heading">{g.heading}</div>{/if}
          {#each g.pages as p (p.id)}
            {@const n = pageChanged(p)}
            <button class:active={!filtering && pageId === p.id} data-page={p.id} onclick={() => openPage(p.id)}>
              <Icon name={p.icon} size={14} />
              <span class="t">{p.title}</span>
              {#if n}<span class="n" title="{n} changed from the default">{n}</span>{/if}
            </button>
          {/each}
        {/each}
      </div>
    </nav>

    <div class="content" data-testid="settings-content" bind:this={content}>
      <div class="narrow">
        <select class="np-input" value={pageId} onchange={(e) => openPage(e.currentTarget.value)} aria-label="Page">
          {#each layout.pages as p (p.id)}<option value={p.id}>{p.title}</option>{/each}
        </select>
      </div>

      {#if filtering}
        <div class="results" data-testid="settings-results">
          <div class="rhead np-dim np-small">
            {#if canBack}{@render backButton()}{/if}
            {#if fieldCount || grouped.links.length}
              {resultLine}
            {:else}
              Nothing matches.
              {#if q.trim()}<button class="link" onclick={lookInJson}>Look in settings.json</button>{/if}
            {/if}
          </div>
          {#if grouped.links.length}
            <div class="links">
              {#each grouped.links.slice(0, 8) as e (e.id)}
                <button class="lnk" onclick={() => openEntry(e)} data-result={e.id}>
                  <Icon name={e.icon ?? (e.kind === 'page' ? (layout.byId[e.page]?.icon ?? 'sliders') : 'chevron-right')} size={13} />
                  <span>{e.label}</span>
                  <span class="np-dim np-small">{e.kind === 'page' ? 'page' : e.kind === 'section' ? e.pageTitle : `${e.group}`}</span>
                </button>
              {/each}
            </div>
          {/if}
          {#each grouped.groups as g (g.key)}
            <div class="rgroup">
              <button class="crumb" onclick={() => (g.section.startsWith('pref:') ? go({ page: 'general' }) : go({ section: g.section }))}>
                {g.pageTitle} <Icon name="chevron-right" size={11} /> {g.sectionTitle}
              </button>
              {#each g.entries as e (e.id)}
                {#if e.kind === 'setting'}
                  <SettingField setting={e.setting} {doc} disabled={disabledFor(e)} />
                {:else}
                  <PrefField pref={e.pref} />
                {/if}
              {/each}
            </div>
          {/each}
        </div>
      {:else if section}
        {@const en = enabledSettingOf(section)}
        <div class="sub-head">
          {@render backButton()}
          <div class="crumbs">
            <button class="crumb" onclick={() => back()}>{page.title}</button>
            <Icon name="chevron-right" size={11} />
            <span class="here">{section.title}</span>
          </div>
          {#if en}
            <input
              type="checkbox"
              class="np-switch"
              checked={(getAt(doc, en.key) ?? en.default) !== false}
              onchange={(e) => setSetting(en.key, e.currentTarget.checked === en.default ? null : e.currentTarget.checked)}
              aria-label="{section.title} on"
              title={en.label ?? en.key}
            />
          {/if}
        </div>
        {#if section.help}<div class="sub-help np-dim">{section.help}</div>{/if}
        <SettingsSection section={section} {doc} {reveal} title={false} />
      {:else if page}
        <div class="page-head">{#if canBack}{@render backButton()}{/if}<div class="page-title">{page.title}</div></div>
        {#if page.id === 'general'}
          {#each prefSections as ps (ps.id)}
            <section class="sec" data-section="pref:{ps.id}">
              <div class="sec-title">{ps.title}</div>
              {#each uiPrefs.filter((x) => x.section === ps.id) as p (p.key)}<PrefField pref={p} highlight={reveal === p.key} />{/each}
            </section>
          {/each}
          {@render sections(page.sections)}
        {:else if page.id === 'agents'}
          <section class="sec" data-section="agents">
            <div class="sec-title">Agents</div>
            <div class="sec-help np-dim">
              An agent is a model with a number of instances (runs at once) and a note on when to use it. Chats and subagents
              run on agents; an agent is active while its model is loaded (local) or reachable (cloud), unless you switch it off.
            </div>
            <AgentsEditor {doc} />
          </section>
          {#each page.sections as s (s.id)}<SettingsSection section={s} {doc} {reveal} />{/each}
        {:else if page.id === 'profiles'}
          <section class="sec">
            <div class="sec-title">Profiles</div>
            <div class="sec-help np-dim">
              A profile is the opening of a chat's system prompt and the tools it gets. Pick one per chat next to the model
              (free before the first message; later the chat is read again once), or give one to a project for its new chats.
            </div>
            <ProfilesEditor {doc} {opening} />
          </section>
          {@render sections(page.sections)}
        {:else if page.id === 'models'}
          <ProvidersPanel {schema} {doc} />
          {@render sections(page.sections)}
        {:else if page.id === 'tools'}
          <div class="rows lead">
            <SettingsRow
              title="Tool exposure"
              subtitle="Which tools are off in every chat, and which are deferred until a chat uses them"
              badges={[(getAt(doc, 'tools.disabled') ?? []).length ? { text: `${getAt(doc, 'tools.disabled').length} off`, tone: 'warn' } : null, getAt(doc, 'tools.deferred') ? { text: 'own deferred list' } : null]}
              icon="wrench"
              onclick={() => (exposureOpen = true)}
              data-testid="tool-exposure-row"
            />
          </div>
          {@render sections(page.sections)}
        {:else if page.id === 'plugins'}
          <section class="sec"><PluginSwitches {doc} /></section>
        {:else if page.id === 'advanced'}
          {@render sections(page.sections)}
          <section class="sec" data-section="unknown-keys">
            <div class="sec-title">Unknown keys</div>
            <div class="sec-help np-dim">
              Keys in settings.json that no loaded plugin declares: a typo, a leftover of a removed setting, or the setting of a
              plugin that is switched off. The agents, profiles, providers, MCP servers and plugin lists are not checked here.
            </div>
            {#each unknown as u (u.key)}
              <div class="unknown" data-unknown={u.key}>
                <span class="np-mono k">{u.key}</span>
                <span class="np-mono np-dim v np-ellipsis" title={JSON.stringify(u.value)}>{preview(u.value)}</span>
                <button class="np-btn np-btn-ghost np-btn-sm" onclick={() => showJson(u.key)}>Show</button>
                <ConfirmButton icon="trash" label="Remove" confirmLabel="Remove it?" onconfirm={() => setSetting(u.key, null)} />
              </div>
            {:else}
              <div class="np-dim np-small none">None: every key is declared.</div>
            {/each}
          </section>
          <div class="rows">
            <SettingsRow title="settings.json" subtitle="Edit the whole file as JSON" icon="file" onclick={() => openPage('json')} />
          </div>
        {:else if page.id === 'json'}
          <div class="json">
            <div class="json-head">
              <span class="np-mono np-dim np-small np-ellipsis" title={path}>{path || 'settings.json'}</span>
              <span class="np-spacer"></span>
              <button class="np-btn np-btn-sm np-btn-ghost" onclick={load} disabled={loading}><Icon name="refresh" size={12} /> Reload</button>
            </div>
            {#if loadError}
              <div class="np-empty">Could not load settings: {loadError}</div>
            {:else}
              <textarea class="editor np-mono" bind:this={editor} bind:value={raw} spellcheck="false" onkeydown={onEditorKey} disabled={loading} aria-label="settings.json"></textarea>
              <div class="json-foot">
                {#if parseError}
                  <span class="bad"><Icon name="alert" size={13} /> {parseError}</span>
                {:else}
                  <span class="good"><Icon name="check" size={13} /> Valid JSON</span>
                {/if}
                <span class="np-spacer"></span>
                {#if dirty}<button class="np-btn np-btn-ghost np-btn-sm" onclick={() => (raw = original)}>Revert</button>{/if}
                <button class="np-btn np-btn-primary np-btn-sm" disabled={!!parseError || !dirty || saving} onclick={save}>
                  {saving ? 'Saving…' : 'Save'} <span class="np-kbd">Ctrl+S</span>
                </button>
              </div>
            {/if}
          </div>
        {:else if page.id === 'about'}
          <dl class="np-kv about">
            <dt>Version</dt><dd>{app.info?.version ?? conn.version ?? '—'}</dd>
            <dt>OS</dt><dd>{app.info?.os ?? '—'}</dd>
            <dt>Home</dt><dd class="np-mono">{app.info?.home ?? '—'}</dd>
            <dt>App dir</dt><dd class="np-mono">{app.info?.appDir ?? '—'}</dd>
            <dt>Workspace</dt><dd class="np-mono">{app.info?.defaultWorkspace ?? '—'}</dd>
            <dt>Shell</dt><dd>{app.info?.desktop ? 'Desktop (WebView2)' : 'Browser'}</dd>
            <dt>Client</dt><dd class="np-mono">{conn.clientId ?? '—'}</dd>
            <dt>Models</dt><dd>{app.models.length}</dd>
            <dt>Plugin tabs</dt><dd>{app.uiTabs.map((t) => t.title).join(', ') || '—'}</dd>
          </dl>
        {:else}
          {@render sections(page.sections)}
        {/if}
      {/if}
    </div>
  </div>
</Modal>

{#if exposureOpen}<ToolExposureDialog {doc} onclose={() => (exposureOpen = false)} />{/if}

<style>
  .layout {
    display: flex;
    height: min(720px, calc(80vh - 46px));
    min-height: 360px;
  }
  .nav {
    width: 200px;
    flex: none;
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 10px 8px;
    border-right: 1px solid var(--border);
    overflow: auto;
    scrollbar-width: thin;
  }
  .search :global(.np-search) {
    width: 100%;
  }
  .chips {
    display: flex;
    gap: 4px;
  }
  .chip {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    height: 22px;
    padding: 0 8px;
    border: 1px solid var(--border);
    border-radius: 11px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
  }
  .chip[aria-pressed='true'] {
    border-color: var(--accent-line);
    background: var(--accent-soft);
    color: var(--fg);
  }
  .pages {
    display: flex;
    flex-direction: column;
    gap: 1px;
  }
  .heading {
    margin: 10px 10px 2px;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    font-weight: 600;
    letter-spacing: 0.04em;
    text-transform: uppercase;
  }
  .nav .pages button {
    display: flex;
    align-items: center;
    gap: 8px;
    height: 29px;
    padding: 0 10px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    text-align: left;
  }
  .nav .pages button:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .nav .pages button.active {
    background: var(--bg-3);
    color: var(--fg);
  }
  .nav .t {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .n {
    min-width: 16px;
    padding: 0 4px;
    border-radius: 8px;
    background: var(--accent-soft);
    color: var(--accent);
    font-size: 10.5px;
    text-align: center;
    font-variant-numeric: tabular-nums;
  }
  .content {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    padding: 14px 20px 18px;
    overflow: auto;
    scrollbar-width: thin;
  }
  .narrow {
    display: none;
  }
  .page-head {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 4px;
  }
  .page-title {
    font-size: 15px;
    font-weight: 600;
  }
  .sec {
    margin-bottom: 18px;
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
    margin: 6px 0 18px;
  }
  .rows.lead {
    margin-top: 8px;
  }
  .srow {
    display: flex;
    align-items: center;
    gap: 10px;
  }
  .srow :global(.setting-row) {
    flex: 1;
    min-width: 0;
  }
  .sub-head {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 4px;
  }
  .back {
    display: grid;
    place-items: center;
    width: 26px;
    height: 26px;
    padding: 0;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
  }
  .back:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .crumbs {
    flex: 1;
    display: flex;
    align-items: center;
    gap: 4px;
    min-width: 0;
    color: var(--fg-dim);
  }
  .here {
    color: var(--fg);
    font-size: 15px;
    font-weight: 600;
  }
  .crumb {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    padding: 0;
    border: 0;
    background: transparent;
    color: var(--fg-dim);
    font-size: var(--fs-sm);
    cursor: pointer;
  }
  .crumb:hover {
    color: var(--accent);
  }
  .sub-help {
    margin-bottom: 8px;
    font-size: var(--fs-sm);
  }
  .results {
    display: flex;
    flex-direction: column;
  }
  .rhead {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 6px;
  }
  .links {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
    margin-bottom: 10px;
  }
  .lnk {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    height: 26px;
    padding: 0 10px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-1);
    color: var(--fg);
    font-size: var(--fs-sm);
  }
  .lnk:hover {
    border-color: var(--accent-line);
  }
  .rgroup {
    margin-bottom: 14px;
  }
  .rgroup .crumb {
    margin-top: 6px;
    font-size: var(--fs-xs);
    font-weight: 600;
    letter-spacing: 0.02em;
  }
  .link {
    padding: 0;
    border: 0;
    background: transparent;
    color: var(--accent);
    font-size: inherit;
    cursor: pointer;
  }
  .unknown {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto auto;
    gap: 10px;
    align-items: center;
    padding: 5px 0;
    border-bottom: 1px solid var(--border);
    font-size: var(--fs-sm);
  }
  .none {
    padding: 4px 0;
  }
  .json {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 8px;
    min-height: 0;
  }
  .json-head,
  .json-foot {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .editor {
    flex: 1;
    min-height: 240px;
    padding: 10px 12px;
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    color: var(--fg);
    font-size: 12.5px;
    line-height: 1.55;
    resize: none;
    outline: none;
    tab-size: 2;
    white-space: pre;
    scrollbar-width: thin;
  }
  .editor:focus {
    border-color: var(--accent-line);
  }
  .bad,
  .good {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: var(--fs-sm);
  }
  .bad {
    color: var(--err);
  }
  .good {
    color: var(--ok);
  }
  .about {
    font-size: var(--fs);
    gap: 8px 18px;
  }
  .about dd {
    overflow-wrap: anywhere;
    white-space: normal;
  }
  /* a narrow window: the search stays on top, the pages become a list to pick from */
  @media (max-width: 700px) {
    .layout {
      flex-direction: column;
      height: calc(80vh - 46px);
    }
    .nav {
      width: auto;
      flex-direction: row;
      flex-wrap: wrap;
      align-items: center;
      border-right: 0;
      border-bottom: 1px solid var(--border);
      overflow: visible;
    }
    .search {
      flex: 1;
      min-width: 160px;
    }
    .pages {
      display: none;
    }
    .narrow {
      display: block;
      margin-bottom: 8px;
    }
    .narrow select {
      width: 100%;
    }
    .content {
      padding: 10px 12px 14px;
    }
  }
</style>
