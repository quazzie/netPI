<script>
  import { onMount, tick } from 'svelte';
  import { Icon, IconButton, SearchInput, Menu, Empty, bytes, basename, confirm, copyText, desktop, useRefresh } from '@netpi/kit';

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
  let git = $state.raw(null); // files.git: the changes since the last commit; null outside a repository
  let gitOpen = $state(false); // the list shows the changed files instead of the tree
  let menu = $state();
  let selected = $state(''); // the row a single click selected (a file opens on a double click, like the file manager)
  let listEl = $state();
  let visible = true;
  let dirty = false;
  let rekey = false; // the workspace may have changed while the tab was hidden: find out when it is shown
  let keyHint = ''; // what the key was last computed from (hint()): the same hint is the same key, without asking again
  let scopeKey = $state(''); // workspace generation (session + workspace identity/version): an answer applies only while its generation is still current
  let scope = $state.raw(null); // files.scope (or the host's copy of it): the workspace this tab shows (root, branch), null until loaded

  /**
   * What the key depends on, from what is in hand: the session, its workspace as the host knows it (ctx.app.activeWorkspace:
   * the files.scope the host read when the tab was activated, or the session.workspace that bound it since) and the
   * project path. ctx.app.onChange fires for every session.updated of the active chat — each appended message — and only
   * a change in this is a reason to compute the key again.
   */
  function hint() {
    const ws = ctx.app.activeWorkspace;
    return `${ctx.app.activeSessionId ?? ''}|${ws?.identity ?? ''}|${ws?.workspaceId ?? ''}|${ws?.version ?? ''}|${ctx.app.activeProject?.path ?? ''}`;
  }

  /**
   * The refresh key. The workspace identity (id + version) rather than the session's project path, because a session can
   * move to another checkout of the same project — the path alone would not change, and answers for the old root would be
   * merged into the new one. The host's copy answers when it has the identity for this session (no round trip); else
   * files.scope, and the session with its project path when the workspace plugin is not loaded.
   */
  async function key() {
    const sid = ctx.app.activeSessionId ?? '';
    const known = ctx.app.activeWorkspace;
    if (known && known.sessionId === sid && known.identity) {
      scope = known;
      return `${sid}|${known.identity}|${known.version ?? 0}`;
    }
    try {
      const r = await ctx.rpc('files.scope', loc());
      scope = r ?? null;
      return `${sid}|${r?.identity ?? (r?.root ?? '')}|${r?.version ?? 0}`;
    } catch {
      // No workspace support: the session and its project path are all there is.
      scope = null;
      return `${sid}|${ctx.app.activeProject?.path ?? ''}`;
    }
  }

  const loc = () => (ctx.app.activeSessionId ? { sessionId: ctx.app.activeSessionId } : {});
  const sep = $derived(root.includes('\\') && !root.includes('/') ? '\\' : '/');
  const abs = (rel) => (rel ? `${root.replace(/[\\/]+$/, '')}${sep}${rel.replaceAll('/', sep)}` : root);

  // ------------------------------------------------------------------ loading
  async function loadDir(dir) {
    const gen = scopeKey; // the workspace this request was sent for
    const s = new Set(loadingDirs);
    s.add(dir);
    loadingDirs = s;
    try {
      const res = await ctx.rpc('files.list', { ...loc(), dir });
      if (gen !== scopeKey) return; // the workspace changed while it was in flight: this answer is for the old one
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
      if (gen !== scopeKey) return;
      if (dir === '') error = e?.message ?? String(e);
      else ctx.app.toast(`Cannot list ${dir}: ${e.message}`, 'error');
    } finally {
      const s2 = new Set(loadingDirs);
      s2.delete(dir);
      loadingDirs = s2;
    }
  }

  async function refresh() {
    loadGit();
    await loadDir('');
    await Promise.all([...expanded].map((d) => loadDir(d)));
    dirty = false;
  }

  // The git line: loaded with the tree, after tools ran (debounced) and when the window gets the focus back (a
  // commit made in a terminal); while the tab is hidden it only notes that it is out of date.
  let gitSeq = 0;
  async function loadGit() {
    const seq = ++gitSeq;
    const gen = scopeKey;
    let r = null;
    try {
      r = await ctx.rpc('files.git', loc());
    } catch {
      // git failed, or an older files plugin without files.git: no git line
    }
    if (seq !== gitSeq || gen !== scopeKey) return;
    git = r ?? null;
    if (!git) gitOpen = false;
  }
  // svelte-ignore state_referenced_locally
  const gitTab = useRefresh(ctx, { load: loadGit, events: ['tool.end'], delayMs: 800 });

  export function setVisible(v) {
    visible = v;
    gitTab.setVisible(v);
    if (!v) return;
    if (rekey) {
      rekey = false;
      void switchWorkspace(); // the tab is visible now, so a changed workspace loads at once
    } else if (dirty) refresh();
  }

  /**
   * The active session or its workspace changed: compute the key, and when it is a new generation, start over for the
   * new root. The identity is read asynchronously; every load below waits for it, and the generation it yields is what
   * the in-flight answers of the old workspace are compared against.
   */
  async function switchWorkspace() {
    const next = await key();
    if (disposed || next === scopeKey) return;
    scopeKey = next; // new generation: the in-flight answers of the old workspace are stale
    root = '';
    entries = new Map();
    expanded = new Set();
    loadingDirs = new Set();
    error = '';
    results = null;
    git = null;
    gitOpen = false;
    if (visible) {
      await loadDir('');
      loadGit();
    } else dirty = true;
  }

  // Set when this instance is torn down. onMount has to stay synchronous for Svelte to register the teardown below
  // (an async callback returns a Promise, so the returned function would never run: every remount — each plugin load
  // bumps UiVersion and PluginTabHost re-mounts on it — leaked a window focus listener that ran a real files.git RPC on
  // the next focus and kept this component graph alive). The async part therefore runs inside the mount and every step
  // after an await checks this flag.
  let disposed = false;

  onMount(() => {
    keyHint = hint();
    (async () => {
      const k = await key();
      if (disposed) return;
      scopeKey = k;
      loadDir('');
      loadGit();
    })();
    const onFocus = () => gitTab.schedule(300);
    window.addEventListener('focus', onFocus);
    const off = ctx.app.onChange(() => {
      // Every session.updated of the active chat lands here (one per appended message): nothing to do unless what the key
      // depends on changed — and while the tab is hidden, only a note to find out when it is shown.
      const h = hint();
      if (h === keyHint) return;
      keyHint = h;
      if (!visible) {
        rekey = true;
        return;
      }
      void switchWorkspace();
    });
    return () => {
      disposed = true;
      off?.();
      window.removeEventListener('focus', onFocus);
    };
  });

  // ------------------------------------------------------------------ search
  let searchTimer = 0;
  let searchSeq = 0;
  $effect(() => {
    void scopeKey; // the workspace changed: re-run the query even when its text did not (the hits are per-workspace)
    const query = q.trim();
    clearTimeout(searchTimer);
    if (!query) {
      results = null;
      searching = false;
      return;
    }
    searching = true;
    const seq = ++searchSeq;
    const gen = scopeKey;
    searchTimer = setTimeout(async () => {
      try {
        const r = await ctx.rpc('files.search', { ...loc(), query, limit: 200 });
        if (seq === searchSeq && gen === scopeKey) results = r ?? [];
      } catch (e) {
        if (seq === searchSeq && gen === scopeKey) results = [];
      } finally {
        if (seq === searchSeq && gen === scopeKey) searching = false;
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

  /** One click, like the file manager: a folder expands in the tree (a search result is shown in it), a file is only
   * selected — opening it is a double click, because that runs whatever program is associated with it. */
  function select(e, flat = false) {
    selected = e.rel;
    if (e.isDir) flat ? showInTree(e.rel) : toggleDir(e);
  }

  /** Double click or Enter: a file opens; a folder expands in the tree, or (a search result) is shown in the tree. */
  function activate(e, flat = false) {
    if (e.isDir) flat ? showInTree(e.rel) : toggleDir(e);
    else if (!e.gone) openPath(e);
  }

  /** Clears the filter and expands the tree down to a folder (a search result), then focuses its row. */
  async function showInTree(rel) {
    const parts = rel.split('/');
    const dirs = parts.map((_, i) => parts.slice(0, i + 1).join('/'));
    expanded = new Set([...expanded, ...dirs]);
    q = '';
    gitOpen = false;
    await Promise.all(dirs.filter((d) => !entries.has(d)).map((d) => loadDir(d)));
    await tick();
    listEl?.querySelector(`[data-rel="${CSS.escape(rel)}"]`)?.focus();
  }

  /**
   * Open with the operating system: the file's default program, a folder in the file manager. `user: true` says the user
   * picked this row on purpose (a double click here, the row menu's "Open"), which is what makes a double click behave
   * like one in the file manager — every kind of file, executables and scripts included, opens the way Windows would,
   * and a file nothing is associated with gets the "choose a program" dialog.
   */
  async function openPath(e) {
    const call = (confirmed) => ctx.rpc('files.open', { ...loc(), path: e.path ?? e.rel, user: true, ...(confirmed ? { confirm: true } : {}) });
    try {
      const r = await call(false);
      // A changed file outside the session's workspace — the repository is bigger than its root, so its rel starts with
      // ../ — is not opened until the user says so: the host answers 'confirm' and opens nothing itself.
      if (r?.action === 'confirm') {
        const ok = await confirm({
          title: 'Open a file outside the workspace?',
          message: `${r.path}\n\nThis session works somewhere else, so opening it is your call.`,
          confirmLabel: 'Open',
          danger: true,
        });
        if (!ok) return;
        await call(true);
      }
    } catch (err) {
      ctx.app.toast(err.message, 'error');
    }
  }

  /** The row menu; `gone` = a deleted file of the changes list (nothing to open or reveal). */
  function items(e) {
    return [
      ...(e.gone ? [] : [{ label: e.isDir ? 'Open folder' : 'Open', icon: 'external', onclick: () => openPath(e) }, { divider: true }]),
      { label: 'Insert @mention', icon: 'at', onclick: () => mention(e.rel, e.isDir) },
      { label: 'Insert path', icon: 'file-text', onclick: () => ctx.app.insertText(e.rel + ' ') },
      { divider: true },
      { label: 'Copy relative path', icon: 'copy', onclick: () => copyText(e.rel) },
      { label: 'Copy absolute path', icon: 'copy', onclick: () => copyText(e.path ?? abs(e.rel)) },
      ...(desktop.available && !e.gone ? [{ label: 'Reveal in Explorer', icon: 'folder-open', onclick: () => desktop.revealPath(e.path ?? abs(e.rel)) }] : []),
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

  // keyboard navigation between rows; `flat` = a search result or a changed file
  function onKey(ev, e, flat = false) {
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
      e ? activate(e, flat) : null;
    } else if (k === 'ContextMenu' || (k === 'F10' && ev.shiftKey)) {
      ev.preventDefault();
      menu.openFor(ev.currentTarget, items(e));
    }
  }

  const EXT_ICON = { md: 'file-text', txt: 'file-text', json: 'file-text', log: 'file-text' };
  const iconFor = (e) => (e.isDir ? (expanded.has(e.rel) ? 'folder-open' : 'folder') : EXT_ICON[e.name.split('.').pop()?.toLowerCase()] ?? 'file');
  const project = $derived(ctx.app.activeProject);

  // ------------------------------------------------------------------ git
  const STATUS = {
    modified: ['M', 'modified'],
    added: ['A', 'added (staged)'],
    new: ['U', 'new (untracked)'],
    deleted: ['D', 'deleted'],
    renamed: ['R', 'renamed'],
    copied: ['C', 'copied'],
    conflict: ['!', 'conflict'],
  };
  const plural = (n, one) => `${n} ${one}${n === 1 ? '' : 's'}`;
  const gitTitle = $derived.by(() => {
    if (!git) return '';
    const lines = [`${git.repo} · ${git.branch ?? '?'}`];
    lines.push(git.files.length ? `Uncommitted: ${plural(git.files.length, 'file')}, +${git.added} −${git.deleted} lines (staged or not, new files included)` : 'No changes since the last commit');
    if (git.ahead) lines.push(`${plural(git.ahead, 'commit')} not pushed`);
    if (git.behind) lines.push(`${plural(git.behind, 'commit')} to pull`);
    lines.push(gitOpen ? 'Click to show the file tree' : 'Click to list the changed files');
    return lines.join('\n');
  });
  function toggleChanges() {
    gitOpen = !gitOpen;
    if (gitOpen) {
      q = '';
      loadGit();
    }
  }
</script>

<Menu bind:this={menu} />
<div class="files">
  <div class="head">
    <Icon name="folder" size={13} />
    <span class="rname" title={root}>{scope?.branch ?? project?.name ?? (root ? basename(root) : 'Workspace')}</span>
    <span class="rpath np-mono" title={root}><bdi>{root}</bdi></span>
    {#if ignoredCount}
      <IconButton
        icon={showIgnored ? 'eye' : 'eye-off'}
        title="{showIgnored ? 'Hide' : 'Show'} the entries skipped by .gitignore and the built-in ignore rules"
        size="sm"
        onclick={() => (showIgnored = !showIgnored)}
      />
    {/if}
    <IconButton icon="list-tree" title="Collapse all" size="sm" disabled={!expanded.size} onclick={collapseAll} />
    <IconButton icon="refresh" title="Refresh" size="sm" onclick={refresh} />
  </div>
  <div class="bar">
    <SearchInput bind:value={q} placeholder="Find files" />
  </div>

  {#snippet mentionAct(e)}
    <span class="acts">
      <button class="act" title="Insert @{e.rel} into the chat" onclick={(ev) => (ev.stopPropagation(), mention(e.rel, e.isDir))}><Icon name="at" size={12} /></button>
      <button class="act" title="More" onclick={(ev) => onMore(ev, e)}><Icon name="more" size={12} /></button>
    </span>
  {/snippet}

  <div class="list" bind:this={listEl} role="tree" aria-label="Files">
    {#if results}
      {#each results as r (r.path ?? r.rel)}
        {@const i = r.rel.lastIndexOf('/')}
        {@const e = { ...r, name: basename(r.rel) }}
        <div
          class="frow flat"
          data-row
          role="treeitem"
          aria-selected={selected === r.rel}
          tabindex="0"
          title={r.isDir ? `Show ${r.rel} in the tree` : `${r.rel} — double-click to open it`}
          onclick={() => select(e, true)}
          ondblclick={() => activate(e, true)}
          onkeydown={(ev) => onKey(ev, e, true)}
          oncontextmenu={(ev) => onContext(ev, e)}
        >
          <span class="ic"><Icon name={r.isDir ? 'folder' : 'file'} size={13} /></span>
          <span class="np-line np-baseline tx">
            <span class="fname">{r.rel.slice(i + 1)}</span>
            <span class="fdir np-grow">{i > 0 ? r.rel.slice(0, i) : ''}</span>
          </span>
          {@render mentionAct(e)}
        </div>
      {:else}
        <Empty icon="search">{searching ? 'Searching…' : `No files match “${q}”`}</Empty>
      {/each}
    {:else if gitOpen && git}
      <div class="ltitle">Changes since the last commit</div>
      {#each git.files as f (f.path)}
        {@const i = f.rel.lastIndexOf('/')}
        {@const e = { rel: f.rel, path: f.path, name: f.rel.slice(i + 1), isDir: false, gone: f.status === 'deleted' }}
        {@const [letter, what] = STATUS[f.status] ?? ['?', f.status]}
        <div
          class="frow flat change"
          class:gone={e.gone}
          data-row
          data-status={f.status}
          role="treeitem"
          aria-selected={selected === f.rel}
          tabindex="0"
          title="{f.rel}: {what}{e.gone ? '' : ' — double-click to open it'}"
          onclick={() => select(e, true)}
          ondblclick={() => activate(e, true)}
          onkeydown={(ev) => onKey(ev, e, true)}
          oncontextmenu={(ev) => onContext(ev, e)}
        >
          <span class="np-line np-baseline tx">
            <span class="st np-mono">{letter}</span>
            <span class="fname">{e.name}</span>
            <span class="fdir np-grow">{i > 0 ? f.rel.slice(0, i) : ''}</span>
            {#if f.added || f.deleted}
              <span class="lines np-mono">{#if f.added}<span class="add">+{f.added}</span>{/if}{#if f.deleted}<span class="del">−{f.deleted}</span>{/if}</span>
            {/if}
          </span>
          {@render mentionAct(e)}
        </div>
      {:else}
        <Empty icon="circle-check">No changes since the last commit</Empty>
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
            data-rel={e.rel}
            role="treeitem"
            aria-selected={selected === e.rel}
            aria-expanded={e.isDir ? expanded.has(e.rel) : undefined}
            tabindex="0"
            title={e.isDir ? e.rel : `${e.rel} — double-click to open it`}
            onclick={() => select(e)}
            ondblclick={() => activate(e)}
            onkeydown={(ev) => onKey(ev, e)}
            oncontextmenu={(ev) => onContext(ev, e)}
          >
            <span class="chev" class:open={expanded.has(e.rel)}>
              {#if e.isDir}{#if loadingDirs.has(e.rel)}<span class="np-spinner" style="width:9px;height:9px"></span>{:else}<Icon name="chevron-right" size={11} stroke={2} />{/if}{/if}
            </span>
            <span class="ic"><Icon name={iconFor(e)} size={13} /></span>
            <span class="np-line np-baseline tx">
              <span class="fname np-grow">{e.name}</span>
              {#if !e.isDir && e.size != null}<span class="size">{bytes(e.size)}</span>{/if}
            </span>
            {@render mentionAct(e)}
          </div>
        {/if}
      {:else}
        <Empty icon="folder">Empty folder</Empty>
      {/each}
    {/if}
  </div>
  {#if results}
    <div class="foot np-line">
      <span class="np-grow">{results.length}{results.length >= 200 ? '+' : ''} matches</span>
    </div>
  {:else if git}
    <button class="foot git np-line" class:open={gitOpen} aria-expanded={gitOpen} title={gitTitle} onclick={toggleChanges}>
      <Icon name="branch" size={12} />
      <span class="np-line np-baseline np-grow gl">
        <span class="np-grow br">{git.branch ?? '?'}</span>
        {#if git.ahead}<span class="ab"><Icon name="arrow-up" size={10} stroke={2} />{git.ahead}</span>{/if}
        {#if git.behind}<span class="ab"><Icon name="arrow-down" size={10} stroke={2} />{git.behind}</span>{/if}
        {#if git.files.length}
          <span>{plural(git.files.length, 'file')}</span>
          <span class="lines np-mono"><span class="add">+{git.added}</span><span class="del">−{git.deleted}</span></span>
        {:else}
          <span>no changes</span>
        {/if}
      </span>
      <Icon name={gitOpen ? 'chevron-down' : 'chevron-up'} size={11} />
    </button>
  {/if}
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
  /* the row a click selected (a double click is what opens a file) */
  .frow[aria-selected='true'] {
    background: var(--bg-3);
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
  /* a row's texts (name, folder, size, status, counts): one baseline, at the name's line height */
  .tx {
    flex: 1;
    gap: 4px;
    min-width: 0;
    height: 1lh;
    overflow: hidden;
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
  /* the git line: a button that lists the changed files */
  .git {
    gap: 6px;
    width: 100%;
    border: 0;
    border-top: 1px solid var(--border);
    background: transparent;
    font-family: inherit;
    line-height: inherit;
    text-align: left;
    cursor: pointer;
  }
  .git:hover,
  .git.open {
    color: var(--fg);
  }
  .git:hover {
    background: var(--bg-2);
  }
  .git:focus-visible {
    outline: none;
    box-shadow: inset 0 0 0 1px var(--accent-line);
  }
  .git .gl {
    height: 1lh; /* the smaller mono counts would make the line taller than the search footer */
  }
  .git .br {
    flex: 0 1 auto; /* shrinks for a long branch name, but the counts stay next to it */
  }
  .ab {
    color: var(--fg-dim);
  }
  /* an arrow, not ↑ ↓: at this size those read as a 1 */
  .ab :global(.np-icon) {
    margin-right: 1px;
    vertical-align: -1px;
  }
  .lines {
    display: inline-flex;
    flex: none;
    gap: 4px;
    font-size: 10.5px;
  }
  .add {
    color: var(--ok);
  }
  .del {
    color: var(--err);
  }
  .ltitle {
    padding: 2px 6px 4px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
  .st {
    flex: none;
    width: 12px;
    text-align: center;
    font-size: 10.5px;
    font-weight: 600;
    color: var(--fg-dim);
  }
  .change[data-status='modified'] .st {
    color: var(--warn);
  }
  .change[data-status='added'] .st,
  .change[data-status='new'] .st {
    color: var(--ok);
  }
  .change[data-status='deleted'] .st,
  .change[data-status='conflict'] .st {
    color: var(--err);
  }
  .change[data-status='renamed'] .st,
  .change[data-status='copied'] .st {
    color: var(--info);
  }
  .change.gone {
    cursor: default;
  }
  .change.gone .fname {
    color: var(--fg-dim);
    text-decoration: line-through;
  }
</style>
