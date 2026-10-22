<script>
  import { onMount } from 'svelte';
  import { Icon, IconButton, SearchInput, Menu, Empty, Button, useRefresh } from '@netpi/kit';
  import IdeaCard from './IdeaCard.svelte';
  import { STATUSES, ACTIVE, STATUS_TONE, matches, GROUP_ORDER, CLOSED_ORDER } from './model.js';

  let { ctx } = $props();

  let list = $state.raw(null); // ideas.list result (the backlog in NetPI's database)
  let projects = $state.raw([]); // projects.list
  let error = $state('');
  let loading = $state(true);
  let q = $state('');
  let statusFilter = $state('active'); // active | all | <status>
  let tagFilter = $state.raw(new Set());
  let openGroups = $state.raw(new Set()); // collapsed statuses the user opened (parked, done, rejected)
  let dragId = $state(null);
  let dropTarget = $state(null); // { id, after }
  // The project filter: 'all' | 'global' | a project id. It follows the active project until the user picks one, so it
  // cannot be $derived - picking 'All projects' has to stick when the project changes. onChange below keeps both in step.
  // svelte-ignore state_referenced_locally
  let activeProjectId = $state(ctx.app.activeProject?.id ?? null);
  // svelte-ignore state_referenced_locally
  let projectFilter = $state(activeProjectId ?? 'global');
  let followsActive = true;
  let conflict = $state.raw(null); // { id, message } for the idea an update was refused for
  // Meaning search (≈): with embeddings on the server, the search box can rank ideas by meaning (ideas.similar)
  // instead of requiring every word. Off by default; the toggle only shows when the server has embeddings.
  let canMeaning = $state(false);
  let byMeaning = $state(false);
  let meaning = $state.raw(null); // Map id -> score for the current query, or null
  let meaningTimer = null;
  let meaningSeq = 0;
  $effect(() => {
    const text = q.trim();
    const on = byMeaning;
    clearTimeout(meaningTimer);
    const seq = ++meaningSeq; // also when there is nothing to ask: an answer still in flight must not land
    if (!on || !text) { meaning = null; return; }
    meaningTimer = setTimeout(async () => {
      try {
        const r = await ctx.rpc('ideas.similar', { text, limit: 30, status: 'all' });
        if (seq === meaningSeq) meaning = r?.available ? new Map((r.ideas ?? []).map((i) => [i.id, i.score])) : null;
      } catch { if (seq === meaningSeq) meaning = null; }
    }, 300);
    return () => clearTimeout(meaningTimer);
  });

  /** The automatic refine drain's state (ideas.autoRefine.status): shown in the header only while it is enabled. */
  let autoRefine = $state(null);
  const autoRefineText = $derived(
    autoRefine?.enabled
      ? `Auto refine: ${autoRefine.running?.length ?? 0} running, ${autoRefine.queued?.length ?? 0} waiting`
      : '',
  );

  async function load() {
    try {
      [list, projects] = await Promise.all([ctx.rpc('ideas.list', {}), ctx.rpc('projects.list', {}).catch(() => [])]);
      error = '';
      ctx.rpc('ideas.autoRefine.status', {}).then((r) => (autoRefine = r ?? null)).catch(() => (autoRefine = null));
      ctx.rpc('ideas.capabilities', {}).then((c) => (canMeaning = !!c?.embeddings)).catch(() => (canMeaning = false));
    } catch (e) {
      error = e?.message ?? String(e);
      list = null;
    } finally {
      loading = false;
    }
  }

  // svelte-ignore state_referenced_locally
  const tab = useRefresh(ctx, { load, events: ['ideas.changed'] });

  /** Called by main.js (onShow / onHide): what lands while the tab is hidden only marks it stale. */
  export function setVisible(v) {
    tab.setVisible(v);
  }

  onMount(() => {
    const offChange = ctx.app.onChange(() => {
      const pid = ctx.app.activeProject?.id ?? null;
      if (pid !== activeProjectId) {
        activeProjectId = pid;
        if (followsActive) projectFilter = pid ?? 'global'; // the default follows the active project
      }
    });
    return () => {
      offChange();
    };
  });

  // ------------------------------------------------------------------ filtering
  const ideas = $derived(list?.ideas ?? []);
  // Where the backlog lives, as ideas.list describes it. A hint, not a file to open: the ideas are documents in NetPI's store.
  const storage = $derived(list?.storage ?? null);
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
  const shown = $derived.by(() => {
    const scoped = ideas.filter(
      (i) =>
        (projectFilter === 'all' || (projectFilter === 'global' ? !effProject(i) : effProject(i) === projectFilter)) &&
        (statusFilter === 'all' || (statusFilter === 'active' ? ACTIVE.has(i.status) : i.status === statusFilter)) &&
        (!tagFilter.size || (i.tags ?? []).some((t) => tagFilter.has(t))),
    );
    // ≈: the ideas the server ranked by meaning, best first (within the same filters); otherwise every word must match.
    if (byMeaning && meaning) return scoped.filter((i) => meaning.has(i.id)).sort((a, b) => meaning.get(b.id) - meaning.get(a.id));
    return scoped.filter((i) => matches(i, q.trim()));
  });
  const statusLabel = $derived(statusFilter === 'active' ? 'Active' : statusFilter === 'all' ? 'All' : statusFilter);
  const projectLabel = $derived(
    projectFilter === 'all' ? 'All projects' : projectFilter === 'global' ? 'Global' : (projects.find((p) => p.id === projectFilter)?.name ?? projectFilter),
  );

  function toggleTag(t) {
    const s = new Set(tagFilter);
    s.has(t) ? s.delete(t) : s.add(t);
    tagFilter = s;
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
  // The whole list as one flat sequence of rows — a group head, a closed-status fold, or a card — so an idea that
  // moves to another status group is moved in this list rather than destroyed and made again: the card keeps the open
  // editor and everything typed into it (idea-c3hihl).
  const rows = $derived.by(() => {
    const out = [];
    for (const g of groups) {
      out.push({ key: `g:${g.status}`, head: g.status, n: g.items.length });
      for (const idea of g.items) out.push({ key: `i:${idea.id}`, idea });
    }
    for (const c of closed) {
      const open = openGroups.has(c.status);
      out.push({ key: `c:${c.status}`, fold: c, open, items: open ? shown.filter((i) => i.status === c.status) : [] });
    }
    return out;
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
    // The revision the card had when its editor was opened: another window (or the agent) changing the idea in the
    // meantime is a conflict, not something to overwrite. An editor passes the revision it captured; the one-line
    // actions (status, images, section removal) pass none and apply to the idea as it is now. Falling back to the
    // list's revision would make the check unreachable, because the list is refetched on every ideas.changed
    // (idea-c3hihl).
    update: async (id, patch, revision) => {
      const params = { id, patch };
      if (revision != null) params.expectedRevision = revision;
      let r = null;
      try {
        r = await ctx.rpc('ideas.update', params);
      } catch (e) {
        if (/conflict|changed since/i.test(e?.message ?? '')) {
          conflict = { id, message: e.message };
          ctx.app.toast('This idea changed somewhere else - your text is still here. Reload it and apply your change again.', 'error');
          return null;
        }
        ctx.app.toast(`ideas.update: ${e.message}`, 'error');
        return null;
      }
      conflict = null;
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

  // A new idea is filed in the host's idea dialog (the one Ctrl+I opens), on the project this tab is showing: the list's own
  // project, or the active chat's when it shows them all. The new card arrives with ideas.changed.
  function newIdea() {
    ctx.app.openIdea({ projectId: projectFilter === 'all' ? (activeProjectId ?? 'global') : projectFilter });
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
    {#if storage}
      <span class="file" title="The ideas backlog lives in the {storage.database} database ({storage.scope}, version {storage.schemaVersion}) - it is not a file you can edit"><Icon name="archive" size={12} /></span>
    {/if}
    {#if autoRefineText}
      <span class="arstat np-ellipsis" title="Your agents refine new ideas in the background, one after another at low priority, until all are refined. Settings: Ideas.">
        <Icon name="bot" size={12} /> {autoRefineText}
      </span>
    {/if}
    <IconButton icon="plus" title="New idea (Ctrl+I)" size="sm" onclick={newIdea} />
  </div>

  <div class="filters">
    <!-- the search and its ≈ mode share a row; the status and tag filters sit on the row below -->
    <div class="np-line">
      <SearchInput bind:value={q} placeholder={byMeaning ? 'Search by meaning' : 'Search ideas'} class="np-grow" />
      {#if canMeaning}
        <button class="np-chip fchip meaning" aria-pressed={byMeaning} onclick={() => (byMeaning = !byMeaning)}
          title={byMeaning ? 'Searching by meaning (embeddings): click for word search' : 'Search by meaning instead of words (embeddings)'}>≈</button>
      {/if}
    </div>
    <div class="np-line chips">
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
  </div>
  {#if tagFilter.size}
    <div class="tags">
      {#each [...tagFilter] as t (t)}
        <button class="np-chip" aria-pressed="true" title="Remove this tag filter" onclick={() => toggleTag(t)}>#{t}<Icon name="x" size={10} /></button>
      {/each}
    </div>
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
        <Button size="sm" icon="plus" onclick={newIdea}>New idea</Button>
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
      {#each rows as row (row.key)}
        {#if row.head}
          <div class="ghead" data-tone={STATUS_TONE[row.head]}>
            <span class="gname">{row.head}</span>
            <span class="gn">{row.n}</span>
          </div>
        {:else if row.fold}
          <button class="gfold" data-tone={STATUS_TONE[row.fold.status]} aria-expanded={row.open} onclick={() => toggleGroup(row.fold.status)}>
            <Icon name={row.open ? 'chevron-down' : 'chevron-right'} size={11} />
            <span class="gname">{row.fold.status}</span>
            <span class="gn">{row.fold.n}</span>
          </button>
          {#each row.items as idea (idea.id)}
            {@render card(idea)}
          {/each}
        {:else}
          {@render card(row.idea)}
        {/if}
      {/each}
    {/if}
  </div>
  {#if conflict}
    <div class="np-dim conflict" role="status">Someone else changed “{conflict.id}”. Your edit is still on screen - reopen the idea to see their version, then save again.</div>
  {/if}
  {#if list}
    <div class="foot np-dim">
      {shown.length} of {ideas.length}{#if storage}&nbsp;· {storage.database}{/if}
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
  .arstat {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    margin-left: auto;
    min-width: 0;
    font-size: var(--fs-sm);
    color: var(--fg-dim);
  }
  .file {
    display: inline-flex;
    line-height: 20px;
    color: var(--fg-dim);
  }
  /* two rows: the search with its ≈ mode, then the status and tag filters */
  .filters {
    display: flex;
    flex-direction: column;
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
  /* the status filter fills its row (its Menu trigger sits in an .np-menu-anchor, grown as the project badge is in
     .scope), so the tag filter sits on the right; the label stretches, the count and chevron sit at the right edge */
  .chips > :global(.np-menu-anchor:first-child) {
    flex: 1 1 auto;
    min-width: 0;
  }
  .chips .fchip {
    width: 100%;
    justify-content: flex-start;
  }
  .chips .cap {
    flex: 1 1 auto;
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
