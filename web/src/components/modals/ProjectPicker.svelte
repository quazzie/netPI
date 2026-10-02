<script>
  import Icon from '../../lib/kit/Icon.svelte';
  import Popover from '../Popover.svelte';
  import { app, setSessionProject, noteProject, newSession } from '../../lib/state/app.svelte.js';
  import { toast, openProjects } from '../../lib/state/ui.svelte.js';

  /**
   * Project picker.
   * data: { sessionId, anchor }: attach the chosen project to that session (sessions.setProject).
   * data: { select: true, anchor }: start a new session in the picked project (the start screen's chevron).
   */
  let { data, onclose } = $props();
  let q = $state('');

  const select = $derived(!!data?.select);
  const session = $derived(data?.sessionId ? app.sessionsById.get(data.sessionId) : null);
  const current = $derived(
    select ? (app.lastProjectId && app.projectsById.has(app.lastProjectId) ? app.lastProjectId : null) : (session?.projectId ?? null),
  );
  const currentProject = $derived(current ? (app.projectsById.get(current) ?? null) : null);
  const items = $derived.by(() => {
    const query = q.trim().toLowerCase();
    const list = app.projects
      .filter((p) => !query || p.name.toLowerCase().includes(query) || p.path.toLowerCase().includes(query))
      .sort((a, b) => (Date.parse(b.lastUsedAt ?? b.updatedAt) || 0) - (Date.parse(a.lastUsedAt ?? a.updatedAt) || 0));
    return [{ id: null, name: 'No project', path: 'default workspace' }, ...list];
  });
  // the keyboard highlight starts on the current project and goes back to the top when the filter changes
  // svelte-ignore state_referenced_locally
  let index = $state(Math.max(0, items.findIndex((p) => p.id === current)));

  async function choose(p) {
    // read everything from props BEFORE closing: onclose() unmounts this popover and `data` becomes null
    const selecting = select;
    const sessionId = data?.sessionId;
    const was = current;
    onclose();
    if (selecting) {
      noteProject(p.id);   // keep the start screen's hint current
      newSession({ projectId: p.id ?? null });
      return;
    }
    if (!sessionId || was === p.id) return;
    const s = await setSessionProject(sessionId, p.id);
    if (s) toast(p.id ? `Project: ${p.name}` : 'Project detached');
  }

  /** Open the projects dialog; a project created there is attached to this session or selected for new sessions. */
  function manage(view, id = null) {
    const context = select ? { select: true } : data?.sessionId ? { sessionId: data.sessionId } : {};
    onclose();
    openProjects(view === 'new' ? { view, ...context } : { view, id });
  }

  function onKey(e) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      index = Math.min(items.length - 1, index + 1);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      index = Math.max(0, index - 1);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (items[index]) choose(items[index]);
    }
  }
  function focus(n) {
    n.focus();
  }
</script>

<Popover
  anchor={data?.anchor}
  placement={select ? 'bottom-start' : data?.anchor?.closest?.('[data-composer]') ? 'top-start' : 'bottom-end'}
  width={320}
  {onclose}
>
  <div class="head">
    <Icon name="search" size={13} />
    <input placeholder={select ? 'Start a session in…' : 'Attach project…'} bind:value={q} oninput={() => (index = 0)} onkeydown={onKey} use:focus spellcheck="false" />
  </div>
  <div class="list np-scroll">
    {#each items as p, i (p.id ?? '__none')}
      <button class="item" class:active={i === index} class:current={current === p.id} onclick={() => choose(p)} onmouseenter={() => (index = i)}>
        <Icon name={p.id ? 'folder' : 'x'} size={14} />
        <span class="main">
          <span class="np-ellipsis name">{p.name}</span>
          <span class="np-ellipsis path np-mono">{p.path}</span>
        </span>
        {#if current === p.id}<Icon name="check" size={13} />{/if}
      </button>
    {/each}
  </div>
  <div class="foot">
    {#if currentProject}
      <button class="manage" onclick={() => manage('edit', currentProject.id)}>
        <Icon name="pencil" size={13} /> <span class="np-ellipsis">Edit “{currentProject.name}”…</span>
      </button>
    {/if}
    <button class="manage" onclick={() => manage('new')}><Icon name="plus" size={13} /> New project…</button>
    <button class="manage" onclick={() => manage('list')}><Icon name="settings" size={13} /> Manage projects…</button>
  </div>
</Popover>

<style>
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 8px 10px;
    border-bottom: 1px solid var(--border);
    color: var(--fg-dim);
  }
  .head input {
    flex: 1;
    border: 0;
    outline: none;
    background: transparent;
    color: var(--fg);
  }
  .list {
    padding: 4px;
    max-height: 320px;
  }
  .item {
    display: flex;
    align-items: center;
    gap: 9px;
    width: 100%;
    padding: 5px 8px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    text-align: left;
  }
  .item.active {
    background: var(--bg-3);
  }
  .item.current {
    color: var(--accent);
  }
  .main {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    line-height: 1.3;
  }
  .name {
    color: var(--fg);
  }
  .path {
    font-size: 10.5px;
    color: var(--fg-dim);
  }
  .foot {
    display: flex;
    flex-direction: column;
    padding: 4px;
    border-top: 1px solid var(--border);
  }
  .manage {
    display: flex;
    align-items: center;
    gap: 7px;
    min-width: 0;
    height: 28px;
    padding: 0 8px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    text-align: left;
  }
  .manage:hover {
    color: var(--fg);
    background: var(--bg-2);
  }
</style>
