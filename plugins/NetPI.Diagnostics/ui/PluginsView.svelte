<script>
  import { StatusDot, IconButton, SearchInput, Menu, TimeAgo, Empty, desktop, copyText } from '@netpi/kit';

  let { plugins, ctx, onchanged, error } = $props();
  let q = $state('');
  let busy = $state.raw(new Set());
  let open = $state.raw(new Set());
  let menu = $state();

  const STATE = { running: 'ok', loading: 'running', failed: 'error', stopped: 'warn', disabled: 'idle', unloaded: 'idle' };
  const ORDER = ['failed', 'running', 'loading', 'stopped', 'disabled', 'unloaded'];
  const sorted = $derived(
    (plugins ?? [])
      .filter((p) => !q || `${p.id} ${p.name} ${p.description ?? ''}`.toLowerCase().includes(q.toLowerCase()))
      .sort((a, b) => (b.state === 'failed') - (a.state === 'failed') || (a.order ?? 0) - (b.order ?? 0) || a.id.localeCompare(b.id)),
  );
  const counts = $derived.by(() => {
    const c = {};
    for (const p of plugins ?? []) c[p.state] = (c[p.state] ?? 0) + 1;
    return c;
  });

  function mark(id, on) {
    const s = new Set(busy);
    on ? s.add(id) : s.delete(id);
    busy = s;
  }
  async function reload(p) {
    mark(p.id, true);
    try {
      await ctx.rpc('plugins.reload', { id: p.id });
      ctx.app.toast(`Reloaded ${p.name}`);
    } catch (e) {
      ctx.app.toast(`Reload failed: ${e.message}`, 'error');
    } finally {
      mark(p.id, false);
      onchanged?.();
    }
  }
  async function setEnabled(p, enabled) {
    mark(p.id, true);
    try {
      await ctx.rpc('plugins.setEnabled', { id: p.id, enabled });
      ctx.app.toast(`${p.name} ${enabled ? 'enabled' : 'disabled'}`);
    } catch (e) {
      ctx.app.toast(e.message, 'error');
    } finally {
      mark(p.id, false);
      onchanged?.();
    }
  }
  function toggle(id) {
    const s = new Set(open);
    s.has(id) ? s.delete(id) : s.add(id);
    open = s;
  }
  function more(e, p) {
    e.stopPropagation();
    menu.openFor(e.currentTarget, [
      { label: 'Reload', icon: 'refresh', onclick: () => reload(p) },
      p.enabled
        ? { label: 'Disable', icon: 'ban', danger: true, onclick: () => setEnabled(p, false) }
        : { label: 'Enable', icon: 'circle-check', onclick: () => setEnabled(p, true) },
      { divider: true },
      { label: 'Copy id', icon: 'copy', onclick: () => copyText(p.id) },
      { label: 'Copy folder path', icon: 'copy', onclick: () => copyText(p.directory) },
      ...(desktop.available ? [{ label: 'Reveal folder', icon: 'folder-open', onclick: () => desktop.revealPath(p.directory) }] : []),
    ]);
  }
</script>

<Menu bind:this={menu} />
<div class="bar">
  <SearchInput bind:value={q} placeholder="Filter plugins" />
</div>
{#if !plugins}
  <Empty icon="puzzle">{error ?? 'No plugin manager'}</Empty>
{:else}
  <div class="counts np-fit">
    {#each ORDER.filter((s) => counts[s]) as s (s)}<span class="c" data-s={s}><StatusDot status={STATE[s] ?? 'idle'} />{counts[s]} {s}</span>{/each}
  </div>
  <div class="list">
    {#each sorted as p (p.id)}
      <div class="pl np-hover-row" class:is-active={p.state === 'failed'} data-state={p.state}>
        <button class="row np-line" onclick={() => toggle(p.id)} aria-expanded={open.has(p.id)} title={p.description ?? p.name}>
          <StatusDot status={STATE[p.state] ?? 'idle'} title={p.state} />
          <span class="name np-grow">{p.name}</span>
          {#if p.state !== 'running'}<span class="st">{p.state}</span>{/if}
        </button>
        <div class="np-meta sub" title="{p.id} · v{p.version ?? '?'} · loaded in {p.loadMs ?? 0} ms · {p.loadCount ?? 0} load(s)">
          <span class="np-mono">{p.id}</span>{#if p.version}<span>v{p.version}</span>{/if}{#if p.state === 'running' || p.loadCount}<span>{p.loadMs ?? 0} ms</span><span>{p.loadCount ?? 0}×</span>{/if}
        </div>
        <span class="np-hover-actions">
          {#if busy.has(p.id)}<span class="spin"><span class="np-spinner"></span></span>{:else}
            <IconButton icon="refresh" title="Reload {p.name}" size="sm" disabled={!p.enabled} onclick={() => reload(p)} />
          {/if}
          <IconButton icon="more" title="More" size="sm" onclick={(e) => more(e, p)} />
        </span>
        {#if p.error}
          <button class="error np-mono" class:clamp={!open.has(p.id)} onclick={() => toggle(p.id)} title={open.has(p.id) ? undefined : 'Show the full error'}
            ><span class="txt">{p.error}</span></button
          >
        {/if}
        {#if open.has(p.id)}
          <dl class="np-kv details">
            <dt>Folder</dt><dd class="np-mono" title={p.directory}>{p.directory}</dd>
            {#if p.assembly}<dt>Assembly</dt><dd class="np-mono" title={p.assembly}>{p.assembly}</dd>{/if}
            <dt>Order</dt><dd>{p.order}</dd>
            <dt>Enabled</dt><dd>{p.enabled ? 'yes' : 'no'}</dd>
            {#if p.loadedAt}<dt>Loaded</dt><dd><TimeAgo time={p.loadedAt} /></dd>{/if}
            {#if p.description}<dt>About</dt><dd class="wrap">{p.description}</dd>{/if}
          </dl>
        {/if}
      </div>
    {:else}
      <Empty>No plugins match “{q}”</Empty>
    {/each}
  </div>
{/if}

<style>
  .bar {
    padding: 4px 10px 4px 12px;
  }
  .counts {
    padding: 0 12px 4px;
    font-size: var(--fs-xs);
    color: var(--fg-muted);
  }
  .c {
    display: inline-flex;
    align-items: center;
    gap: 5px;
  }
  .c[data-s='failed'] {
    color: var(--err);
  }
  .list {
    padding: 0 8px 10px 10px;
  }
  .pl {
    padding: 3px 4px 4px;
    border-radius: var(--radius-sm);
  }
  .pl + .pl {
    margin-top: 1px;
  }
  .pl:hover,
  .pl:focus-within {
    --row-bg: var(--bg-2);
    background: var(--bg-2);
  }
  .row {
    width: 100%;
    min-height: 22px;
    padding: 0;
    border: 0;
    background: transparent;
    color: var(--fg);
    font: inherit;
    text-align: left;
    cursor: pointer;
  }
  .name {
    font-weight: 600;
  }
  .st {
    font-size: var(--fs-xs);
    padding: 0 6px;
    border-radius: 8px;
    line-height: 16px;
    background: var(--bg-3);
    color: var(--fg-muted);
  }
  [data-state='failed'] .st {
    background: var(--err-soft);
    color: var(--err);
  }
  [data-state='disabled'] .name {
    color: var(--fg-dim);
  }
  .sub {
    padding-left: 13px;
    font-size: 10.5px;
  }
  .sub .np-mono {
    font-size: 10.5px;
  }
  .spin {
    display: inline-grid;
    place-items: center;
    width: 22px;
    height: 22px;
  }
  .error {
    display: block;
    width: calc(100% - 13px);
    margin: 4px 0 2px 13px;
    padding: 5px 8px;
    border: 0;
    border-radius: var(--radius-sm);
    background: var(--err-soft);
    color: var(--err);
    font-size: 11px;
    text-align: left;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    cursor: pointer;
  }
  .error.clamp .txt {
    display: -webkit-box;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 3;
    line-clamp: 3;
    overflow: hidden;
  }
  .details {
    margin: 6px 0 2px 13px;
    font-size: var(--fs-xs);
  }
  .details dd {
    white-space: nowrap;
  }
  .details .wrap {
    white-space: normal;
  }
</style>
