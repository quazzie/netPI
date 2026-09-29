<script>
  import { onMount } from 'svelte';
  import { Icon, IconButton, SearchInput, Menu, Empty, Button, basename } from '@netpi/kit';
  import IdeaCard from './IdeaCard.svelte';
  import NewIdea from './NewIdea.svelte';
  import { STATUSES, ACTIVE, STATUS_TONE, matches, GROUP_ORDER, CLOSED_ORDER } from './model.js';

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
  let openGroups = $state.raw(new Set()); // collapsed statuses the user opened (parked, done, rejected)
  let dragId = $state(null);
  let dropTarget = $state(null); // { id, after }
  let visible = true;
  let dirty = false;
  // The project filter: 'all' | 'global' | a project id. It follows the active project until the user picks one, so it
  // cannot be $derived - picking 'All projects' has to stick when the project changes. onChange below keeps both in step.
  // svelte-ignore state_referenced_locally
  let activeProjectId = $state(ctx.app.activeProject?.id ?? null);
  // svelte-ignore state_referenced_locally
  let projectFilter = $state(activeProjectId ?? 'global');
  let followsActive = true;
  // Plans a closed chat left unsaved (the cards above the composer answer the same cards).
  let unsaved = $state.raw([]);
  let showUnsaved = $state(false);
  let busy = $state(null);

  async function loadUnsaved() {
    try {
      unsaved = (await ctx.rpc('ideas.suggestions', {}))?.suggestions ?? [];
    } catch {
      unsaved = []; // no Ideas plugin, or the checks are off
    }
  }

  // The two offers: a plan a closed chat left unsaved, and an idea a commit may have finished.
  const plans = $derived(unsaved.filter((s) => s.kind !== 'done'));
  const maybes = $derived(unsaved.filter((s) => s.kind === 'done'));

  async function resolve(id, action) {
    busy = id;
    try {
      await ctx.rpc('ideas.resolve', { id, action });
      unsaved = unsaved.filter((s) => s.id !== id);
      await load();
    } catch (e) {
      // Answered somewhere else: the card is gone from the file, not an error to keep on screen.
      if (/not_found|no longer|gone|conflict/i.test(e?.message ?? '')) await loadUnsaved();
      else error = e?.message ?? String(e);
    } finally {
      busy = null;
    }
  }

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
    loadUnsaved();
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
    const offCards = ctx.on('ideas.suggested', () => {
      if (visible) loadUnsaved();
    });
    // A card answered in the composer or in another window leaves the file: drop it here too.
    const offResolved = ctx.on('ideas.resolved', (d) => {
      unsaved = unsaved.filter((s) => s.id !== d?.id);
    });
    return () => {
      offChange();
      offEv();
      offCards();
      offResolved();
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
  function toggleGroup(status) {
    const s = new Set(openGroups);
    s.has(status) ? s.delete(status) : s.add(status);
    openGroups = s;
  }

  // ------------------------------------------------------------------ grouping (idea-43oruq)
  // Active statuses first, in the order the work wants them read, then the closed ones behind a count line.
  const groups = $derived.by(() => {
    const by = new Map(GROUP_ORDER.map((s) => [s, []]));
    for (const i of shown) by.get(i.status)?.push(i);
    return GROUP_ORDER.filter((s) => by.get(s).length).map((status) => ({ status, items: by.get(status) }));
  });
  const closed = $derived.by(() => {
    const n = new Map();
    for (const i of shown) if (CLOSED_ORDER.includes(i.status)) n.set(i.status, (n.get(i.status) ?? 0) + 1);
    return CLOSED_ORDER.filter((s) => n.has(s)).map((status) => ({ status, n: n.get(status) }));
  });
  // The order the cards are actually rendered in: what move up/down and drag-to-reorder move within.
  const order = $derived([...groups.flatMap((g) => g.items), ...[...openGroups].flatMap((s) => shown.filter((i) => i.status === s))]);
  const pos = $derived(new Map(order.map((i, n) => [i.id, n])));
  const placed = (id) => pos.get(id) ?? -1;

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
    // "Send to chat" stages a pointer, not the idea: the agent reads the idea itself (ideas.get, by id). The text lands
    // in the composer, which is the feedback — no toast (the user does not want one here).
    send: (idea) => {
      ctx.app.insertText(`Work on idea ${idea.id} (${idea.title}) — read it, then tell me what you plan to do.`);
    },
    toPrompt: async (id) => {
      const text = await call('ideas.toPrompt', { id });
      if (typeof text === 'string') {
        ctx.app.insertText(text);
        ctx.app.toast('Full idea text inserted into the composer — edit and send');
      }
    },
    move: (id, delta) => {
      const ids = ideas.map((i) => i.id);
      const from = ids.indexOf(id);
      // move past the neighbour that is visible under the current filter
      const visibleIds = order.map((i) => i.id);
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
    {#if list?.file}
      <span class="file" title={list.file}><Icon name="file" size={12} /></span>
    {/if}
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

  {#if unsaved.length}
    <button class="unsaved" onclick={() => (showUnsaved = !showUnsaved)} title="Waiting for you: plans a closed chat left unsaved, and ideas a commit may have finished">
      <Icon name="idea" size={12} />
      <span class="np-ellipsis"
        >{[plans.length ? `${plans.length} unsaved ${plans.length === 1 ? 'plan' : 'plans'}` : '', maybes.length ? `${maybes.length} maybe done` : '']
          .filter(Boolean)
          .join(' · ')}</span
      >
      <Icon name={showUnsaved ? 'chevron-down' : 'chevron-right'} size={11} />
    </button>
    {#if showUnsaved}
      <div class="cards">
        {#each maybes as s (s.id)}
          <div class="card done">
            <div class="np-ellipsis ct" title={s.title}>may be done: {s.title}</div>
            {#if s.commits?.length}<div class="cs np-ellipsis" title={s.commits.join('\n')}>{s.commits.slice(0, 2).join(' · ')}</div>{/if}
            <div class="cb">
              <Button size="sm" variant="primary" disabled={busy === s.id} onclick={() => resolve(s.id, 'done')}>Mark done</Button>
              <Button size="sm" disabled={busy === s.id} onclick={() => resolve(s.id, 'discard')}>Dismiss</Button>
            </div>
          </div>
        {/each}
        {#each plans as s (s.id)}
          <div class="card">
            <div class="np-ellipsis ct" title={s.title}>{s.title}</div>
            {#if s.summary}<div class="cs">{s.summary}</div>{/if}
            <div class="cb">
              <Button size="sm" variant="primary" disabled={busy === s.id} onclick={() => resolve(s.id, 'save')}>Save</Button>
              <Button size="sm" disabled={busy === s.id} onclick={() => resolve(s.id, 'discard')}>Discard</Button>
            </div>
          </div>
        {/each}
      </div>
    {/if}
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
      <!-- a calm overview: status groups of one-line titles, closed ones behind a count line (idea-43oruq) -->
      {#snippet card(idea)}
        <IdeaCard
          {idea}
          {api}
          {ctx}
          open={expanded.has(idea.id)}
          ontoggle={() => toggleExpanded(idea.id)}
          canUp={placed(idea.id) > 0}
          canDown={placed(idea.id) < order.length - 1}
          dragging={dragId === idea.id}
          drop={dropTarget?.id === idea.id ? (dropTarget.after ? 'after' : 'before') : null}
          ondragstart={(e) => onDragStart(e, idea.id)}
          ondragover={(e) => onDragOver(e, idea.id)}
          ondrop={onDrop}
          ondragend={end}
        />
      {/snippet}
      {#each groups as g (g.status)}
        <div class="ghead" data-tone={STATUS_TONE[g.status]}>
          <span class="gname">{g.status}</span>
          <span class="gn">{g.items.length}</span>
        </div>
        {#each g.items as idea (idea.id)}
          {@render card(idea)}
        {/each}
      {/each}
      {#each closed as c (c.status)}
        <button class="gfold" data-tone={STATUS_TONE[c.status]} aria-expanded={openGroups.has(c.status)} onclick={() => toggleGroup(c.status)}>
          <Icon name={openGroups.has(c.status) ? 'chevron-down' : 'chevron-right'} size={11} />
          <span class="gname">{c.status}</span>
          <span class="gn">{c.n}</span>
        </button>
        {#if openGroups.has(c.status)}
          {#each shown.filter((i) => i.status === c.status) as idea (idea.id)}
            {@render card(idea)}
          {/each}
        {/if}
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
    /* fill the tab (.plugin-root is a flex column; a percentage min-height would not resolve) */
    flex: 1 0 auto;
    display: flex;
    flex-direction: column;
  }
  .scope {
    gap: 8px;
    padding: 8px 8px 4px 12px;
  }
  /* the project-filter Menu wraps its trigger in .np-menu-anchor: grow that, and let the badge fill it */
  .scope > :global(.np-menu-anchor) {
    flex: 1 1 auto;
    min-width: 0;
  }
  .badge {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    width: 100%;
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
  /* stretch the label so the chevron sits at the right edge of the badge (buttons centre their text by default) */
  .badge .np-ellipsis {
    flex: 1 1 auto;
    text-align: left;
  }
  .file {
    display: inline-flex;
    line-height: 20px;
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
  /* the cards a closed chat left unsaved: one line until opened, so the list below still reads as a list */
  .unsaved {
    display: flex;
    align-items: center;
    gap: 6px;
    width: calc(100% - 16px);
    margin: 0 8px 6px;
    padding: 4px 8px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
    text-align: left;
  }
  .unsaved:hover {
    background: var(--bg-2);
    color: var(--fg);
  }
  .unsaved .np-ellipsis {
    flex: 1;
    min-width: 0;
  }
  .cards {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin: 0 8px 8px;
  }
  .card {
    padding: 7px 9px;
    border: 1px solid var(--border);
    border-left: 2px solid var(--accent);
    border-radius: var(--radius);
    background: var(--bg-1);
    font-size: var(--fs-sm);
  }
  .ct {
    color: var(--fg);
    font-weight: 600;
  }
  .card.done {
    border-left-color: var(--ok);
  }
  .cs {
    margin-top: 2px;
    color: var(--fg-muted);
    line-height: 1.45;
  }
  .cb {
    display: flex;
    gap: 6px;
    margin-top: 7px;
  }
  .list {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 4px 10px 10px 12px;
  }
  /* the group the status filter chip shows too, so the sections read as a list without repeating it on every card */
  .ghead {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 8px 2px 2px;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    font-weight: 600;
    letter-spacing: 0.04em;
    text-transform: uppercase;
  }
  .ghead[data-tone='accent'] {
    color: var(--accent);
  }
  .ghead[data-tone='warn'] {
    color: var(--warn);
  }
  .ghead .gn {
    font-variant-numeric: tabular-nums;
    opacity: 0.7;
  }
  /* a closed status is one line: its name and how many are in it */
  .gfold {
    display: flex;
    align-items: center;
    gap: 6px;
    margin-top: 6px;
    padding: 3px 4px;
    border: 0;
    border-top: 1px solid var(--border);
    background: transparent;
    color: var(--fg-dim);
    font: inherit;
    font-size: var(--fs-xs);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    cursor: pointer;
    text-align: left;
  }
  .gfold:hover {
    color: var(--fg-muted);
  }
  .gfold .gname {
    flex: 1 1 auto;
  }
  .gfold .gn {
    font-variant-numeric: tabular-nums;
    opacity: 0.7;
  }
  .foot {
    padding: 6px 12px;
    border-top: 1px solid var(--border);
    font-size: var(--fs-xs);
  }
</style>
