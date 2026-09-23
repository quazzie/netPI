<script>
  import { onMount } from 'svelte';
  import { Segmented, IconButton, Empty, duration } from '@netpi/kit';
  import PluginsView from './PluginsView.svelte';
  import ToolsView from './ToolsView.svelte';
  import RpcView from './RpcView.svelte';
  import EventsView from './EventsView.svelte';
  import LogsView from './LogsView.svelte';
  import ContextView from './ContextView.svelte';

  let { ctx } = $props();

  let snap = $state.raw(null);
  let error = $state('');
  let loading = $state(false);
  let visible = $state(true);
  let dirty = false;
  let view = $state(load('view', 'plugins'));

  function load(k, d) {
    try {
      return localStorage.getItem(`netpi.diag.${k}`) ?? d;
    } catch {
      return d;
    }
  }
  $effect(() => {
    try {
      localStorage.setItem('netpi.diag.view', view);
    } catch {}
  });

  async function refresh() {
    loading = true;
    try {
      snap = await ctx.rpc('diag.snapshot', { events: 300 });
      error = '';
    } catch (e) {
      error = e?.message ?? String(e);
    } finally {
      loading = false;
      dirty = false;
    }
  }

  export function setVisible(v) {
    visible = v;
    if (v && dirty) refresh();
  }

  let timer = 0;
  function soon() {
    if (!visible) return void (dirty = true);
    clearTimeout(timer);
    timer = setTimeout(refresh, 300);
  }

  onMount(() => {
    refresh();
    const offs = [ctx.on('plugins.changed', soon), ctx.on('ui.changed', soon), ctx.on('tools.changed', soon)];
    return () => {
      offs.forEach((o) => o());
      clearTimeout(timer);
    };
  });

  const part = (x) => (x && !Array.isArray(x) && x.error ? null : x);
  const plugins = $derived(part(snap?.plugins) ?? null);
  const tools = $derived(part(snap?.tools) ?? null);
  const rpcs = $derived(part(snap?.rpc) ?? null);
  const rt = $derived(snap?.runtime && !snap.runtime.error ? snap.runtime : null);
  const failedPlugins = $derived((plugins ?? []).filter((p) => p.state === 'failed').length);

  const options = $derived([
    { value: 'plugins', label: 'Plugins', count: failedPlugins || null, title: failedPlugins ? `${failedPlugins} failed` : undefined },
    { value: 'tools', label: 'Tools', title: tools ? `${new Set(tools.map((t) => t.name)).size} tools` : undefined },
    { value: 'rpc', label: 'RPC', title: rpcs ? `${rpcs.length} methods` : undefined },
    { value: 'events', label: 'Events' },
    { value: 'logs', label: 'Logs' },
    { value: 'context', label: 'Context' },
  ]);
</script>

<div class="diag">
  <div class="rt">
    {#if rt}
      <span class="np-mono" title="process id">pid {rt.pid}</span>
      <span title={rt.framework}>{rt.framework?.split(' (')[0]}</span>
      <span title="working set (GC heap {rt.gcHeapMb} MB)">{rt.workingSetMb} MB</span>
      <span title="threads">{rt.threads} thr</span>
      <span title="uptime">up {duration((rt.uptimeSeconds ?? 0) * 1000)}</span>
    {:else if error}
      <span class="err">{error}</span>
    {:else}
      <span class="np-dim">loading…</span>
    {/if}
    <span class="np-spacer"></span>
    <IconButton icon="refresh" title="Refresh" size="sm" disabled={loading} onclick={refresh} />
  </div>
  <div class="views np-scroll">
    <Segmented {options} bind:value={view} class="seg" />
  </div>

  <div class="content">
    {#if !snap && error}
      <Empty icon="alert">Diagnostics unavailable: {error}</Empty>
    {:else if view === 'plugins'}
      <PluginsView {plugins} {ctx} onchanged={soon} error={snap?.plugins?.error} />
    {:else if view === 'tools'}
      <ToolsView {tools} error={snap?.tools?.error} />
    {:else if view === 'rpc'}
      <RpcView {rpcs} />
    {:else if view === 'events'}
      <EventsView initial={snap?.events ?? []} {ctx} {visible} />
    {:else if view === 'logs'}
      <LogsView initial={snap?.logs ?? null} {ctx} {visible} />
    {:else if view === 'context'}
      <ContextView {ctx} {visible} />
    {/if}
  </div>
</div>

<style>
  .diag {
    display: flex;
    flex-direction: column;
    min-height: 100%;
  }
  .rt {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 5px 6px 5px 12px;
    font-size: var(--fs-xs);
    color: var(--fg-muted);
    white-space: nowrap;
    overflow: hidden;
    border-bottom: 1px solid var(--border);
  }
  .err {
    color: var(--err);
  }
  .views {
    padding: 8px 10px 4px 12px;
    overflow-x: auto;
    scrollbar-width: none;
  }
  .views :global(.seg) {
    display: flex;
    width: max-content;
    min-width: 100%;
  }
  .views :global(.seg > button) {
    flex: 1;
    justify-content: center;
    padding: 0 6px;
  }
  .views :global(.seg .np-seg-count) {
    color: var(--err);
    font-weight: 700;
  }
  .content {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
  }
</style>
