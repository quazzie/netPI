<script>
  import { onMount, tick } from 'svelte';
  import { Icon, IconButton, SearchInput, Menu, Empty, bytes, basename, copyText, desktop } from '@netpi/kit';

  let { ctx } = $props();

  let root = $state(''); // absolute workspace path
  let entries = $state.raw(new Map()); // dir rel ('' = root) -> entry[]
  let expanded = $state.raw(new Set());
  let loadingDirs = $state.raw(new Set());
  let error = $state('');
  let q = $state('');
  let results = $state.raw(null); // files.search results
  let searching = $state(false);
  let showIgnored = $state(true);
  let menu = $state();
  let listEl = $state();
  let visible = true;
  let dirty = false;
  let scopeKey = '';

  const loc = () => (ctx.app.activeSessionId ? { sessionId: ctx.app.activeSessionId } : {});
  const sep = $derived(root.includes('\\') && !root.includes('/') ? '\\' : '/');
  const abs = (rel) => (rel ? `${root.replace(/[\\/]+$/, '')}${sep}${rel.replaceAll('/', sep)}` : root);

  // ------------------------------------------------------------------ loading
  async function loadDir(dir) {
    const s = new Set(loadingDirs);
    s.add(dir);
    loadingDirs = s;
    try {
      const res = await ctx.rpc('files.list', { ...loc(), dir });
      if (dir === '') {
        if (res.root !== root) {
          // a different workspace: start over
          root = res.root;
          expanded = new Set();
          entries = new Map();
        }
        error = '';
      }
      const m = new Map(entries);
      m.set(dir, res.entries ?? []);
      entries = m;
    } catch (e) {
      if (dir === '') error = e?.message ?? String(e);
      else ctx.app.toast(`Cannot list ${dir}: ${e.message}`, 'error');
    } finally {
      const s2 = new Set(loadingDirs);
      s2.delete(dir);
      loadingDirs = s2;
    }
  }

  async function refresh() {
    await loadDir('');
    await Promise.all([...expanded].map((d) => loadDir(d)));
    dirty = false;
  }

  export function setVisible(v) {
    visible = v;
    if (v && dirty) refresh();
  }

  onMount(() => {
    scopeKey = `${ctx.app.activeSessionId}|${ctx.app.activeProject?.path ?? ''}`;
    loadDir('');
    return ctx.app.onChange(() => {
      const key = `${ctx.app.activeSessionId}|${ctx.app.activeProject?.path ?? ''}`;
      if (key === scopeKey) return;
      scopeKey = key;
      results = null;
      if (visible) loadDir('');
      else dirty = true;
    });
  });

  // ------------------------------------------------------------------ search
  let searchTimer = 0;
  let searchSeq = 0;
  $effect(() => {
    const query = q.trim();
    clearTimeout(searchTimer);
    if (!query) {
      results = null;
      searching = false;
      return;
    }
    searching = true;
    const seq = ++searchSeq;
    searchTimer = setTimeout(async () => {
      try {
        const r = await ctx.rpc('files.search', { ...loc(), query, limit: 200 });
        if (seq === searchSeq) results = r ?? [];
      } catch (e) {
        if (seq === searchSeq) results = [];
      } finally {
        if (seq === searchSeq) searching = false;
      }
    }, 140);
  });

  // ------------------------------------------------------------------ tree
  const rows = $derived.by(() => {
    const out = [];
    const walk = (dir, depth) => {
      for (const e of entries.get(dir) ?? []) {
        if (!showIgnored && e.ignored) continue;
        out.push({ e, depth });
        if (e.isDir && expanded.has(e.rel)) {
          if (entries.has(e.rel)) walk(e.rel, depth + 1);
          else out.push({ loading: true, key: `${e.rel}/…`, depth: depth + 1 });
        }
      }
    };
    walk('', 0);
    return out;
  });
  const ignoredCount = $derived([...entries.values()].reduce((n, arr) => n + arr.filter((e) => e.ignored).length, 0));

  function toggleDir(e) {
    const s = new Set(expanded);
    if (s.has(e.rel)) s.delete(e.rel);
    else {
      s.add(e.rel);
      if (!entries.has(e.rel)) loadDir(e.rel);
    }
    expanded = s;
  }
  function collapseAll() {
    expanded = new Set();
  }

  function mention(rel, isDir) {
    const p = rel.replace(/\\/g, '/') + (isDir && !rel.endsWith('/') ? '/' : '');
    ctx.app.insertText(`@${p.includes(' ') ? `"${p}"` : p} `);
  }

  function activate(e) {
    if (e.isDir) toggleDir(e);
    else mention(e.rel, false);
  }

  function items(e) {
    return [
      { label: 'Insert @mention', icon: 'at', onclick: () => mention(e.rel, e.isDir) },
      { label: 'Insert path', icon: 'file-text', onclick: () => ctx.app.insertText(e.rel + ' ') },
      { divider: true },
      { label: 'Copy relative path', icon: 'copy', onclick: () => copyText(e.rel) },
      { label: 'Copy absolute path', icon: 'copy', onclick: () => copyText(e.path ?? abs(e.rel)) },
      ...(desktop.available ? [{ label: 'Reveal in Explorer', icon: 'folder-open', onclick: () => desktop.revealPath(e.path ?? abs(e.rel)) }] : []),
      ...(e.isDir ? [{ divider: true }, { label: 'Refresh folder', icon: 'refresh', onclick: () => loadDir(e.rel) }] : []),
    ];
  }
  function onContext(ev, e) {
    ev.preventDefault();
    menu.openAt(ev.clientX, ev.clientY, items(e));
  }
  function onMore(ev, e) {
    ev.stopPropagation();
    menu.openFor(ev.currentTarget, items(e));
  }

  // keyboard navigation between rows
  function onKey(ev, e) {
    const k = ev.key;
    if (k === 'ArrowDown' || k === 'ArrowUp') {
      ev.preventDefault();
      const all = [...listEl.querySelectorAll('[data-row]')];
      const i = all.indexOf(ev.currentTarget);
      all[Math.max(0, Math.min(all.length - 1, i + (k === 'ArrowDown' ? 1 : -1)))]?.focus();
    } else if (k === 'ArrowRight' && e?.isDir && !expanded.has(e.rel)) {
      ev.preventDefault();
      toggleDir(e);
    } else if (k === 'ArrowLeft' && e?.isDir && expanded.has(e.rel)) {
      ev.preventDefault();
      toggleDir(e);
    } else if (k === 'Enter') {
      ev.preventDefault();
      e ? activate(e) : null;
    } else if (k === 'ContextMenu' || (k === 'F10' && ev.shiftKey)) {
      ev.preventDefault();
      menu.openFor(ev.currentTarget, items(e));
    }
  }

  const EXT_ICON = { md: 'file-text', txt: 'file-text', json: 'file-text', log: 'file-text' };
  const iconFor = (e) => (e.isDir ? (expanded.has(e.rel) ? 'folder-open' : 'folder') : EXT_ICON[e.name.split('.').pop()?.toLowerCase()] ?? 'file');
  const project = $derived(ctx.app.activeProject);
</script>

<Menu bind:this={menu} />
<div class="files">
  <div class="head">
    <Icon name="folder" size={13} />
    <span class="rname" title={root}>{project?.name ?? (root ? basename(root) : 'Workspace')}</span>
    <span class="rpath np-mono" title={root}><bdi>{root}</bdi></span>
    <IconButton icon="list-tree" title="Collapse all" size="sm" disabled={!expanded.size} onclick={collapseAll} />
    <IconButton icon="refresh" title="Refresh" size="sm" onclick={refresh} />
  </div>
  <div class="bar">
    <SearchInput bind:value={q} placeholder="Find files" />
  </div>

  <div class="list" bind:this={listEl} role="tree" aria-label="Files">
    {#if results}
      {#each results as r (r.path ?? r.rel)}
        {@const i = r.rel.lastIndexOf('/')}
        <div
          class="frow flat"
          data-row
          role="treeitem"
          aria-selected="false"
          tabindex="0"
          title="Click to insert @{r.rel}"
          onclick={() => mention(r.rel, r.isDir)}
          onkeydown={(ev) => onKey(ev, { ...r, name: basename(r.rel) })}
          oncontextmenu={(ev) => onContext(ev, { ...r, name: basename(r.rel) })}
        >
          <span class="ic"><Icon name={r.isDir ? 'folder' : 'file'} size={13} /></span>
          <span class="fname">{r.rel.slice(i + 1)}</span>
          <span class="fdir np-ellipsis">{i > 0 ? r.rel.slice(0, i) : ''}</span>
        </div>
      {:else}
        <Empty icon="search">{searching ? 'Searching…' : `No files match “${q}”`}</Empty>
      {/each}
    {:else if error}
      <Empty icon="alert">
        <div>{error}</div>
        <button class="np-btn np-btn-sm" onclick={refresh}>Retry</button>
      </Empty>
    {:else if !entries.has('')}
      <Empty><span class="np-spinner"></span></Empty>
    {:else}
      {#each rows as r (r.loading ? r.key : r.e.rel)}
        {#if r.loading}
          <div class="frow loading" style:--d={r.depth}><span class="np-spinner" style="width:10px;height:10px"></span></div>
        {:else}
          {@const e = r.e}
          <div
            class="frow"
            class:dir={e.isDir}
            class:ignored={e.ignored}
            style:--d={r.depth}
            data-row
            role="treeitem"
            aria-selected="false"
            aria-expanded={e.isDir ? expanded.has(e.rel) : undefined}
            tabindex="0"
            title={e.isDir ? e.rel : `Click to insert @${e.rel}`}
            onclick={() => activate(e)}
            onkeydown={(ev) => onKey(ev, e)}
            oncontextmenu={(ev) => onContext(ev, e)}
          >
            <span class="chev" class:open={expanded.has(e.rel)}>
              {#if e.isDir}{#if loadingDirs.has(e.rel)}<span class="np-spinner" style="width:9px;height:9px"></span>{:else}<Icon name="chevron-right" size={11} stroke={2} />{/if}{/if}
            </span>
            <span class="ic"><Icon name={iconFor(e)} size={13} /></span>
            <span class="fname">{e.name}</span>
            <span class="np-spacer"></span>
            {#if !e.isDir && e.size != null}<span class="size">{bytes(e.size)}</span>{/if}
            <span class="acts">
              <button class="act" title="Insert @mention" onclick={(ev) => (ev.stopPropagation(), mention(e.rel, e.isDir))}><Icon name="at" size={12} /></button>
              <button class="act" title="More" onclick={(ev) => onMore(ev, e)}><Icon name="more" size={12} /></button>
            </span>
          </div>
        {/if}
      {:else}
        <Empty icon="folder">Empty folder</Empty>
      {/each}
    {/if}
  </div>
  <div class="foot np-line">
    {#if results}
      <span class="np-grow">{results.length}{results.length >= 200 ? '+' : ''} matches</span>
    {:else}
      <span class="np-grow" title="Click a file to insert @path into the composer; right-click for more">Click: <span class="np-mono">@path</span> · right-click: more</span>
    {/if}
    {#if ignoredCount && !results}
      <button class="lnk" onclick={() => (showIgnored = !showIgnored)} title="Entries skipped by .gitignore / built-in ignore rules">
        {showIgnored ? 'hide' : 'show'} ignored
      </button>
    {/if}
  </div>
</div>

<style>
  .files {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
  }
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 8px 6px 4px 12px;
    color: var(--fg-dim);
    min-width: 0;
  }
  .rname {
    flex: none;
    max-width: 50%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    color: var(--fg);
    font-weight: 600;
  }
  .rpath {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    direction: rtl;
    text-align: left;
    font-size: 10.5px;
  }
  .bar {
    padding: 2px 8px 6px 10px;
  }
  .list {
    flex: 1;
    min-height: 0;
    overflow: auto;
    padding: 0 6px 8px;
    scrollbar-width: thin;
  }
  .frow {
    --d: 0;
    display: flex;
    align-items: center;
    gap: 4px;
    height: 24px;
    padding: 0 4px 0 calc(4px + var(--d) * var(--indent, 14px));
    border-radius: 4px;
    color: var(--fg);
    font-size: var(--fs);
    cursor: pointer;
    user-select: none;
    outline: none;
    min-width: 0;
  }
  .frow:hover {
    background: var(--bg-2);
  }
  .frow:focus-visible {
    box-shadow: inset 0 0 0 1px var(--accent-line);
    background: var(--bg-2);
  }
  .frow.ignored {
    opacity: 0.45;
  }
  .frow.loading {
    color: var(--fg-dim);
    padding-left: calc(24px + var(--d) * var(--indent, 14px));
  }
  @container (max-width: 279px) {
    .list {
      --indent: 11px;
    }
  }
  .chev {
    display: grid;
    place-items: center;
    width: 12px;
    flex: none;
    color: var(--fg-dim);
    transition: transform var(--t-fast);
  }
  .chev.open {
    transform: rotate(90deg);
  }
  .ic {
    display: grid;
    place-items: center;
    flex: none;
    color: var(--fg-dim);
  }
  .dir .ic {
    color: color-mix(in srgb, var(--accent) 70%, var(--fg-dim));
  }
  .fname {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .flat .fname {
    flex: none;
    max-width: 60%;
  }
  .fdir {
    color: var(--fg-dim);
    font-size: var(--fs-xs);
    direction: rtl;
    text-align: left;
  }
  .size {
    flex: none;
    font-size: 10.5px;
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
  }
  .frow:hover .size {
    display: none;
  }
  .acts {
    display: none;
    flex: none;
    gap: 1px;
  }
  .frow:hover .acts,
  .frow:focus-visible .acts {
    display: flex;
  }
  .act {
    display: grid;
    place-items: center;
    width: 20px;
    height: 20px;
    padding: 0;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: var(--fg-dim);
    cursor: pointer;
  }
  .act:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  .foot {
    gap: 8px;
    padding: 6px 12px;
    border-top: 1px solid var(--border);
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .lnk {
    padding: 0;
    border: 0;
    background: transparent;
    color: var(--fg-dim);
    font: inherit;
    cursor: pointer;
  }
  .lnk:hover {
    color: var(--fg);
  }
</style>
