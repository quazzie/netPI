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
    {
      value: 'plugins',
      label: 'Plugins',
      icon: 'puzzle',
      count: failedPlugins || null,
      tone: 'err',
      title: failedPlugins ? `Plugins (${failedPlugins} failed)` : 'Plugins',
    },
    { value: 'tools', label: 'Tools', icon: 'wrench', title: tools ? `Tools (${new Set(tools.map((t) => t.name)).size})` : 'Tools' },
    { value: 'rpc', label: 'RPC', icon: 'zap', title: rpcs ? `RPC methods (${rpcs.length})` : 'RPC methods' },
    { value: 'events', label: 'Events', icon: 'activity', title: 'Live events' },
    { value: 'logs', label: 'Logs', icon: 'list', title: 'Log' },
    { value: 'context', label: 'Context', icon: 'layers', title: 'Context of the active session' },
  ]);
  const rtTip = $derived(
    rt
      ? `pid ${rt.pid} · ${rt.framework ?? ''} · ${rt.os ?? ''}\nworking set ${rt.workingSetMb} MB · GC heap ${rt.gcHeapMb} MB · ${rt.threads} threads · up ${duration((rt.uptimeSeconds ?? 0) * 1000)}`
      : undefined,
  );
</script>

<div class="diag">
  <div class="rt">
    <!-- items that do not fit are dropped whole (least important last); the tooltip has everything -->
    <div class="np-fit facts" title={rtTip}>
      {#if rt}
        <span class="np-mono">pid {rt.pid}</span>
        <span>{rt.workingSetMb} MB</span>
        <span>up {duration((rt.uptimeSeconds ?? 0) * 1000)}</span>
        <span>{rt.threads} thr</span>
        <span>{rt.framework?.split(' (')[0]}</span>
      {:else if error}
        <span class="err">{error}</span>
      {:else}
        <span class="np-dim">loading…</span>
      {/if}
    </div>
    <IconButton icon="refresh" title="Refresh" size="sm" disabled={loading} onclick={refresh} />
  </div>
  <div class="views">
    <Segmented {options} bind:value={view} />
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
    gap: 6px;
    padding: 4px 6px 4px 12px;
    font-size: var(--fs-xs);
    color: var(--fg-muted);
    border-bottom: 1px solid var(--border);
  }
  .facts {
    flex: 1;
    column-gap: 9px;
  }
  .err {
    color: var(--err);
  }
  .views {
    display: flex;
    padding: 8px 10px 4px 12px;
  }
  .views :global(.np-seg) {
    width: 100%;
  }
  .views :global(.np-seg > button) {
    flex: 1 1 auto;
    justify-content: center;
    padding: 0 6px;
  }
  .content {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
  }
</style>
