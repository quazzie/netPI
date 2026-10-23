<script>
  /**
   * Every tool, by plugin, with the two settings that act on all chats: Off (`tools.disabled`: hidden from every chat)
   * and Deferred (`tools.deferred`: registered and callable, but kept out of requests until a chat calls it by name or
   * finds it with tool_search). Reads tools.exposure; Save writes the deferred choice as "*" plus exceptions, so a tool
   * that comes later is deferred as with the built-in default, and keeps names in tools.disabled that no loaded plugin
   * registers. A chat's own switches (the composer's tools button) and a profile's lists come on top of these.
   */
  import Modal from '../../lib/kit/Modal.svelte';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { getAt, setSetting } from '../../lib/settings.js';
  import { deferredList } from '../../lib/settingsModel.js';
  import SearchInput from '../../lib/kit/SearchInput.svelte';

  let { doc, onclose } = $props();

  let data = $state(null);
  let names = $state({});
  let error = $state('');
  let off = $state(new Set());
  let deferred = $state(new Set());
  let touchedDeferred = $state(false);
  let saving = $state(false);
  let q = $state('');

  async function load() {
    try {
      const [x, plugins] = await Promise.all([rpc('tools.exposure'), rpc('plugins.list').catch(() => [])]);
      names = Object.fromEntries((plugins ?? []).map((p) => [p.id, p.name]));
      data = x;
      off = new Set(x.tools.filter((t) => t.disabled).map((t) => t.name));
      deferred = new Set(x.tools.filter((t) => t.deferred && !t.byDefinition).map((t) => t.name));
      touchedDeferred = false;
    } catch (e) {
      error = e.message;
    }
  }
  $effect(() => {
    load();
  });

  const groups = $derived.by(() => {
    const by = new Map();
    const query = q.trim().toLowerCase();
    for (const t of data?.tools ?? []) {
      if (query && !`${t.name} ${t.label ?? ''} ${t.description ?? ''}`.toLowerCase().includes(query)) continue;
      const k = t.pluginId ?? 'host';
      if (!by.has(k)) by.set(k, []);
      by.get(k).push(t);
    }
    return [...by].map(([id, tools]) => ({ id, name: names[id] ?? id, tools })).sort((a, b) => a.name.localeCompare(b.name));
  });

  function toggle(set, name, on) {
    const next = new Set(set);
    if (on) next.add(name);
    else next.delete(name);
    return next;
  }
  const firstLine = (s) => String(s ?? '').split('\n')[0].replace(/\s+/g, ' ').slice(0, 140);

  const counts = $derived({ off: off.size, deferred: deferred.size, all: data?.tools.length ?? 0 });

  async function save() {
    saving = true;
    try {
      const registered = new Set((data?.tools ?? []).map((t) => t.name));
      const kept = (getAt(doc, 'tools.disabled') ?? []).filter((n) => !registered.has(n));
      const disabled = [...kept, ...[...off].sort()];
      const before = JSON.stringify(getAt(doc, 'tools.disabled') ?? []);
      if (JSON.stringify(disabled) !== before && !(await setSetting('tools.disabled', disabled.length ? disabled : null))) return;
      if (touchedDeferred) {
        const all = (data?.tools ?? []).filter((t) => !t.byDefinition).map((t) => t.name);
        if (!(await setSetting('tools.deferred', deferredList(all, deferred)))) return;
      }
      onclose();
    } finally {
      saving = false;
    }
  }
  async function useDefault() {
    if (await setSetting('tools.deferred', null)) await load();
  }
</script>

<Modal title="Tool exposure" width={680} {onclose} class="section-dialog">
  <div class="help np-dim">
    <b>Off</b> hides a tool from every chat. <b>Deferred</b> keeps it out of each request until a chat calls it by name or finds it with
    tool_search, which keeps requests small. Each chat can still switch its own tools (the tools button next to the model), and a
    profile can bring its own deferred list.
  </div>
  {#if error}
    <div class="np-empty">Could not load the tools: {error}</div>
  {:else if !data}
    <div class="np-empty">Loading…</div>
  {:else}
    <div class="bar">
      <SearchInput bind:value={q} placeholder="Filter tools" />
      <span class="np-dim np-small">{counts.all} tools · {counts.off} off · {counts.deferred} deferred</span>
    </div>
    <div class="state np-dim np-small">
      {#if data.deferredSetting}Deferred: your own list in <span class="np-mono">tools.deferred</span>.{:else}Deferred: the built-in default ({data.defaultDeferred.length} tools).{/if}
    </div>
    <div class="list" data-testid="tool-exposure">
      {#each groups as g (g.id)}
        <div class="plugin">{g.name}</div>
        {#each g.tools as t (t.name)}
          <div class="tool" class:isoff={off.has(t.name)} data-tool={t.name}>
            <div class="t">
              <span class="np-mono name">{t.name}</span>
              {#if t.description}<span class="np-dim desc np-ellipsis" title={t.description}>{firstLine(t.description)}</span>{/if}
            </div>
            <label class="ck" title="Hidden from every chat">
              <input type="checkbox" checked={off.has(t.name)} onchange={(e) => (off = toggle(off, t.name, e.currentTarget.checked))} aria-label="{t.name} off" />
              Off
            </label>
            <label class="ck" title={t.byDefinition ? 'Deferred by its server: pin it in the MCP tab to declare it' : t.infrastructure ? 'Reaches the deferred tools: never deferred' : 'Kept out of requests until a chat uses it'}>
              <input
                type="checkbox"
                checked={t.byDefinition || deferred.has(t.name)}
                disabled={t.byDefinition || t.infrastructure}
                onchange={(e) => {
                  deferred = toggle(deferred, t.name, e.currentTarget.checked);
                  touchedDeferred = true;
                }}
                aria-label="{t.name} deferred"
              />
              Deferred
            </label>
          </div>
        {/each}
      {:else}
        <div class="np-empty">No tool matches.</div>
      {/each}
    </div>
  {/if}
  {#snippet footer()}
    {#if data?.deferredSetting}<button class="np-btn np-btn-ghost np-btn-sm" onclick={useDefault}>Default deferred list</button>{/if}
    <span class="np-spacer"></span>
    <button class="np-btn np-btn-ghost np-btn-sm" onclick={onclose}>Cancel</button>
    <button class="np-btn np-btn-primary np-btn-sm" disabled={!data || saving} onclick={save} data-testid="save-exposure">{saving ? 'Saving…' : 'Save'}</button>
  {/snippet}
</Modal>

<style>
  .help {
    margin-bottom: 10px;
    font-size: var(--fs-sm);
  }
  .bar {
    display: flex;
    align-items: center;
    gap: 10px;
    margin-bottom: 4px;
  }
  .bar :global(.np-search) {
    flex: 1;
  }
  .state {
    margin-bottom: 8px;
  }
  .plugin {
    margin: 12px 0 2px;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
    font-weight: 600;
    letter-spacing: 0.04em;
    text-transform: uppercase;
  }
  .tool {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto auto;
    gap: 12px;
    align-items: center;
    padding: 5px 0;
    border-bottom: 1px solid var(--border);
  }
  .t {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }
  .name {
    font-size: var(--fs-sm);
  }
  .isoff .name {
    color: var(--fg-dim);
    text-decoration: line-through;
  }
  .desc {
    font-size: var(--fs-xs);
  }
  .ck {
    display: flex;
    align-items: center;
    gap: 4px;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
    cursor: pointer;
  }
  .ck input {
    accent-color: var(--accent);
  }
</style>
