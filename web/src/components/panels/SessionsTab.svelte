<script>
  import { SvelteSet } from 'svelte/reactivity';
  import Icon from '../../lib/kit/Icon.svelte';
  import IconButton from '../../lib/kit/IconButton.svelte';
  import TimeAgo from '../../lib/kit/TimeAgo.svelte';
  import { app, openSession, newSession, updateSession, deleteSession, sessionStatus } from '../../lib/state/app.svelte.js';
  import { confirmDialog, toast } from '../../lib/state/ui.svelte.js';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { recencyBucket } from '../../lib/format.js';

  let { visible = true } = $props();

  let q = $state('');
  let serverHits = $state.raw(null); // SessionInfo[] from sessions.list { search }
  let showArchived = $state(false);
  let archived = $state.raw([]);
  let editing = $state(null);
  let editValue = $state('');
  const expanded = new SvelteSet();

  // children (subagent sessions) by parent id
  const children = $derived.by(() => {
    const map = new Map();
    for (const s of app.sessions) {
      if (!s.parentSessionId || s.archived) continue;
      let arr = map.get(s.parentSessionId);
      if (!arr) map.set(s.parentSessionId, (arr = []));
      arr.push(s);
    }
    return map;
  });

  const roots = $derived.by(() => {
    const query = q.trim().toLowerCase();
    if (query) {
      const local = app.sessions.filter((s) => !s.archived && (s.title || '').toLowerCase().includes(query));
      if (!serverHits) return local;
      const seen = new Set(local.map((s) => s.id));
      return local.concat(serverHits.filter((s) => !seen.has(s.id) && !s.archived));
    }
    return app.sessions.filter(
      (s) => !s.archived && (!s.parentSessionId || !app.sessionsById.has(s.parentSessionId)),
    );
  });

  const groups = $derived.by(() => {
    const now = new Date();
    const order = ['Today', 'Yesterday', 'Previous 7 days', 'Earlier'];
    const map = new Map(order.map((k) => [k, []]));
    for (const s of roots) map.get(recencyBucket(s.updatedAt, now)).push(s);
    return order.map((label) => ({ label, items: map.get(label) })).filter((g) => g.items.length);
  });

  let searchTimer = 0;
  $effect(() => {
    const query = q.trim();
    clearTimeout(searchTimer);
    serverHits = null;
    if (!query) return;
    searchTimer = setTimeout(async () => {
      try {
        const res = await rpc('sessions.list', { search: query, includeSubagents: true, limit: 50 });
        if (q.trim() === query) serverHits = res;
      } catch {}
    }, 220);
  });

  async function loadArchived() {
    try {
      const res = await rpc('sessions.list', { includeArchived: true, includeSubagents: false, limit: 200 });
      archived = (res ?? []).filter((s) => s.archived);
    } catch (e) {
      toast(e.message, 'error');
    }
  }
  $effect(() => {
    if (showArchived) loadArchived();
  });

  function startRename(s) {
    editing = s.id;
    editValue = s.title || '';
  }
  async function commitRename(s) {
    const v = editValue.trim();
    editing = null;
    if (v && v !== s.title) await updateSession(s.id, { title: v });
  }
  async function archive(s, value = true) {
    await updateSession(s.id, { archived: value });
    if (showArchived) loadArchived();
  }
  async function remove(s) {
    const ok = await confirmDialog({
      title: 'Delete session?',
      message: `“${s.title || 'New session'}” and its messages will be deleted permanently.`,
      confirmLabel: 'Delete',
      danger: true,
    });
    if (ok) {
      await deleteSession(s.id);
      if (showArchived) loadArchived();
    }
  }

  function projectName(s) {
    return s.projectId ? (app.projectsById.get(s.projectId)?.name ?? '') : '';
  }

  function focusSelect(node) {
    node.focus();
    node.select();
  }
</script>

{#snippet row(s, depth)}
  {@const kids = children.get(s.id)}
  {@const status = sessionStatus(s.id)}
  <div
    class="srow"
    class:active={s.id === app.activeId}
    class:child={depth > 0}
    role="button"
    tabindex="0"
    onclick={() => editing !== s.id && openSession(s.id)}
    onkeydown={(e) => {
      if (e.target !== e.currentTarget) return;
      if (e.key === 'Enter') openSession(s.id);
      else if (e.key === 'F2') startRename(s);
      else if (e.key === 'Delete') remove(s);
    }}
    ondblclick={() => startRename(s)}
  >
    <span class="lead">
      {#if depth > 0}
        <Icon name="branch" size={12} />
      {:else if status !== 'idle'}
        <span class="np-dot" data-status={status}></span>
      {/if}
    </span>
    <div class="main">
      {#if editing === s.id}
        <input
          class="np-input rename"
          bind:value={editValue}
          use:focusSelect
          onclick={(e) => e.stopPropagation()}
          onkeydown={(e) => {
            e.stopPropagation();
            if (e.key === 'Enter') commitRename(s);
            if (e.key === 'Escape') editing = null;
          }}
          onblur={() => commitRename(s)}
        />
      {:else}
        <div class="line1">
          <span class="title">{s.title || 'New session'}</span>
          <TimeAgo time={s.updatedAt} class="when" />
        </div>
        <div class="line2">
          {#if depth === 0 && projectName(s)}<span class="proj" title="Project {projectName(s)}"><Icon name="folder" size={11} /><span class="pn">{projectName(s)}</span></span>{/if}
          {#if depth === 0 && s.messageCount}<span class="count">{s.messageCount} msgs</span>{/if}
          {#if kids?.length}
            <button
              class="kids"
              title={expanded.has(s.id) ? 'Hide subagents' : 'Show subagents'}
              onclick={(e) => {
                e.stopPropagation();
                expanded.has(s.id) ? expanded.delete(s.id) : expanded.add(s.id);
              }}
            >
              <span class="chev" class:open={expanded.has(s.id)}><Icon name="chevron-right" size={11} /></span>
              {kids.length}<span class="kl">&nbsp;subagent{kids.length === 1 ? '' : 's'}</span>
            </button>
          {/if}
        </div>
      {/if}
    </div>
    {#if editing !== s.id}
      <div class="actions">
        <IconButton icon="rename" title="Rename (F2)" size="sm" onclick={(e) => (e.stopPropagation(), startRename(s))} />
        <IconButton icon="archive" title="Archive" size="sm" onclick={(e) => (e.stopPropagation(), archive(s))} />
        <IconButton icon="trash" title="Delete" size="sm" onclick={(e) => (e.stopPropagation(), remove(s))} />
      </div>
    {/if}
  </div>
  {#if kids?.length && expanded.has(s.id)}
    <div class="nested">
      {#each kids as k (k.id)}
        {@render row(k, depth + 1)}
      {/each}
    </div>
  {/if}
{/snippet}

<div class="tab-root">
  <div class="head">
    <div class="search">
      <Icon name="search" size={13} />
      <input
        class="search-input"
        placeholder="Search sessions"
        bind:value={q}
        onkeydown={(e) => e.key === 'Escape' && (q = '')}
        spellcheck="false"
      />
      {#if q}<button class="clear" aria-label="Clear" onclick={() => (q = '')}><Icon name="x" size={12} /></button>{/if}
    </div>
    <IconButton icon="plus" title="New session (Ctrl+T)" onclick={() => newSession()} />
  </div>

  <div class="list np-scroll">
    {#each groups as g (g.label)}
      <div class="glabel">{g.label}</div>
      {#each g.items as s (s.id)}
        {@render row(s, 0)}
      {/each}
    {:else}
      <div class="np-empty">
        <Icon name="sessions" size={22} />
        {#if q}No sessions match “{q}”{:else}No sessions yet{/if}
        {#if !q}<button class="np-btn np-btn-sm" onclick={() => newSession()}>New session</button>{/if}
      </div>
    {/each}

    {#if showArchived}
      <div class="glabel">Archived</div>
      {#each archived as s (s.id)}
        <div class="srow archived" role="button" tabindex="0" onclick={() => openSession(s.id)} onkeydown={(e) => e.key === 'Enter' && openSession(s.id)}>
          <span class="lead"><Icon name="archive" size={12} /></span>
          <div class="main">
            <div class="line1"><span class="title">{s.title || 'New session'}</span><TimeAgo time={s.updatedAt} class="when" /></div>
          </div>
          <div class="actions">
            <IconButton icon="refresh" title="Unarchive" size="sm" onclick={(e) => (e.stopPropagation(), archive(s, false))} />
            <IconButton icon="trash" title="Delete" size="sm" onclick={(e) => (e.stopPropagation(), remove(s))} />
          </div>
        </div>
      {:else}
        <div class="np-empty">Nothing archived</div>
      {/each}
    {/if}
  </div>

  <div class="foot">
    <button class="link" onclick={() => (showArchived = !showArchived)}>
      <Icon name="archive" size={12} />{showArchived ? 'Hide archived' : 'Show archived'}
    </button>
    <span class="np-dim">{app.sessions.filter((s) => !s.archived && !s.parentSessionId).length} sessions</span>
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
    padding: 0 6px 0 8px;
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
    font-size: var(--fs);
  }
  .search-input::placeholder {
    color: var(--fg-dim);
  }
  .clear {
    display: grid;
    place-items: center;
    border: 0;
    padding: 2px;
    border-radius: 3px;
    background: transparent;
    color: var(--fg-dim);
  }
  .list {
    flex: 1;
    min-height: 0;
    padding: 0 6px 8px;
  }
  .glabel {
    padding: 10px 8px 4px;
    font-size: var(--fs-xs);
    font-weight: 600;
    letter-spacing: 0.05em;
    text-transform: uppercase;
    color: var(--fg-dim);
  }
  .srow {
    position: relative;
    display: flex;
    align-items: flex-start;
    gap: 6px;
    padding: 6px 8px 6px 4px;
    border-radius: 6px;
    cursor: default;
    outline: none;
  }
  .srow:hover {
    background: var(--bg-2);
  }
  .srow:focus-visible {
    box-shadow: inset 0 0 0 1px var(--accent-line);
  }
  .srow.active {
    background: var(--bg-3);
  }
  .srow.active::before {
    content: '';
    position: absolute;
    left: -6px;
    top: 8px;
    bottom: 8px;
    width: 2px;
    border-radius: 2px;
    background: var(--accent);
  }
  .lead {
    width: 14px;
    height: 19px;
    flex: none;
    display: grid;
    place-items: center;
    color: var(--fg-dim);
  }
  .main {
    flex: 1;
    min-width: 0;
  }
  .line1 {
    display: flex;
    align-items: baseline;
    gap: 8px;
    min-width: 0;
  }
  .title {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    color: var(--fg);
  }
  .srow :global(.when) {
    flex: none;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
  }
  .line2 {
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    white-space: nowrap;
    overflow: hidden;
  }
  .line2:empty {
    display: none;
  }
  .proj {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    min-width: 0;
    flex: 0 1 auto;
  }
  .proj :global(svg) {
    flex: none;
  }
  .pn {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .count {
    flex: none;
  }
  @container (max-width: 259px) {
    .kl {
      display: none;
    }
  }
  .kids {
    flex: none;
    display: inline-flex;
    align-items: center;
    gap: 2px;
    padding: 0 4px 0 2px;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-xs);
  }
  .kids:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  .chev {
    display: inline-grid;
    transition: transform var(--t-fast);
  }
  .chev.open {
    transform: rotate(90deg);
  }
  .nested {
    margin-left: 12px;
    padding-left: 4px;
    border-left: 1px solid var(--border);
  }
  .srow.child {
    padding-top: 4px;
    padding-bottom: 4px;
  }
  .actions {
    position: absolute;
    right: 4px;
    top: 4px;
    display: flex;
    opacity: 0;
    pointer-events: none;
    transition: opacity var(--t-fast);
    gap: 1px;
    padding: 1px;
    border-radius: 5px;
    background: var(--bg-2);
    box-shadow: -8px 0 8px var(--bg-2);
  }
  .srow.active .actions {
    background: var(--bg-3);
    box-shadow: -8px 0 8px var(--bg-3);
  }
  .srow:hover .actions,
  .srow:focus-within .actions,
  .srow.active .actions {
    opacity: 1;
    pointer-events: auto;
  }
  .rename {
    height: 24px;
  }
  .archived .title {
    color: var(--fg-muted);
  }
  .foot {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 6px 12px;
    border-top: 1px solid var(--border);
    font-size: var(--fs-xs);
  }
  .link {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    border: 0;
    padding: 2px 0;
    background: transparent;
    color: var(--fg-dim);
    font-size: var(--fs-xs);
  }
  .link:hover {
    color: var(--fg);
  }
</style>
