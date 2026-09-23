<script>
  import { StatusDot, IconButton, SearchInput, Menu, TimeAgo, Empty, desktop, copyText } from '@netpi/kit';

  let { plugins, ctx, onchanged, error } = $props();
  let q = $state('');
  let busy = $state.raw(new Set());
  let open = $state.raw(new Set());
  let menu = $state();

  const STATE = { running: 'ok', loading: 'running', failed: 'error', stopped: 'warn', disabled: 'idle', unloaded: 'idle' };
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
  <div class="counts">
    {#each Object.entries(counts) as [s, n] (s)}<span class="c" data-s={s}><StatusDot status={STATE[s] ?? 'idle'} />{n} {s}</span>{/each}
  </div>
  <div class="list">
    {#each sorted as p (p.id)}
      <div class="pl" data-state={p.state}>
        <div class="row" role="button" tabindex="0" onclick={() => toggle(p.id)} onkeydown={(e) => e.key === 'Enter' && toggle(p.id)}>
          <StatusDot status={STATE[p.state] ?? 'idle'} title={p.state} />
          <span class="name">{p.name}</span>
          {#if p.state !== 'running'}<span class="st">{p.state}</span>{/if}
          <span class="np-spacer"></span>
          <span class="acts">
            {#if busy.has(p.id)}<span class="np-spinner"></span>{:else}
              <IconButton icon="refresh" title="Reload {p.name}" size="sm" disabled={!p.enabled} onclick={(e) => (e.stopPropagation(), reload(p))} />
            {/if}
            <IconButton icon="more" title="More" size="sm" onclick={(e) => more(e, p)} />
          </span>
        </div>
        <div class="sub np-mono">
          <span>{p.id}</span>
          {#if p.version}<span>v{p.version}</span>{/if}
          {#if p.state === 'running' || p.loadCount}<span title="load time · load count">{p.loadMs ?? 0} ms · {p.loadCount ?? 0}×</span>{/if}
        </div>
        {#if p.error}<div class="error np-mono">{p.error}</div>{/if}
        {#if open.has(p.id)}
          <dl class="np-kv details">
            <dt>Folder</dt><dd class="np-mono" title={p.directory}>{p.directory}</dd>
            {#if p.assembly}<dt>Assembly</dt><dd class="np-mono">{p.assembly}</dd>{/if}
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
    display: flex;
    flex-wrap: wrap;
    gap: 4px 12px;
    padding: 2px 12px 6px;
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
    padding: 0 10px 10px 12px;
  }
  .pl {
    padding: 4px 0 5px;
    border-bottom: 1px solid color-mix(in srgb, var(--border) 60%, transparent);
  }
  .row {
    display: flex;
    align-items: center;
    gap: 7px;
    min-height: 24px;
    padding: 0 2px 0 3px;
    margin: 0 -2px 0 -3px;
    border-radius: 4px;
    cursor: pointer;
  }
  .row:hover {
    background: var(--bg-2);
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
  .acts {
    display: flex;
    align-items: center;
    opacity: 0;
    transition: opacity var(--t-fast);
  }
  .row:hover .acts,
  .row:focus-within .acts,
  [data-state='failed'] .acts {
    opacity: 1;
  }
  .sub {
    display: flex;
    gap: 10px;
    padding-left: 15px;
    font-size: 10.5px;
    color: var(--fg-dim);
  }
  .error {
    margin: 4px 0 2px 15px;
    padding: 5px 8px;
    border-radius: var(--radius-sm);
    background: var(--err-soft);
    color: var(--err);
    font-size: 11px;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .details {
    margin: 6px 0 2px 15px;
    font-size: var(--fs-xs);
  }
  .details dd {
    white-space: nowrap;
  }
  .details .wrap {
    white-space: normal;
  }
</style>
