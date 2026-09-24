<script>
  /**
   * Profiles (settings profiles.<id> = { name, prompt, toolsOff }, profiles.defaultProfile): one row each (name, how its
   * instructions start, its tools), a dialog to edit one (ProfileDialog), the profile new chats start with, and "Add
   * profile". A new chat gets its project's default profile (Projects dialog) or the one chosen here.
   * opening: the current opening of the system prompt (context.customPrompt or the built-in text).
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import SettingsRow from './SettingsRow.svelte';
  import ProfileDialog from './ProfileDialog.svelte';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { bus } from '../../lib/bus.js';
  import { setSetting } from '../../lib/settings.js';
  import { firstLine } from '../../lib/format.js';

  let { doc, opening = '' } = $props();

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
  const onCount = (p) => toolCount - categories.reduce((n, [, list]) => n + list.filter((t) => (p.toolsOff ?? []).includes(t.name)).length, 0);

  let openId = $state(null);
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
      openId = id;
    }
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
    {@const on = onCount(p)}
    <SettingsRow
      title={p.name || id}
      icon="user"
      subtitle={firstLine(p.prompt?.trim() || opening)}
      badges={[defaultId === id ? { text: 'default', tone: 'accent' } : null, { text: on === toolCount ? 'all tools' : `${on} of ${toolCount} tools` }]}
      onclick={() => (openId = id)}
      data-profile={id}
    />
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

{#if openId}
  <ProfileDialog id={openId} profile={doc?.profiles?.[openId]} {categories} {opening} isDefault={defaultId === openId} onclose={() => (openId = null)} />
{/if}

<style>
  .profiles {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin-bottom: 12px;
  }
  .default {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px 8px;
    margin-bottom: 6px;
    font-size: var(--fs-sm);
  }
  .default select {
    width: auto;
    min-width: 140px;
  }
  .empty {
    font-size: var(--fs-sm);
  }
  .add {
    display: flex;
    gap: 8px;
    margin-top: 4px;
  }
  .add input {
    flex: 1;
    min-width: 0;
  }
</style>
