<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import IconButton from '../../lib/kit/IconButton.svelte';
  import {
    app,
    newSession,
    setSessionProject,
    createProject,
    updateProject,
    deleteProject,
  } from '../../lib/state/app.svelte.js';
  import { confirmDialog, toast } from '../../lib/state/ui.svelte.js';
  import { pickFolder } from '../../lib/folderPicker.js';
  import { basename } from '../../lib/format.js';

  let { visible = true } = $props();

  let adding = $state(false);
  let form = $state({ name: '', path: '', create: false });
  let editing = $state(null); // project id
  let edit = $state({ name: '', path: '' });
  let busy = $state(false);
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

  function startAdd() {
    adding = true;
    form = { name: '', path: '', create: false };
  }

  async function browse(target) {
    const p = await pickFolder({ initial: target.path || app.info?.home || null, title: 'Project folder' });
    if (!p) return;
    target.path = p;
    if (!target.name) target.name = basename(p);
  }

  async function submitAdd(e) {
    e?.preventDefault();
    if (!form.path.trim()) return toast('Choose a folder', 'warn');
    busy = true;
    try {
      const p = await createProject({
        name: form.name.trim() || basename(form.path.trim()),
        path: form.path.trim(),
        ...(form.create ? { create: true } : {}),
      });
      adding = false;
      toast(`Project “${p.name}” added`, 'info');
    } catch (err) {
      toast(err.message, 'error');
    } finally {
      busy = false;
    }
  }

  function startEdit(p) {
    editing = p.id;
    edit = { name: p.name, path: p.path };
  }
  async function submitEdit(e, p) {
    e?.preventDefault();
    busy = true;
    try {
      await updateProject({ id: p.id, name: edit.name.trim() || p.name, path: edit.path.trim() || p.path });
      editing = null;
    } catch (err) {
      toast(err.message, 'error');
    } finally {
      busy = false;
    }
  }

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

  function focus(node) {
    node.focus();
  }
</script>

<div class="tab-root">
  <div class="head">
    <div class="search">
      <Icon name="search" size={13} />
      <input class="search-input" placeholder="Filter projects" bind:value={q} spellcheck="false" />
    </div>
    <IconButton icon="plus" title="Add project" onclick={startAdd} pressed={adding} />
  </div>

  {#if adding}
    <form class="card" onsubmit={submitAdd}>
      <div class="card-title">New project</div>
      <label class="field">
        <span>Folder</span>
        <div class="pathrow">
          <input class="np-input np-mono" placeholder="C:\src\my-project" bind:value={form.path} use:focus />
          <button type="button" class="np-btn" title="Browse…" onclick={() => browse(form)}><Icon name="folder-open" size={14} /></button>
        </div>
      </label>
      <label class="field">
        <span>Name</span>
        <input class="np-input" placeholder={form.path ? basename(form.path) : 'my-project'} bind:value={form.name} />
      </label>
      <label class="np-check np-small np-muted"><input type="checkbox" bind:checked={form.create} /> Create the folder if it doesn’t exist</label>
      <div class="btns">
        <button type="button" class="np-btn np-btn-ghost" onclick={() => (adding = false)}>Cancel</button>
        <button type="submit" class="np-btn np-btn-primary" disabled={busy || !form.path.trim()}>Add project</button>
      </div>
    </form>
  {/if}

  <div class="list np-scroll">
    {#each list as p (p.id)}
      {@const isActive = app.activeSession?.projectId === p.id}
      {#if editing === p.id}
        <form class="card" onsubmit={(e) => submitEdit(e, p)}>
          <label class="field"><span>Name</span><input class="np-input" bind:value={edit.name} use:focus /></label>
          <label class="field">
            <span>Folder</span>
            <div class="pathrow">
              <input class="np-input np-mono" bind:value={edit.path} />
              <button type="button" class="np-btn" title="Browse…" onclick={() => browse(edit)}><Icon name="folder-open" size={14} /></button>
            </div>
          </label>
          <div class="btns">
            <button type="button" class="np-btn np-btn-ghost" onclick={() => (editing = null)}>Cancel</button>
            <button type="submit" class="np-btn np-btn-primary" disabled={busy}>Save</button>
          </div>
        </form>
      {:else}
        <div class="prow" class:active={isActive}>
          <span class="picon"><Icon name={isActive ? 'folder-open' : 'folder'} size={15} /></span>
          <div class="pmain">
            <div class="pname">
              <span class="np-ellipsis">{p.name}</span>
              {#if counts.get(p.id)}<span class="pcount">{counts.get(p.id)}</span>{/if}
            </div>
            <div class="ppath np-mono" title={p.path}><bdi>{p.path}</bdi></div>
          </div>
          <div class="actions">
            <IconButton icon="plus" size="sm" title="New session here" onclick={() => newSession({ projectId: p.id })} />
            <IconButton
              icon="link"
              size="sm"
              title="Attach to current session"
              disabled={!app.activeId || isActive}
              onclick={() => attach(p)}
            />
            <IconButton icon="rename" size="sm" title="Edit" onclick={() => startEdit(p)} />
            <IconButton icon="trash" size="sm" title="Remove" onclick={() => remove(p)} />
          </div>
        </div>
      {/if}
    {:else}
      <div class="np-empty">
        <Icon name="folder" size={22} />
        {q ? 'No matching projects' : 'No projects yet. A project is a name and a folder the agent works in.'}
        {#if !q && !adding}<button class="np-btn np-btn-sm" onclick={startAdd}>Add project</button>{/if}
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
  .card {
    display: flex;
    flex-direction: column;
    gap: 8px;
    margin: 4px 8px 8px;
    padding: 10px;
    border: 1px solid var(--border-strong);
    border-radius: var(--radius);
    background: var(--bg-2);
  }
  .list .card {
    margin: 4px 0;
  }
  .card-title {
    font-weight: 600;
    font-size: var(--fs-sm);
  }
  .field {
    display: flex;
    flex-direction: column;
    gap: 3px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .pathrow {
    display: flex;
    gap: 4px;
  }
  .pathrow .np-btn {
    padding: 0 7px;
  }
  .btns {
    display: flex;
    justify-content: flex-end;
    gap: 6px;
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
