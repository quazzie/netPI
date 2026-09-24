<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import IconButton from '../../lib/kit/IconButton.svelte';
  import { app, newSession, setSessionProject, deleteProject } from '../../lib/state/app.svelte.js';
  import { confirmDialog, toast, openProjects } from '../../lib/state/ui.svelte.js';

  let { visible = true } = $props();

  let q = $state('');

  const counts = $derived.by(() => {
    const m = new Map();
    for (const s of app.sessions) if (s.projectId && !s.archived && !s.parentSessionId) m.set(s.projectId, (m.get(s.projectId) ?? 0) + 1);
    return m;
  });

  const list = $derived.by(() => {
    const query = q.trim().toLowerCase();
    const arr = query
      ? app.projects.filter((p) => p.name.toLowerCase().includes(query) || p.path.toLowerCase().includes(query))
      : app.projects.slice();
    return arr.sort((a, b) => (Date.parse(b.lastUsedAt ?? b.updatedAt) || 0) - (Date.parse(a.lastUsedAt ?? a.updatedAt) || 0));
  });

  // creating and editing happen in the projects dialog
  const add = () => openProjects({ view: 'new' });
  const edit = (p) => openProjects({ view: 'edit', id: p.id });

  async function remove(p) {
    const ok = await confirmDialog({
      title: 'Remove project?',
      message: `“${p.name}” will be removed from NetPI. Files in ${p.path} are not touched; its sessions are kept.`,
      confirmLabel: 'Remove',
      danger: true,
    });
    if (!ok) return;
    try {
      await deleteProject(p.id);
    } catch (err) {
      toast(err.message, 'error');
    }
  }

  async function attach(p) {
    if (!app.activeId) return toast('No active session', 'warn');
    const s = await setSessionProject(app.activeId, p.id);
    if (s) toast(`Attached “${p.name}” to this session`);
  }
</script>

<div class="tab-root">
  <div class="head">
    <div class="search">
      <Icon name="search" size={13} />
      <input class="search-input" placeholder="Filter projects" bind:value={q} spellcheck="false" />
    </div>
    <IconButton icon="plus" title="Add project" onclick={add} />
  </div>

  <div class="list np-scroll">
    {#each list as p (p.id)}
      {@const isActive = app.activeSession?.projectId === p.id}
      <div class="prow" class:active={isActive}>
        <button class="pmain" title="{p.path} (click to edit)" onclick={() => edit(p)}>
          <span class="picon"><Icon name={isActive ? 'folder-open' : 'folder'} size={15} /></span>
          <span class="ptext">
            <span class="pname">
              <span class="np-ellipsis">{p.name}</span>
              {#if counts.get(p.id)}<span class="pcount">{counts.get(p.id)}</span>{/if}
            </span>
            <span class="ppath np-mono"><bdi>{p.path}</bdi></span>
          </span>
        </button>
        <div class="actions">
          <IconButton icon="plus" size="sm" title="New session here" onclick={() => newSession({ projectId: p.id })} />
          <IconButton
            icon="link"
            size="sm"
            title="Attach to current session"
            disabled={!app.activeId || isActive}
            onclick={() => attach(p)}
          />
          <IconButton icon="pencil" size="sm" title="Edit" onclick={() => edit(p)} />
          <IconButton icon="trash" size="sm" title="Remove" onclick={() => remove(p)} />
        </div>
      </div>
    {:else}
      <div class="np-empty">
        <Icon name="folder" size={22} />
        {q ? 'No matching projects' : 'No projects yet. A project is a name and a folder the agent works in.'}
        {#if !q}<button class="np-btn np-btn-sm" onclick={add}>Add project</button>{/if}
      </div>
    {/each}
  </div>
</div>

<style>
  .tab-root {
    display: flex;
    flex-direction: column;
    min-height: 0;
    flex: 1;
    container-type: inline-size;
  }
  .head {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 8px 8px 6px 10px;
  }
  .search {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 6px;
    height: 28px;
    padding: 0 8px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg);
    color: var(--fg-dim);
  }
  .search:focus-within {
    border-color: var(--accent-line);
  }
  .search-input {
    flex: 1;
    min-width: 0;
    border: 0;
    outline: none;
    background: transparent;
    color: var(--fg);
  }
  .search-input::placeholder {
    color: var(--fg-dim);
  }
  .list {
    flex: 1;
    min-height: 0;
    padding: 2px 6px 8px;
  }
  .prow {
    position: relative;
    display: flex;
    align-items: flex-start;
    gap: 8px;
    padding: 7px 8px;
    border-radius: 6px;
  }
  .prow:hover {
    background: var(--bg-2);
  }
  .prow.active .picon {
    color: var(--accent);
  }
  .picon {
    color: var(--fg-dim);
    padding-top: 1px;
  }
  .pmain {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: flex-start;
    gap: 8px;
    padding: 0;
    border: 0;
    background: transparent;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
  }
  .ptext {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
  }
  .pname {
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
  }
  .pcount {
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
  }
  .ppath {
    display: block;
    font-size: 11.5px;
    color: var(--fg-dim);
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    direction: rtl;
    text-align: left;
  }
  .actions {
    position: absolute;
    right: 4px;
    top: 5px;
    display: flex;
    gap: 1px;
    padding: 1px;
    border-radius: 5px;
    background: var(--bg-2);
    box-shadow: -8px 0 8px var(--bg-2);
    opacity: 0;
    pointer-events: none;
    transition: opacity var(--t-fast);
  }
  .prow:hover .actions,
  .prow:focus-within .actions {
    opacity: 1;
    pointer-events: auto;
  }
  .prow:focus-within {
    background: var(--bg-2);
  }
</style>
