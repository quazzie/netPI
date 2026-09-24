<script>
  import Modal from './Modal.svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import IconButton from '../../lib/kit/IconButton.svelte';
  import TimeAgo from '../../lib/kit/TimeAgo.svelte';
  import {
    app,
    newSession,
    openSession,
    createProject,
    updateProject,
    deleteProject,
    setSessionProject,
    noteProject,
  } from '../../lib/state/app.svelte.js';
  import { confirmDialog, toast } from '../../lib/state/ui.svelte.js';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { pickFolder } from '../../lib/folderPicker.js';
  import { basename } from '../../lib/format.js';
  import { desktop } from '../../lib/kit/host.js';

  /**
   * Projects dialog. data: { view: 'list' | 'new' | 'edit', id?, sessionId?, select? }
   * - list: every project, with new session / edit / remove
   * - new: create a project; opened from a picker it is then attached to `sessionId`, or with `select` it becomes the
   *   project new sessions start in
   * - edit: one project (`id`): name and folder, its sessions, the instruction files that apply to its folder
   */
  let { data, onclose } = $props();

  // read the props once: onclose() unmounts the dialog and `data` is gone afterwards
  // svelte-ignore state_referenced_locally
  const opened = { view: data?.view ?? 'list', id: data?.id ?? null, sessionId: data?.sessionId ?? null, select: !!data?.select };
  const MAX_SESSIONS = 6;

  let view = $state(opened.view);
  let fromList = $state(opened.view === 'list'); // Back / Cancel return to the list instead of closing
  let editId = $state(opened.id);
  let q = $state('');
  let busy = $state(false);
  let form = $state({ name: '', path: '', create: false });
  let files = $state(null); // instruction files of the edited project; null while loading
  let filesError = $state('');

  const project = $derived(editId ? (app.projectsById.get(editId) ?? null) : null);
  const dirty = $derived(!!project && (form.name.trim() !== project.name || form.path.trim() !== project.path));
  const title = $derived(view === 'new' ? 'New project' : view === 'edit' ? (project?.name ?? 'Project') : 'Projects');

  const counts = $derived.by(() => {
    const m = new Map();
    for (const s of app.sessions) if (s.projectId && !s.archived && !s.parentSessionId) m.set(s.projectId, (m.get(s.projectId) ?? 0) + 1);
    return m;
  });
  const list = $derived.by(() => {
    const query = q.trim().toLowerCase();
    return app.projects
      .filter((p) => !query || p.name.toLowerCase().includes(query) || p.path.toLowerCase().includes(query))
      .sort((a, b) => (Date.parse(b.lastUsedAt ?? b.updatedAt) || 0) - (Date.parse(a.lastUsedAt ?? a.updatedAt) || 0));
  });
  const sessions = $derived(editId ? app.sessions.filter((s) => s.projectId === editId && !s.archived && !s.parentSessionId) : []);

  if (opened.view === 'edit') {
    const p = app.projectsById.get(opened.id);
    if (p) showEdit(p);
  } else if (opened.view === 'new') showNew();

  function showList() {
    view = 'list';
    editId = null;
    fromList = true;
  }
  function showNew() {
    form = { name: '', path: '', create: false };
    view = 'new';
  }
  function showEdit(p) {
    editId = p.id;
    form = { name: p.name, path: p.path, create: false };
    view = 'edit';
    loadFiles(p.id);
  }
  function back() {
    if (fromList) showList();
    else onclose();
  }

  async function loadFiles(id) {
    files = null;
    filesError = '';
    try {
      const res = await rpc('agentsmd.list', { projectId: id });
      if (id === editId) files = Array.isArray(res) ? res : [];
    } catch (e) {
      if (id === editId) filesError = e.message;
    }
  }

  async function browse() {
    const p = await pickFolder({ initial: form.path.trim() || app.info?.home || null, title: 'Project folder' });
    if (!p) return;
    form.path = p;
    if (view === 'new' && !form.name.trim()) form.name = basename(p);
  }

  async function create(e) {
    e?.preventDefault();
    const dir = form.path.trim();
    if (!dir) return toast('Choose a folder', 'warn');
    busy = true;
    try {
      const p = await createProject({ name: form.name.trim() || basename(dir), path: dir, ...(form.create ? { create: true } : {}) });
      toast(`Project “${p.name}” added`);
      if (opened.sessionId) {
        onclose();
        await setSessionProject(opened.sessionId, p.id);
      } else if (opened.select) {
        onclose();
        noteProject(p.id);
      } else if (fromList) showList();
      else onclose();
    } catch (err) {
      toast(err.message, 'error');
    } finally {
      busy = false;
    }
  }

  async function save(e) {
    e?.preventDefault();
    if (!project || !dirty) return;
    busy = true;
    try {
      const moved = form.path.trim() !== project.path;
      const p = await updateProject({ id: project.id, name: form.name.trim() || project.name, path: form.path.trim() || project.path });
      form = { name: p.name, path: p.path, create: false };
      toast('Project saved');
      if (moved) loadFiles(p.id);
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
      toast(`Project “${p.name}” removed`);
      if (view === 'edit') back();
    } catch (err) {
      toast(err.message, 'error');
    }
  }

  function startSession(p) {
    onclose();
    newSession({ projectId: p.id });
  }
  function open(s) {
    onclose();
    openSession(s.id);
  }

  function focus(node) {
    node.focus();
  }
</script>

{#snippet newFoot()}
  <button class="np-btn np-btn-ghost" onclick={back}>{fromList ? 'Back' : 'Cancel'}</button>
  <button class="np-btn np-btn-primary" type="submit" form="project-form" disabled={busy || !form.path.trim()}>Create project</button>
{/snippet}

{#snippet editFoot()}
  <button class="np-btn np-btn-danger remove" onclick={() => remove(project)}><Icon name="trash" size={13} /> Remove</button>
  <button class="np-btn np-btn-ghost" onclick={back}>{fromList ? 'Back' : 'Close'}</button>
  <button class="np-btn np-btn-primary" type="submit" form="project-form" disabled={busy || !dirty}>Save</button>
{/snippet}

<Modal
  {title}
  width={520}
  class="projects-dialog"
  {onclose}
  footer={view === 'new' ? newFoot : view === 'edit' && project ? editFoot : undefined}
>
  {#if view !== 'list' && fromList}
    <button class="np-btn np-btn-ghost np-btn-sm backlink" onclick={showList}><Icon name="arrow-left" size={12} /> All projects</button>
  {/if}

  {#if view === 'list'}
    <div class="lhead">
      <div class="search">
        <Icon name="search" size={13} />
        <input placeholder="Filter projects" bind:value={q} spellcheck="false" use:focus />
      </div>
      <button class="np-btn np-btn-primary" onclick={showNew}><Icon name="plus" size={14} /> New project</button>
    </div>
    <div class="plist">
      {#each list as p (p.id)}
        <div class="prow">
          <button class="pmain" title="Edit {p.name}" onclick={() => showEdit(p)}>
            <Icon name="folder" size={15} />
            <span class="ptext">
              <span class="pname"
                ><span class="np-ellipsis">{p.name}</span><span class="pcount"
                  >{counts.get(p.id) ?? 0} {counts.get(p.id) === 1 ? 'session' : 'sessions'}</span
                ></span
              >
              <span class="ppath np-mono" title={p.path}><bdi>{p.path}</bdi></span>
            </span>
          </button>
          <div class="pacts">
            <IconButton icon="plus" size="sm" title="New session in {p.name}" onclick={() => startSession(p)} />
            <IconButton icon="pencil" size="sm" title="Edit" onclick={() => showEdit(p)} />
            <IconButton icon="trash" size="sm" title="Remove" onclick={() => remove(p)} />
          </div>
        </div>
      {:else}
        <div class="np-empty">
          <Icon name="folder" size={22} />
          {q ? 'No matching projects' : 'No projects yet. A project is a name and a folder the agent works in.'}
        </div>
      {/each}
    </div>
  {:else if view === 'new'}
    <form id="project-form" class="fields" onsubmit={create}>
      <label class="field">
        <span>Folder</span>
        <div class="pathrow">
          <input class="np-input np-mono" placeholder="C:\src\my-project" bind:value={form.path} spellcheck="false" use:focus />
          <button type="button" class="np-btn" onclick={browse}><Icon name="folder-open" size={14} /> Browse…</button>
        </div>
      </label>
      <label class="field">
        <span>Name</span>
        <input class="np-input" placeholder={form.path.trim() ? basename(form.path.trim()) : 'my-project'} bind:value={form.name} />
      </label>
      <label class="np-check np-small np-muted"><input type="checkbox" bind:checked={form.create} /> Create the folder if it doesn’t exist</label>
      {#if opened.sessionId}
        <p class="hint">The current session moves into the new project.</p>
      {:else if opened.select}
        <p class="hint">New sessions will start in it.</p>
      {/if}
    </form>
  {:else if project}
    <form id="project-form" class="fields" onsubmit={save}>
      <label class="field">
        <span>Name</span>
        <input class="np-input" bind:value={form.name} use:focus />
      </label>
      <label class="field">
        <span>Folder</span>
        <div class="pathrow">
          <input class="np-input np-mono" bind:value={form.path} spellcheck="false" />
          <button type="button" class="np-btn" title="Choose another folder" onclick={browse}><Icon name="folder-open" size={14} /> Browse…</button>
          {#if desktop.available}
            <IconButton icon="external" title="Show in Explorer" onclick={() => desktop.revealPath(project.path)} />
          {/if}
        </div>
        <span class="hint">Sessions in this project follow the folder; their agents are told at their next turn.</span>
      </label>
    </form>

    <section>
      <div class="np-section-title">
        Sessions <span class="np-section-count">{sessions.length}</span>
        <span class="np-section-actions">
          <button class="np-btn np-btn-sm" onclick={() => startSession(project)}><Icon name="plus" size={12} /> New session</button>
        </span>
      </div>
      {#each sessions.slice(0, MAX_SESSIONS) as s (s.id)}
        <button class="sess" onclick={() => open(s)}>
          <Icon name="sessions" size={13} />
          <span class="np-ellipsis sname">{s.title || 'New session'}</span>
          <TimeAgo time={s.updatedAt} class="np-dim np-small" />
        </button>
      {:else}
        <p class="hint">No sessions yet.</p>
      {/each}
      {#if sessions.length > MAX_SESSIONS}
        <p class="hint">{sessions.length - MAX_SESSIONS} more in the Sessions panel.</p>
      {/if}
    </section>

    <section>
      <div class="np-section-title">
        Instruction files {#if files}<span class="np-section-count">{files.length}</span>{/if}
      </div>
      {#if filesError}
        <p class="hint">Unavailable: {filesError}</p>
      {:else if !files}
        <p class="hint">Loading…</p>
      {:else if !files.length}
        <p class="hint">No AGENTS.md or CLAUDE.md applies to this folder.</p>
      {:else}
        {#each files as f (f.path)}
          <div class="frow">
            <span class="scope" data-s={f.scope}>{f.scope}</span>
            <span class="fpath np-mono" title={f.path}><bdi>{f.path}</bdi></span>
            {#if desktop.available}
              <IconButton icon="external" size="sm" title="Show in Explorer" onclick={() => desktop.revealPath(f.path)} />
            {/if}
          </div>
        {/each}
        <p class="hint">Agents get these as notices; edits are announced at their next turn.</p>
      {/if}
    </section>
  {:else}
    <div class="np-empty">
      <Icon name="folder" size={22} />
      This project no longer exists.
    </div>
  {/if}
</Modal>

<style>
  .backlink {
    margin: -6px 0 10px -6px;
  }
  .lhead {
    display: flex;
    gap: 8px;
    margin-bottom: 10px;
  }
  .search {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 6px;
    height: 28px;
    padding: 0 8px;
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-sm);
    background: var(--bg);
    color: var(--fg-dim);
  }
  .search:focus-within {
    border-color: var(--accent-line);
  }
  .search input {
    flex: 1;
    min-width: 0;
    border: 0;
    outline: none;
    background: transparent;
    color: var(--fg);
  }
  .plist {
    display: flex;
    flex-direction: column;
    gap: 2px;
    margin: 0 -6px;
  }
  .prow {
    display: flex;
    align-items: center;
    gap: 4px;
    padding-right: 4px;
    border-radius: 6px;
  }
  .prow:hover,
  .prow:focus-within {
    background: var(--bg-2);
  }
  .pmain {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: flex-start;
    gap: 9px;
    padding: 7px 6px;
    border: 0;
    background: transparent;
    color: var(--fg-dim);
    text-align: left;
  }
  .ptext {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
  }
  .pname {
    display: flex;
    align-items: baseline;
    gap: 8px;
    min-width: 0;
    color: var(--fg);
  }
  .pcount {
    flex: none;
    margin-left: auto;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
  }
  .ppath,
  .fpath {
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    direction: rtl;
    text-align: left;
    font-size: 11.5px;
    color: var(--fg-dim);
  }
  .pacts {
    flex: none;
    display: flex;
    gap: 1px;
  }
  .fields {
    display: flex;
    flex-direction: column;
    gap: 12px;
  }
  .field {
    display: flex;
    flex-direction: column;
    gap: 4px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .pathrow {
    display: flex;
    gap: 6px;
  }
  .pathrow .np-btn {
    flex: none;
  }
  .hint {
    margin: 0;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    line-height: 1.45;
  }
  section {
    margin-top: 20px;
  }
  .sess {
    display: flex;
    align-items: center;
    gap: 9px;
    width: calc(100% + 12px);
    height: 30px;
    margin: 0 -6px;
    padding: 0 6px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    text-align: left;
  }
  .sess:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .sname {
    flex: 1;
    color: var(--fg);
  }
  .frow {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 26px;
  }
  .fpath {
    flex: 1;
    min-width: 0;
  }
  .scope {
    flex: none;
    padding: 0 6px;
    border-radius: 8px;
    background: var(--bg-3);
    color: var(--fg-muted);
    font-size: 10px;
    line-height: 16px;
  }
  .scope[data-s='project'] {
    background: var(--accent-soft);
    color: var(--accent);
  }
  .remove {
    margin-right: auto;
  }
</style>
