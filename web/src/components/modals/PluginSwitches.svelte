<script>
  /**
   * Every plugin with an on/off switch (plugins.setEnabled) and the tools it brings. Single tools are switched per chat
   * (the composer's Tools button); tools.disabled (settings.json) still hides tools everywhere and is shown when set.
   */
  import { rpc } from '../../lib/rpc.svelte.js';
  import { bus } from '../../lib/bus.js';
  import { getAt, setSetting } from '../../lib/settings.js';
  import { confirmDialog, toast } from '../../lib/state/ui.svelte.js';

  let { doc } = $props();
  let tools = $state([]);
  let plugins = $state([]);

  async function load() {
    try {
      [tools, plugins] = await Promise.all([rpc('tools.list'), rpc('plugins.list')]);
    } catch {}
  }
  $effect(() => {
    load();
    return bus.on('plugins.changed', load);
  });

  const hidden = $derived(getAt(doc, 'tools.disabled') ?? []);
  const toolsOf = $derived.by(() => {
    const by = new Map();
    for (const t of tools ?? []) {
      if (!by.has(t.pluginId)) by.set(t.pluginId, new Set());
      by.get(t.pluginId).add(t.name);
    }
    return (id) => [...(by.get(id) ?? [])].sort();
  });

  async function unhide(name) {
    const next = hidden.filter((n) => n !== name);
    await setSetting('tools.disabled', next.length ? next : null);
  }

  async function togglePlugin(p, on) {
    if (!on) {
      const ok = await confirmDialog({
        title: `Disable ${p.name}?`,
        message: 'Its tools, tabs and settings go away until you enable it again.',
        confirmLabel: 'Disable',
        danger: true,
      });
      if (!ok) return load();
    }
    try {
      await rpc('plugins.setEnabled', { id: p.id, enabled: on });
    } catch (e) {
      toast(e.message, 'error');
    }
    load();
  }
</script>

<div class="block">
  <div class="title">Plugins</div>
  <div class="help np-dim">
    A plugin brings tools, tabs and settings; switched off, all of it goes. To leave out single tools, use the tools button
    next to the model in a chat.
  </div>
  {#if hidden.length}
    <div class="hidden">
      <span class="np-dim">Hidden from every chat by <span class="np-mono">tools.disabled</span>:</span>
      {#each hidden as name (name)}
        <button class="hid np-mono" title="Show {name} again" onclick={() => unhide(name)}>{name} ×</button>
      {/each}
    </div>
  {/if}
  {#each plugins as p (p.id)}
    {@const own = toolsOf(p.id)}
    <label class="plugin" class:off={!p.enabled} data-plugin={p.id}>
      <div class="pl">
        <div class="pname">{p.name} <span class="np-dim np-mono state">{p.state}</span></div>
        {#if p.description}<div class="np-dim desc">{p.description}</div>{/if}
        {#if own.length}<div class="ptools np-mono">{own.join(' · ')}</div>{/if}
        {#if p.error}<div class="err">{p.error}</div>{/if}
      </div>
      <input type="checkbox" checked={p.enabled} onchange={(e) => togglePlugin(p, e.currentTarget.checked)} aria-label="Enable {p.name}" />
    </label>
  {/each}
</div>

<style>
  .block {
    margin-bottom: 16px;
  }
  .title {
    margin: 4px 0 2px;
    font-weight: 600;
  }
  .help {
    margin-bottom: 8px;
    font-size: var(--fs-sm);
  }
  .hidden {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px 6px;
    margin-bottom: 8px;
    font-size: var(--fs-xs);
  }
  .hid {
    height: 20px;
    padding: 0 6px;
    border: 1px solid var(--border);
    border-radius: 10px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
  }
  .hid:hover {
    color: var(--fg);
    border-color: var(--fg-dim);
  }
  .plugin {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 7px 0;
    border-bottom: 1px solid var(--border);
    cursor: pointer;
  }
  .plugin input {
    accent-color: var(--accent);
  }
  .plugin.off .pname {
    color: var(--fg-dim);
  }
  .pl {
    flex: 1;
    min-width: 0;
  }
  .state {
    margin-left: 4px;
    font-size: var(--fs-xs);
  }
  .desc {
    font-size: var(--fs-xs);
  }
  .ptools {
    margin-top: 2px;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
    overflow-wrap: anywhere;
  }
  .err {
    color: var(--err);
    font-size: var(--fs-xs);
  }
</style>
