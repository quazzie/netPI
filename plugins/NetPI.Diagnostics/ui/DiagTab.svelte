<script>
  import { onMount } from 'svelte';
  import { Segmented, IconButton, Empty, duration } from '@netpi/kit';
  import PluginsView from './PluginsView.svelte';
  import ToolsView from './ToolsView.svelte';
  import RpcView from './RpcView.svelte';
  import EventsView from './EventsView.svelte';
  import LogsView from './LogsView.svelte';
  import ContextView from './ContextView.svelte';
  import CallsView from './CallsView.svelte';

  let { ctx } = $props();

  let snap = $state.raw(null);
  let error = $state('');
  let loading = $state(false);
  let visible = $state(true);
  let dirty = false;
  let view = $state(load('view', 'plugins'));
  // what looks wrong (diag.problems), polled while the tab is visible
  let problems = $state.raw([]);
  let problemsOpen = $state(false);

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

  async function loadProblems() {
    try {
      problems = (await ctx.rpc('diag.problems')) ?? [];
    } catch {
      problems = [];
    }
  }

  onMount(() => {
    refresh();
    loadProblems();
    const poll = setInterval(() => visible && loadProblems(), 5000);
    const offs = [ctx.on('plugins.changed', soon), ctx.on('ui.changed', soon), ctx.on('tools.changed', soon)];
    return () => {
      offs.forEach((o) => o());
      clearTimeout(timer);
      clearInterval(poll);
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
    { value: 'calls', label: 'Calls', icon: 'zap', title: 'Model calls' },
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
  {#if problems.some((p) => p.severity !== 'info')}
    {@const worst = problems.filter((p) => p.severity !== 'info')}
    <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
    <div class="problems" data-sev={worst[0].severity} onclick={() => (problemsOpen = !problemsOpen)} title="diag.problems">
      <div class="ptitle">{worst.length} problem{worst.length === 1 ? '' : 's'}{problemsOpen ? '' : ': ' + worst[0].message}</div>
      {#if problemsOpen}
        {#each problems as p, i (i)}
          <div class="p" data-sev={p.severity}><b>{p.area}</b> {p.message}{#if p.hint}<span class="hint np-mono"> → {p.hint}</span>{/if}</div>
        {/each}
      {/if}
    </div>
  {/if}
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
    {:else if view === 'calls'}
      <CallsView {ctx} {visible} />
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
  .problems {
    margin: 8px 10px 0 12px;
    padding: 6px 8px;
    border-radius: var(--radius-sm);
    background: var(--warn-soft);
    color: var(--warn);
    font-size: var(--fs-xs);
    cursor: pointer;
  }
  .problems[data-sev='error'] {
    background: var(--err-soft);
    color: var(--err);
  }
  .ptitle {
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .p {
    margin-top: 4px;
    color: var(--fg-muted);
    overflow-wrap: anywhere;
  }
  .p[data-sev='error'] b {
    color: var(--err);
  }
  .p[data-sev='warn'] b {
    color: var(--warn);
  }
  .hint {
    color: var(--fg-dim);
    font-size: 10.5px;
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
