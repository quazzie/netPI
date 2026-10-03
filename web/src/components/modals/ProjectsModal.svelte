<script>
  import Modal from './Modal.svelte';
  import Icon from '../../lib/kit/Icon.svelte';
  import ProjectsListView from './ProjectsListView.svelte';
  import ProjectsNewView from './ProjectsNewView.svelte';
  import ProjectsEditView from './ProjectsEditView.svelte';
  import {
    app,
    newSession,
    openSession,
    createProject,
    updateProject,
    setSessionProject,
    noteProject,
  } from '../../lib/state/app.svelte.js';
  import { toast } from '../../lib/state/ui.svelte.js';
  import { pickFolder } from '../../lib/folderPicker.js';
  import { basename } from '../../lib/format.js';
  import { removeProject } from '../../lib/projects.js';

  /**
   * Projects dialog. data: { view: 'list' | 'new' | 'edit', id?, sessionId?, select? }
   * - list: every project, with new session / edit / remove
   * - new: create a project; opened from a picker it is then attached to `sessionId`, or with `select` a new
   *   session starts in it
   * - edit: one project (`id`): name and folder, its sessions, the instruction files and skills that apply to its folder
   *
   * The three views are components of their own (idea-g26991); this is what they share: which view is open, the form
   * behind the footers, and the three actions (create, save, remove) they call.
   */
  let { data, onclose } = $props();

  // read the props once: onclose() unmounts the dialog and `data` is gone afterwards
  // svelte-ignore state_referenced_locally
  const opened = { view: data?.view ?? 'list', id: data?.id ?? null, sessionId: data?.sessionId ?? null, select: !!data?.select };

  let view = $state(opened.view);
  let fromList = $state(opened.view === 'list'); // Back / Cancel return to the list instead of closing
  let editId = $state(opened.id);
  let busy = $state(false);
  let form = $state({ name: '', path: '', create: false });

  const project = $derived(editId ? (app.projectsById.get(editId) ?? null) : null);
  const dirty = $derived(!!project && (form.name.trim() !== project.name || form.path.trim() !== project.path));
  const title = $derived(view === 'new' ? 'New project' : view === 'edit' ? (project?.name ?? 'Project') : 'Projects');
  const newHint = $derived(opened.sessionId ? 'The current session moves into the new project.' : opened.select ? 'A new session starts in it.' : '');

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
  }
  function back() {
    if (fromList) showList();
    else onclose();
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
        newSession({ projectId: p.id });
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
      const p = await updateProject({ id: project.id, name: form.name.trim() || project.name, path: form.path.trim() || project.path });
      form = { name: p.name, path: p.path, create: false }; // what the host kept, so a name it fixed is not still "unsaved"
      toast('Project saved');
    } catch (err) {
      toast(err.message, 'error');
    } finally {
      busy = false;
    }
  }

  async function remove(p) {
    // the confirm, the delete and what it says are the shared flow (lib/projects.js), in the panel too
    if (await removeProject(p) && view === 'edit') back();
  }

  function startSession(p) {
    onclose();
    newSession({ projectId: p.id });
  }
  function open(s) {
    onclose();
    openSession(s.id);
  }

  async function setProfile(value) {
    try {
      await updateProject({ id: project.id, meta: { profile: value || null } });
    } catch (err) {
      toast(err.message, 'error');
    }
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
    <ProjectsListView onnew={showNew} onedit={showEdit} onremove={remove} onstart={startSession} />
  {:else if view === 'new'}
    <ProjectsNewView bind:form hint={newHint} onsubmit={create} onbrowse={browse} />
  {:else if project}
    <ProjectsEditView
      {project}
      bind:form
      onsubmit={save}
      {onbrowse}
      onprofile={setProfile}
      {onstart}
      onopen={open}
    />
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
  .remove {
    margin-right: auto;
  }
</style>
