<script>
  /**
   * Profiles (settings profiles.<id> = { name, prompt, toolsOff }, profiles.defaultProfile): the opening of a chat's
   * system prompt and its tools, switched on and off here. A new chat gets its project's default profile (Projects
   * dialog) or the one chosen here.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { bus } from '../../lib/bus.js';
  import { setSetting } from '../../lib/settings.js';
  import { confirmDialog } from '../../lib/state/ui.svelte.js';

  let { doc } = $props();

  const RESERVED = new Set(['defaultProfile', 'none']);
  const profiles = $derived(Object.entries(doc?.profiles ?? {}).filter(([id, v]) => !RESERVED.has(id) && v && typeof v === 'object'));
  const defaultId = $derived(typeof doc?.profiles?.defaultProfile === 'string' ? doc.profiles.defaultProfile : '');

  // the tools a profile can switch: every active tool once (tools.disabled and disabled plugins are not offered)
  let tools = $state([]);
  async function loadTools() {
    try {
      tools = (await rpc('tools.list')) ?? [];
    } catch {}
  }
  $effect(() => {
    loadTools();
    return bus.on('plugins.changed', loadTools);
  });
  const categories = $derived.by(() => {
    const seen = new Map();
    for (const t of tools) if (t.active && !t.disabled && !seen.has(t.name)) seen.set(t.name, t);
    const byCat = new Map();
    for (const t of [...seen.values()].sort((a, b) => a.name.localeCompare(b.name))) {
      const c = t.category || 'general';
      if (!byCat.has(c)) byCat.set(c, []);
      byCat.get(c).push(t);
    }
    return [...byCat].sort(([a], [b]) => a.localeCompare(b));
  });
  const toolCount = $derived(categories.reduce((n, [, list]) => n + list.length, 0));
  const offCount = (p) => categories.reduce((n, [, list]) => n + list.filter((t) => (p.toolsOff ?? []).includes(t.name)).length, 0);

  let expanded = $state(null); // the profile whose tools are shown
  let newName = $state('');

  function slug(name) {
    return (
      name
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '')
        .slice(0, 24) || 'profile'
    );
  }
  async function add() {
    const name = newName.trim();
    if (!name) return;
    const base = slug(name);
    let id = base;
    for (let n = 2; doc?.profiles?.[id] || RESERVED.has(id); n++) id = `${base}-${n}`;
    if (await setSetting(`profiles.${id}`, { name })) {
      newName = '';
      expanded = id;
    }
  }
  async function remove(id, name) {
    const ok = await confirmDialog({
      title: `Remove the profile "${name}"?`,
      message: 'Chats that have it keep their instructions and tools; new chats no longer get it.',
      confirmLabel: 'Remove',
      danger: true,
    });
    if (!ok) return;
    await setSetting(`profiles.${id}`, null);
    if (defaultId === id) await setSetting('profiles.defaultProfile', null);
  }
  async function toggleTool(id, p, name, on) {
    const off = new Set(p.toolsOff ?? []);
    if (on) off.delete(name);
    else off.add(name);
    await setSetting(`profiles.${id}.toolsOff`, off.size ? [...off].sort() : null);
  }
  async function setCategory(id, p, list, on) {
    const off = new Set(p.toolsOff ?? []);
    for (const t of list) {
      if (on) off.delete(t.name);
      else off.add(t.name);
    }
    await setSetting(`profiles.${id}.toolsOff`, off.size ? [...off].sort() : null);
  }
</script>

<div class="profiles">
  <label class="default">
    <span>New chats start with</span>
    <select class="np-input" value={defaultId} onchange={(e) => setSetting('profiles.defaultProfile', e.currentTarget.value || null)} aria-label="Default profile">
      <option value="">no profile</option>
      {#each profiles as [id, p] (id)}<option value={id}>{p.name || id}</option>{/each}
    </select>
    <span class="np-dim">unless their project has its own (Projects dialog).</span>
  </label>

  {#each profiles as [id, p] (id)}
    {@const off = new Set(p.toolsOff ?? [])}
    {@const offN = offCount(p)}
    <div class="profile" data-profile={id}>
      <div class="head">
        <input class="np-input name" value={p.name ?? ''} placeholder={id} aria-label="Name" onchange={(e) => setSetting(`profiles.${id}.name`, e.currentTarget.value.trim() || null)} />
        {#if defaultId === id}<span class="badge">default</span>{/if}
        <button class="icon" title="Remove the profile" onclick={() => remove(id, p.name || id)}><Icon name="trash" size={13} /></button>
      </div>
      <textarea
        class="np-input prompt"
        rows="4"
        value={p.prompt ?? ''}
        placeholder="Who the agent is and how it works, e.g. “You are a system administrator for the hosts in ~/.ssh/config …”. It replaces the opening of the system prompt; empty keeps the built-in one."
        aria-label="Instructions"
        onchange={(e) => setSetting(`profiles.${id}.prompt`, e.currentTarget.value.trim() || null)}
      ></textarea>
      <button class="tools-toggle" aria-expanded={expanded === id} onclick={() => (expanded = expanded === id ? null : id)}>
        <Icon name={expanded === id ? 'chevron-down' : 'chevron-right'} size={12} />
        <Icon name="wrench" size={12} /> Tools: {toolCount - offN} of {toolCount} on
      </button>
      {#if expanded === id}
        <div class="tools">
          {#each categories as [cat, list] (cat)}
            {@const allOn = list.every((t) => !off.has(t.name))}
            <div class="cat">
              <span class="np-dim">{cat}</span>
              <button class="all" onclick={() => setCategory(id, p, list, !allOn)} title={allOn ? `Switch off every ${cat} tool` : `Switch on every ${cat} tool`}
                >{allOn ? 'all off' : 'all on'}</button
              >
            </div>
            <div class="chips">
              {#each list as t (t.name)}
                <label class="chip" class:is-off={off.has(t.name)} title={t.description}>
                  <input type="checkbox" checked={!off.has(t.name)} onchange={(e) => toggleTool(id, p, t.name, e.currentTarget.checked)} />
                  <span class="np-mono">{t.name}</span>
                </label>
              {/each}
            </div>
          {/each}
        </div>
      {/if}
    </div>
  {:else}
    <div class="empty np-dim">
      No profiles yet. A profile is a name, the instructions that open the system prompt and the tools a chat gets; give
      one to a project and its new chats start with it.
    </div>
  {/each}

  <div class="add">
    <input class="np-input" placeholder="Name of a new profile, e.g. Admin" bind:value={newName} onkeydown={(e) => e.key === 'Enter' && add()} aria-label="New profile name" />
    <button class="np-btn np-btn-sm" disabled={!newName.trim()} onclick={add}><Icon name="plus" size={12} /> Add profile</button>
  </div>
</div>

<style>
  .profiles {
    display: flex;
    flex-direction: column;
    gap: 10px;
    margin-bottom: 12px;
  }
  .default {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px 8px;
    font-size: var(--fs-sm);
  }
  .default select {
    width: auto;
    min-width: 140px;
  }
  .empty {
    font-size: var(--fs-sm);
  }
  .profile {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 10px 12px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-1);
  }
  .head {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .name {
    flex: 1;
    min-width: 0;
    font-weight: 600;
  }
  .badge {
    flex: none;
    padding: 1px 6px;
    border-radius: 8px;
    background: var(--accent-soft);
    color: var(--accent);
    font-size: var(--fs-xs);
  }
  .prompt {
    width: 100%;
    height: auto;
    padding: 6px 8px;
    line-height: 1.45;
    resize: vertical;
  }
  .tools-toggle {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    align-self: flex-start;
    padding: 2px 4px;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
  }
  .tools-toggle:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .cat {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 6px 0 3px;
    font-size: var(--fs-xs);
  }
  .all {
    padding: 0 4px;
    border: 0;
    background: transparent;
    color: var(--accent);
    font-size: var(--fs-xs);
  }
  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
  }
  .chip {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    height: 22px;
    padding: 0 7px 0 5px;
    border: 1px solid var(--border);
    border-radius: 11px;
    font-size: var(--fs-xs);
    cursor: pointer;
  }
  .chip.is-off {
    opacity: 0.55;
    text-decoration: line-through;
  }
  .chip input {
    margin: 0;
    accent-color: var(--accent);
  }
  .icon {
    display: grid;
    place-items: center;
    flex: none;
    width: 26px;
    height: 26px;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--fg-dim);
  }
  .icon:hover {
    background: var(--bg-2);
    color: var(--err);
  }
  .add {
    display: flex;
    gap: 8px;
  }
  .add input {
    flex: 1;
    min-width: 0;
  }
</style>
