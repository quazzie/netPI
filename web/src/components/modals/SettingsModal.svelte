<script>
  import Modal from './Modal.svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import { prefs, savePrefs, toast } from '../../lib/state/ui.svelte.js';
  import { app } from '../../lib/state/app.svelte.js';
  import { rpc, conn } from '../../lib/rpc.svelte.js';

  let { onclose } = $props();

  let section = $state('general');
  let raw = $state('');
  let original = $state('');
  let path = $state('');
  let loading = $state(false);
  let saving = $state(false);
  let loadError = $state('');

  async function load() {
    loading = true;
    loadError = '';
    try {
      const res = await rpc('settings.get');
      path = res?.path ?? '';
      raw = original = JSON.stringify(res?.settings ?? {}, null, 2);
    } catch (e) {
      loadError = e.message;
    } finally {
      loading = false;
    }
  }
  $effect(() => {
    if (section === 'json' && !original && !loading && !loadError) load();
  });

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

  async function save() {
    if (parseError) return;
    saving = true;
    try {
      await rpc('settings.replace', { settings: JSON.parse(raw) });
      original = raw = JSON.stringify(JSON.parse(raw), null, 2);
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

  function setPref(k, v) {
    prefs[k] = v;
    savePrefs();
  }

  // desktop app: the WebView zoom (the desktop shell remembers it in window.json)
  let zoom = $state(null);
  $effect(() => {
    if (!app.info?.desktop) return;
    rpc('desktop.zoom', {})
      .then((r) => (zoom = r?.factor ?? 1))
      .catch(() => {});
  });
  async function setZoom(factor) {
    const f = Math.round(Math.min(3, Math.max(0.5, factor)) * 100) / 100;
    try {
      zoom = (await rpc('desktop.zoom', { factor: f }))?.factor ?? f;
    } catch (e) {
      toast(`Zoom failed: ${e.message}`, 'error');
    }
  }

  // settings.json changed elsewhere (another client, an editor, a plugin): show it unless there are unsaved edits here
  let seenVersion = app.settingsVersion;
  $effect(() => {
    const v = app.settingsVersion;
    if (v === seenVersion) return;
    seenVersion = v;
    if (original && raw === original && !saving && !loading) load();
  });
</script>

<Modal title="Settings" width={760} padded={false} {onclose}>
  <div class="layout">
    <nav class="nav">
      <button class:active={section === 'general'} onclick={() => (section = 'general')}><Icon name="sliders" size={14} /> General</button>
      <button class:active={section === 'json'} onclick={() => (section = 'json')}><Icon name="file-text" size={14} /> settings.json</button>
      <button class:active={section === 'about'} onclick={() => (section = 'about')}><Icon name="info" size={14} /> About</button>
    </nav>
    <div class="content">
      {#if section === 'general'}
        <div class="group">
          <div class="row">
            <div class="lbl"><div>Theme</div><div class="np-dim np-small">Dark, light or follow the system</div></div>
            <div class="np-seg">
              {#each [['dark', 'Dark'], ['light', 'Light'], ['system', 'System']] as [v, l] (v)}
                <button aria-pressed={prefs.theme === v} onclick={() => setPref('theme', v)}>{l}</button>
              {/each}
            </div>
          </div>
          <div class="row">
            <div class="lbl"><div>Send with</div><div class="np-dim np-small">The other combination inserts a newline (Shift+Enter always does)</div></div>
            <div class="np-seg">
              <button aria-pressed={prefs.enterSends} onclick={() => setPref('enterSends', true)}>Enter</button>
              <button aria-pressed={!prefs.enterSends} onclick={() => setPref('enterSends', false)}>Ctrl+Enter</button>
            </div>
          </div>
          <div class="row">
            <div class="lbl">
              <div>Steps</div>
              <div class="np-dim np-small">
                Thinking and tool calls. <b>Fold when done</b>: groups of more than 3 steps fold into “N steps · time” when the
                run ends. <b>Folded</b>: they fold into one line from the start, which keeps the chat still while the agent works.
              </div>
            </div>
            <div class="np-seg">
              {#each [['open', 'Expanded'], ['done', 'Fold when done'], ['folded', 'Folded']] as [v, l] (v)}
                <button aria-pressed={prefs.steps === v} onclick={() => setPref('steps', v)}>{l}</button>
              {/each}
            </div>
          </div>
          <label class="row check">
            <div class="lbl"><div>Expand thinking</div><div class="np-dim np-small">Show reasoning blocks expanded by default, also while they stream</div></div>
            <input type="checkbox" checked={prefs.expandThinking} onchange={(e) => setPref('expandThinking', e.currentTarget.checked)} />
          </label>
          <div class="row">
            <div class="lbl"><div>Chat width</div><div class="np-dim np-small">How wide messages get on a large window</div></div>
            <div class="np-seg">
              {#each [['normal', 'Normal'], ['wide', 'Wide'], ['full', 'Full']] as [v, l] (v)}
                <button aria-pressed={prefs.chatWidth === v} onclick={() => setPref('chatWidth', v)}>{l}</button>
              {/each}
            </div>
          </div>
          <div class="row">
            <div class="lbl">
              <div>Zoom</div>
              <div class="np-dim np-small">
                {#if app.info?.desktop}Also Ctrl + mouse wheel, Ctrl + / − / 0; NetPI remembers it{:else}Use the browser's zoom (Ctrl + / −); it is remembered per site{/if}
              </div>
            </div>
            {#if app.info?.desktop}
              <div class="np-seg zoom">
                <button title="Smaller" onclick={() => setZoom(zoom - 0.1)} disabled={zoom == null}>−</button>
                <button title="Reset to 100%" onclick={() => setZoom(1)} disabled={zoom == null}>{zoom == null ? '…' : `${Math.round(zoom * 100)}%`}</button>
                <button title="Larger" onclick={() => setZoom(zoom + 0.1)} disabled={zoom == null}>+</button>
              </div>
            {/if}
          </div>
          <label class="row check">
            <div class="lbl"><div>Spellcheck</div><div class="np-dim np-small">Underline misspelled words in the message box</div></div>
            <input type="checkbox" checked={prefs.spellcheck} onchange={(e) => setPref('spellcheck', e.currentTarget.checked)} />
          </label>
        </div>
      {:else if section === 'json'}
        <div class="json">
          <div class="json-head">
            <span class="np-mono np-dim np-small np-ellipsis" title={path}>{path || 'settings.json'}</span>
            <span class="np-spacer"></span>
            <button class="np-btn np-btn-sm np-btn-ghost" onclick={load} disabled={loading}><Icon name="refresh" size={12} /> Reload</button>
          </div>
          {#if loadError}
            <div class="np-empty">Could not load settings: {loadError}</div>
          {:else}
            <textarea class="editor np-mono" bind:value={raw} spellcheck="false" onkeydown={onEditorKey} disabled={loading}></textarea>
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
      {:else}
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
      {/if}
    </div>
  </div>
</Modal>

<style>
  .layout {
    display: flex;
    min-height: 420px;
    height: 60vh;
  }
  .nav {
    width: 170px;
    flex: none;
    padding: 10px 8px;
    border-right: 1px solid var(--border);
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .nav button {
    display: flex;
    align-items: center;
    gap: 8px;
    height: 30px;
    padding: 0 10px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    text-align: left;
  }
  .nav button:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .nav button.active {
    background: var(--bg-3);
    color: var(--fg);
  }
  .content {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    padding: 16px 18px;
    overflow: auto;
  }
  .group {
    display: flex;
    flex-direction: column;
  }
  .row {
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 12px 0;
    border-bottom: 1px solid var(--border);
  }
  .row:last-child {
    border-bottom: 0;
  }
  .row.check {
    cursor: pointer;
  }
  .row.check input {
    accent-color: var(--accent);
    width: 16px;
    height: 16px;
  }
  .zoom button {
    min-width: 34px;
    font-variant-numeric: tabular-nums;
  }
  .lbl {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 2px;
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
</style>
