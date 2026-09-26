<script>
  import { onMount } from 'svelte';
  import { Icon, IconButton, SearchInput, Menu, Empty, Button, basename } from '@netpi/kit';
  import IdeaCard from './IdeaCard.svelte';
  import NewIdea from './NewIdea.svelte';
  import { STATUSES, ACTIVE, STATUS_TONE, matches } from './model.js';

  let { ctx } = $props();

  let list = $state.raw(null); // ideas.list result (the single global file)
  let projects = $state.raw([]); // projects.list
  let error = $state('');
  let loading = $state(true);
  let q = $state('');
  let statusFilter = $state('active'); // active | all | <status>
  let tagFilter = $state.raw(new Set());
  let adding = $state(false);
  let expanded = $state.raw(new Set());
  let dragId = $state(null);
  let dropTarget = $state(null); // { id, after }
  let visible = true;
  let dirty = false;
  // The project filter: 'all' | 'global' | a project id. It follows the active project until the user picks one.
  let activeProjectId = $state(ctx.app.activeProject?.id ?? null);
  let projectFilter = $state(activeProjectId ?? 'global');
  let followsActive = true;

  async function load() {
    try {
      [list, projects] = await Promise.all([ctx.rpc('ideas.list', {}), ctx.rpc('projects.list', {}).catch(() => [])]);
      error = '';
    } catch (e) {
      error = e?.message ?? String(e);
      list = null;
    } finally {
      loading = false;
      dirty = false;
    }
  }

  export function setVisible(v) {
    visible = v;
    if (v && dirty) load();
  }

  onMount(() => {
    load();
    const offChange = ctx.app.onChange(() => {
      const pid = ctx.app.activeProject?.id ?? null;
      if (pid !== activeProjectId) {
        activeProjectId = pid;
        if (followsActive) projectFilter = pid ?? 'global'; // the default follows the active project
      }
    });
    const offEv = ctx.on('ideas.changed', () => {
      if (visible) load();
      else dirty = true;
    });
    return () => {
      offChange();
      offEv();
    };
  });

  // ------------------------------------------------------------------ filtering
  const ideas = $derived(list?.ideas ?? []);
  const effProject = (i) => i.project?.id ?? null;
  const counts = $derived.by(() => {
    const c = { all: ideas.length, active: 0 };
    for (const s of STATUSES) c[s] = 0;
    for (const i of ideas) {
      c[i.status] = (c[i.status] ?? 0) + 1;
      if (ACTIVE.has(i.status)) c.active++;
    }
    return c;
  });
  const allTags = $derived.by(() => {
    const m = new Map();
    for (const i of ideas) for (const t of i.tags ?? []) m.set(t, (m.get(t) ?? 0) + 1);
    return [...m].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).map(([t, n]) => ({ t, n }));
  });
  const shown = $derived(
    ideas.filter(
      (i) =>
        (projectFilter === 'all' || (projectFilter === 'global' ? !effProject(i) : effProject(i) === projectFilter)) &&
        (statusFilter === 'all' || (statusFilter === 'active' ? ACTIVE.has(i.status) : i.status === statusFilter)) &&
        (!tagFilter.size || (i.tags ?? []).some((t) => tagFilter.has(t))) &&
        matches(i, q.trim()),
    ),
  );
  const statusLabel = $derived(statusFilter === 'active' ? 'Active' : statusFilter === 'all' ? 'All' : statusFilter);
  const projectLabel = $derived(
    projectFilter === 'all' ? 'All projects' : projectFilter === 'global' ? 'Global' : (projects.find((p) => p.id === projectFilter)?.name ?? projectFilter),
  );

  function toggleTag(t) {
    const s = new Set(tagFilter);
    s.has(t) ? s.delete(t) : s.add(t);
    tagFilter = s;
  }
  function toggleExpanded(id) {
    const s = new Set(expanded);
    s.has(id) ? s.delete(id) : s.add(id);
    expanded = s;
  }

  // ------------------------------------------------------------------ mutations
  function replaceIdea(idea) {
    if (!list || !idea) return;
    list = { ...list, exists: true, ideas: list.ideas.map((i) => (i.id === idea.id ? idea : i)) };
  }

  async function call(method, params, okMsg) {
    try {
      const r = await ctx.rpc(method, params);
      if (okMsg) ctx.app.toast(okMsg);
      return r;
    } catch (e) {
      ctx.app.toast(`${method}: ${e.message}`, 'error');
      return null;
    }
  }

  const api = {
    update: async (id, patch) => {
      const r = await call('ideas.update', { id, patch });
      if (r) replaceIdea(r);
      return r;
    },
    remove: async (id) => {
      const r = await call('ideas.delete', { id });
      if (r && list) list = { ...list, ideas: list.ideas.filter((i) => i.id !== id) };
      return r;
    },
    toPrompt: async (id) => {
      const text = await call('ideas.toPrompt', { id });
      if (typeof text === 'string') {
        ctx.app.insertText(text);
        ctx.app.toast('Idea inserted into the composer — edit and send');
      }
    },
    move: (id, delta) => {
      const ids = ideas.map((i) => i.id);
      const from = ids.indexOf(id);
      // move past the neighbour that is visible under the current filter
      const visibleIds = shown.map((i) => i.id);
      const vi = visibleIds.indexOf(id);
      const neighbour = visibleIds[vi + delta];
      if (from < 0 || !neighbour) return;
      ids.splice(from, 1);
      const to = ids.indexOf(neighbour) + (delta > 0 ? 1 : 0);
      ids.splice(to, 0, id);
      reorder(ids);
    },
  };

  async function add(idea, projectId) {
    const params = { idea, prepend: true, sessionId: ctx.app.activeSessionId || undefined };
    if (projectId) params.projectId = projectId; // '' → omitted: the session's project is the default stamp
    const r = await call('ideas.add', params);
    if (r) {
      list = { ...list, exists: true, ideas: [r, ...(list?.ideas ?? [])] };
      adding = false;
      expanded = new Set([r.id]);
    }
    return r;
  }

  async function reorder(ids) {
    if (!list) return;
    const byId = new Map(list.ideas.map((i) => [i.id, i]));
    list = { ...list, ideas: ids.map((id) => byId.get(id)).filter(Boolean) };
    await call('ideas.reorder', { ids });
  }

  // ------------------------------------------------------------------ drag & drop
  function onDragStart(e, id) {
    dragId = id;
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', id);
  }
  function onDragOver(e, id) {
    if (!dragId || dragId === id) return;
    e.preventDefault();
    const r = e.currentTarget.getBoundingClientRect();
    dropTarget = { id, after: e.clientY > r.top + r.height / 2 };
  }
  function onDrop(e) {
    e.preventDefault();
    if (!dragId || !dropTarget) return end();
    const ids = ideas.map((i) => i.id).filter((x) => x !== dragId);
    const at = ids.indexOf(dropTarget.id) + (dropTarget.after ? 1 : 0);
    ids.splice(at, 0, dragId);
    reorder(ids);
    end();
  }
  function end() {
    dragId = null;
    dropTarget = null;
  }

  const tagItems = $derived([
    ...(tagFilter.size ? [{ label: 'Clear tags', icon: 'x', onclick: () => (tagFilter = new Set()) }, { divider: true }] : []),
    ...allTags.map(({ t, n }) => ({ label: `#${t}`, hint: String(n), checked: tagFilter.has(t), keepOpen: true, onclick: () => toggleTag(t) })),
  ]);
  const statusItems = $derived([
    { label: `Active`, hint: String(counts.active), checked: statusFilter === 'active', onclick: () => (statusFilter = 'active') },
    { label: `All`, hint: String(counts.all), checked: statusFilter === 'all', onclick: () => (statusFilter = 'all') },
    { divider: true },
    ...STATUSES.map((s) => ({ label: s, hint: String(counts[s] ?? 0), checked: statusFilter === s, onclick: () => (statusFilter = s) })),
  ]);
  const projectItems = $derived([
    ...(activeProjectId
      ? [{ label: `This project — ${ctx.app.activeProject?.name ?? ''}`, checked: projectFilter === activeProjectId, onclick: () => { projectFilter = activeProjectId; followsActive = true; } }]
      : []),
    { label: 'All projects', checked: projectFilter === 'all', onclick: () => { projectFilter = 'all'; followsActive = false; } },
    { label: 'Global (unbound)', checked: projectFilter === 'global', onclick: () => { projectFilter = 'global'; followsActive = false; } },
    ...(projects.length
      ? [
          { divider: true },
          ...projects.filter((p) => p.id !== activeProjectId).map((p) => ({ label: p.name, checked: projectFilter === p.id, onclick: () => { projectFilter = p.id; followsActive = false; } })),
        ]
      : []),
  ]);
  const emptyWhere = $derived(
    projectFilter === 'all' ? 'all projects' : projectFilter === 'global' ? 'the global backlog' : `“${projectLabel}”`,
  );
</script>

<div class="ideas">
  <div class="scope np-line">
    <Menu items={projectItems} minWidth={190}>
      {#snippet trigger({ toggle, open })}
        <button
          class="badge"
          class:global={projectFilter === 'global' && !activeProjectId}
          aria-expanded={open}
          onclick={toggle}
          title="Project filter">
          <Icon name={projectFilter === 'global' ? 'globe' : 'folder'} size={12} />
          <span class="np-ellipsis">{projectLabel}</span>
          <Icon name="chevron-down" size={11} />
        </button>
      {/snippet}
    </Menu>
    <span class="file np-mono np-grow" title={list?.file}><bdi>{list ? list.file : ''}</bdi></span>
    <IconButton icon="plus" title="New idea" size="sm" pressed={adding} onclick={() => (adding = !adding)} />
  </div>

  <div class="filters np-line">
    <SearchInput bind:value={q} placeholder="Search ideas" class="np-grow" />
    <Menu items={statusItems} minWidth={170}>
      {#snippet trigger({ toggle, open })}
        <button class="np-chip fchip" aria-pressed={statusFilter !== 'active'} aria-expanded={open} onclick={toggle} title="Status filter">
          <span class="cap">{statusLabel}</span>
          <span class="n">{counts[statusFilter] ?? 0}</span>
          <Icon name="chevron-down" size={11} />
        </button>
      {/snippet}
    </Menu>
    {#if allTags.length}
      <Menu items={tagItems} minWidth={160}>
        {#snippet trigger({ toggle, open })}
          <button class="np-chip fchip" aria-pressed={tagFilter.size > 0} aria-expanded={open} onclick={toggle} title="Filter by tag">
            #{#if tagFilter.size}<span class="n">{tagFilter.size}</span>{/if}
            <Icon name="chevron-down" size={11} />
          </button>
        {/snippet}
      </Menu>
    {/if}
  </div>
  {#if tagFilter.size}
    <div class="tags">
      {#each [...tagFilter] as t (t)}
        <button class="np-chip" aria-pressed="true" title="Remove this tag filter" onclick={() => toggleTag(t)}>#{t}<Icon name="x" size={10} /></button>
      {/each}
    </div>
  {/if}

  {#if adding}
    <NewIdea onadd={add} oncancel={() => (adding = false)} projects={projects} activeProjectId={activeProjectId ?? ''} />
  {/if}

  <div class="list">
    {#if loading && !list}
      <Empty><span class="np-spinner"></span></Empty>
    {:else if error}
      <Empty icon="alert">
        <div>{error}</div>
        <Button size="sm" icon="refresh" onclick={load}>Retry</Button>
      </Empty>
    {:else if !ideas.length}
      <Empty icon="idea">
        <div>No ideas yet in {emptyWhere}.</div>
        <div class="np-dim">Agents add them with the <span class="np-mono">ideas</span> tool; you can use <span class="np-mono">/idea &lt;title&gt;</span> or the + button.</div>
        {#if !adding}<Button size="sm" icon="plus" onclick={() => (adding = true)}>New idea</Button>{/if}
      </Empty>
    {:else if !shown.length}
      <Empty icon="search">No ideas match the filters</Empty>
    {:else}
      {#each shown as idea, i (idea.id)}
        <IdeaCard
          {idea}
          {api}
          open={expanded.has(idea.id)}
          ontoggle={() => toggleExpanded(idea.id)}
          canUp={i > 0}
          canDown={i < shown.length - 1}
          dragging={dragId === idea.id}
          drop={dropTarget?.id === idea.id ? (dropTarget.after ? 'after' : 'before') : null}
          ondragstart={(e) => onDragStart(e, idea.id)}
          ondragover={(e) => onDragOver(e, idea.id)}
          ondrop={onDrop}
          ondragend={end}
        />
      {/each}
    {/if}
  </div>
  {#if list}
    <div class="foot np-dim">
      {shown.length} of {ideas.length} · {basename(list.file)}{list.exists ? '' : ' (not created yet)'}
    </div>
  {/if}
</div>

<style>
  .ideas {
    display: flex;
    flex-direction: column;
    min-height: 100%;
  }
  .scope {
    gap: 8px;
    padding: 8px 8px 4px 12px;
  }
  .badge {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    max-width: 55%;
    height: 20px;
    padding: 0 8px;
    border: 0;
    border-radius: 10px;
    background: var(--accent-soft);
    color: var(--accent);
    font: inherit;
    font-size: var(--fs-sm);
    font-weight: 600;
    cursor: pointer;
    white-space: nowrap;
  }
  .badge.global {
    background: var(--bg-3);
    color: var(--fg-muted);
  }
  .file {
    direction: rtl;
    text-align: left;
    font-size: 10.5px;
    color: var(--fg-dim);
  }
  .filters {
    gap: 5px;
    padding: 4px 10px 4px 12px;
  }
  .fchip {
    height: 28px;
    padding: 0 7px;
    border-radius: var(--radius-sm);
  }
  .cap {
    text-transform: capitalize;
  }
  .n {
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
  }
  .tags {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
    padding: 2px 10px 2px 12px;
  }
  .list {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 6px 10px 10px 12px;
  }
  .foot {
    padding: 6px 12px;
    border-top: 1px solid var(--border);
    font-size: var(--fs-xs);
  }
</style>
