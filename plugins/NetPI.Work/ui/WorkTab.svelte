<script>
  import { onMount } from 'svelte';
  import { Section, Empty, IconButton, tokens } from '@netpi/kit';
  import LanePool from './LanePool.svelte';
  import AgentNode from './AgentNode.svelte';
  import RecentAgent from './RecentAgent.svelte';
  import ProcessRow from './ProcessRow.svelte';
  import { ACTIVE, TERMINAL, upsert } from './util.js';

  /** ctx: host plugin API (docs/PROTOCOL.md → Plugin UI tabs) */
  let { ctx } = $props();

  let lanes = $state.raw(null);
  let agents = $state.raw(null);
  let processes = $state.raw(null);
  let usage = $state.raw(null);
  let errors = $state.raw({});
  let models = $state.raw(new Map()); // ref -> ModelInfo (for model status dots)
  let loading = $state(true);
  let failed = $state('');
  let updatedAt = $state(null);
  let visible = true;
  let dirty = false;
  let titles = $state.raw(new Map()); // sessionId -> title
  let showAllRecent = $state(false);
  let showAllProcs = $state(false);

  // ------------------------------------------------------------------ data
  let refreshTimer = 0;
  let inflight = null;

  async function refresh() {
    clearTimeout(refreshTimer);
    if (inflight) return inflight;
    inflight = (async () => {
      try {
        const s = await ctx.rpc('work.snapshot');
        lanes = s?.lanes ?? null;
        agents = s?.agents ?? null;
        processes = s?.processes ?? null;
        usage = s?.usage ?? null;
        errors = s?.errors ?? {};
        failed = '';
        updatedAt = new Date().toISOString();
      } catch (e) {
        failed = e?.message ?? String(e);
      } finally {
        loading = false;
        dirty = false;
        inflight = null;
      }
    })();
    return inflight;
  }

  function scheduleRefresh(ms = 250) {
    if (!visible) {
      dirty = true;
      return;
    }
    clearTimeout(refreshTimer);
    refreshTimer = setTimeout(refresh, ms);
  }

  async function loadTitles() {
    try {
      const list = await ctx.rpc('sessions.list', { includeSubagents: true, limit: 300 });
      titles = new Map((list ?? []).map((s) => [s.id, s.title]));
    } catch {}
  }
  function setTitle(s) {
    if (!s?.id || titles.get(s.id) === s.title) return;
    const m = new Map(titles);
    m.set(s.id, s.title);
    titles = m;
  }

  async function loadModels() {
    try {
      const res = await ctx.rpc('models.list');
      models = new Map((res?.models ?? []).map((m) => [m.ref ?? `${m.provider}/${m.id}`, m]));
    } catch {}
  }

  /** Called by main.js (onShow / onHide). Events are ignored while hidden; a fresh snapshot is taken on show. */
  export function setVisible(v) {
    visible = v;
    if (v && dirty) refresh();
  }

  onMount(() => {
    refresh();
    loadModels();
    loadTitles();
    const offs = [
      ctx.on('agent.status', (d) => {
        if (!visible) return void (dirty = true);
        agents = upsert(agents, d?.agent);
      }),
      ctx.on('lanes.changed', (d) => {
        if (!visible) return void (dirty = true);
        if (Array.isArray(d?.pools)) lanes = d.pools;
      }),
      ctx.on('process.started', (d) => {
        if (!visible) return void (dirty = true);
        processes = upsert(processes, d?.process);
      }),
      ctx.on('process.exited', (d) => {
        if (!visible) return void (dirty = true);
        processes = upsert(processes, d?.process);
      }),
      ctx.on('usage.recorded', () => scheduleRefresh(400)),
      ctx.on('models.changed', loadModels),
      ctx.on('session.created', (d) => setTitle(d?.session)),
      ctx.on('session.updated', (d) => setTitle(d?.session)),
      ctx.on('plugins.changed', () => scheduleRefresh(600)),
    ];
    // safety net: a slow full refresh while visible (elapsed labels tick on their own)
    const slow = setInterval(() => visible && refresh(), 30_000);
    return () => {
      offs.forEach((off) => off());
      clearInterval(slow);
      clearTimeout(refreshTimer);
    };
  });

  // ------------------------------------------------------------------ derived views
  const agentById = $derived(new Map((agents ?? []).map((a) => [a.id, a])));
  const active = $derived((agents ?? []).filter((a) => ACTIVE.has(a.status)));
  const activeIds = $derived(new Set(active.map((a) => a.id)));
  const roots = $derived(
    active
      .filter((a) => !a.parentAgentId || !activeIds.has(a.parentAgentId))
      .sort((a, b) => (Date.parse(a.startedAt ?? a.createdAt) || 0) - (Date.parse(b.startedAt ?? b.createdAt) || 0)),
  );
  const childrenOf = $derived.by(() => {
    const m = new Map();
    for (const a of active) {
      if (!a.parentAgentId || !activeIds.has(a.parentAgentId)) continue;
      let arr = m.get(a.parentAgentId);
      if (!arr) m.set(a.parentAgentId, (arr = []));
      arr.push(a);
    }
    return m;
  });
  const recent = $derived(
    (agents ?? [])
      .filter((a) => TERMINAL.has(a.status))
      .sort((a, b) => (Date.parse(b.finishedAt ?? b.createdAt) || 0) - (Date.parse(a.finishedAt ?? a.createdAt) || 0)),
  );
  const idleCount = $derived((agents ?? []).filter((a) => a.status === 'idle').length);

  const running = $derived((processes ?? []).filter((p) => p.status === 'running'));
  const finished = $derived(
    (processes ?? [])
      .filter((p) => p.status !== 'running')
      .sort((a, b) => (Date.parse(b.endedAt ?? b.startedAt) || 0) - (Date.parse(a.endedAt ?? a.startedAt) || 0)),
  );

  const busySlots = $derived((lanes ?? []).reduce((n, p) => n + (p.busy ?? 0), 0));
  const queuedSlots = $derived((lanes ?? []).reduce((n, p) => n + (p.queued ?? 0), 0));
  const todayTokens = $derived(
    (usage?.providers ?? []).reduce((n, p) => n + (p.inputTokens ?? 0) + (p.outputTokens ?? 0), 0),
  );
</script>

<div class="work">
  <div class="summary">
    <!-- stats that do not fit are dropped whole, least important last -->
    <span class="stats np-fit">
      <span class="stat" title="Active agents (running, queued or waiting)"><b>{active.length}</b> agents</span>
      <span class="stat" title="Busy lane slots{queuedSlots ? ` · ${queuedSlots} waiting for a slot` : ''}"
        ><b>{busySlots}</b> slots{#if queuedSlots}<span class="warn"> +{queuedSlots}</span>{/if}</span
      >
      <span class="stat" title="Running shell processes"><b>{running.length}</b> proc</span>
      <span class="stat" title="Input + output tokens today"><b>{tokens(todayTokens) || 0}</b> tok</span>
    </span>
    <IconButton icon="refresh" title={updatedAt ? `Refresh (updated ${new Date(updatedAt).toLocaleTimeString()})` : 'Refresh'} size="sm" onclick={refresh} />
  </div>

  {#if failed && !agents && !lanes}
    <Empty icon="alert">Work overview unavailable: {failed}</Empty>
  {:else if loading && !agents && !lanes}
    <Empty><span class="np-spinner"></span></Empty>
  {:else}
    <!-- ---------------------------------------------------------------- lanes -->
    <Section title="Lanes" count={lanes ? `${busySlots}/${lanes.reduce((n, p) => n + (p.capacity ?? 0), 0)}` : null} collapsible storageKey="work.lanes">
      {#if !lanes}
        <div class="na">Lanes not available{errors.lanes ? ` — ${errors.lanes}` : ''}</div>
      {:else}
        {#each lanes as pool (pool.key)}
          <LanePool {pool} {agentById} {models} {ctx} />
        {:else}
          <div class="na">No lane pools yet — they appear with the first model call.</div>
        {/each}
      {/if}
    </Section>

    <!-- ---------------------------------------------------------------- agents -->
    <Section title="Agents" count={active.length || null} collapsible storageKey="work.agents">
      {#if !agents}
        <div class="na">Agents not available{errors.agents ? ` — ${errors.agents}` : ''}</div>
      {:else}
        {#each roots as a (a.id)}
          <AgentNode agent={a} {childrenOf} {titles} {ctx} depth={0} />
        {:else}
          <div class="na">Nothing running{idleCount ? ` · ${idleCount} idle` : ''}</div>
        {/each}
        {#if recent.length}
          <div class="sub">Recent</div>
          {#each showAllRecent ? recent : recent.slice(0, 6) as a (a.id)}
            <RecentAgent agent={a} {titles} {ctx} />
          {/each}
          {#if recent.length > 6}
            <button class="more" onclick={() => (showAllRecent = !showAllRecent)}>
              {showAllRecent ? 'Show less' : `Show all ${recent.length}`}
            </button>
          {/if}
        {/if}
      {/if}
    </Section>

    <!-- ---------------------------------------------------------------- processes -->
    <Section title="Processes" count={processes ? (running.length ? `${running.length} running` : processes.length || null) : null} collapsible storageKey="work.processes">
      {#if !processes}
        <div class="na">Processes not available{errors.processes ? ` — ${errors.processes}` : ''}</div>
      {:else if !processes.length}
        <div class="na">No shell processes yet</div>
      {:else}
        {#each running as p (p.id)}
          <ProcessRow proc={p} {ctx} />
        {/each}
        {#if finished.length}
          {#if running.length}<div class="sub">Recent</div>{/if}
          {#each showAllProcs ? finished : finished.slice(0, 6) as p (p.id)}
            <ProcessRow proc={p} {ctx} />
          {/each}
          {#if finished.length > 6}
            <button class="more" onclick={() => (showAllProcs = !showAllProcs)}>
              {showAllProcs ? 'Show less' : `Show all ${finished.length}`}
            </button>
          {/if}
        {/if}
      {/if}
    </Section>

    <!-- ---------------------------------------------------------------- usage -->
    <Section title="Usage today" count={usage?.providers?.length || null} collapsible storageKey="work.usage">
      {#if !usage}
        <div class="na">Usage not available{errors.usage ? ` — ${errors.usage}` : ''}</div>
      {:else if !usage.providers?.length}
        <div class="na">No model calls today</div>
      {:else}
        {#each usage.providers as u (u.provider)}
          {@const used = u.budgetUsed ?? (u.inputTokens ?? 0) + (u.outputTokens ?? 0) + (u.cacheWriteTokens ?? 0)}
          {@const frac = u.budgetTokens ? Math.min(1, used / u.budgetTokens) : null}
          <div class="usage">
            <div class="uline np-line">
              <span class="uprov np-grow">{u.provider}</span>
              <span class="np-mono unums" title="{(u.inputTokens ?? 0).toLocaleString()} input · {(u.outputTokens ?? 0).toLocaleString()} output · {(u.cacheReadTokens ?? 0).toLocaleString()} cache read tokens">
                {tokens(u.inputTokens) || 0}<span class="np-dim">↑</span>
                {tokens(u.outputTokens) || 0}<span class="np-dim">↓</span>
                {#if u.cacheReadTokens}<span class="np-dim cached">{tokens(u.cacheReadTokens)} cached</span>{/if}
              </span>
            </div>
            <div class="umeta np-line">
              <span class="np-grow">{u.calls ?? 0} calls</span>
              {#if u.budgetTokens}
                <span class:warn={frac >= 0.8 && frac < 1} class:err={frac >= 1} title="Daily budget (input + output + cache write)">{tokens(used)} / {tokens(u.budgetTokens)}</span>
              {/if}
            </div>
            {#if u.budgetTokens}
              <div class="np-progress" style="--value: {frac}" data-tone={frac >= 1 ? 'err' : frac >= 0.8 ? 'warn' : undefined}></div>
            {/if}
          </div>
        {/each}
      {/if}
    </Section>
  {/if}
</div>

<style>
  .work {
    display: flex;
    flex-direction: column;
    min-height: 100%;
  }
  .summary {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 6px 4px 12px;
    border-bottom: 1px solid var(--border);
    font-size: var(--fs-sm);
    color: var(--fg-muted);
  }
  .stats {
    flex: 1;
    column-gap: 11px;
  }
  .stat b {
    color: var(--fg);
    font-weight: 600;
    font-variant-numeric: tabular-nums;
  }
  .warn {
    color: var(--warn);
  }
  .err {
    color: var(--err);
  }
  .na {
    padding: 4px 0 2px;
    font-size: var(--fs-sm);
    color: var(--fg-dim);
  }
  .sub {
    margin: 10px 0 2px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .more {
    margin-top: 4px;
    padding: 2px 0;
    border: 0;
    background: transparent;
    color: var(--accent);
    font-size: var(--fs-xs);
    cursor: pointer;
  }
  .usage {
    display: flex;
    flex-direction: column;
    gap: 3px;
    padding: 4px 0 6px;
  }
  .usage + .usage {
    border-top: 1px dashed var(--border);
    padding-top: 8px;
  }
  .uline,
  .umeta {
    gap: 8px;
  }
  @container (max-width: 259px) {
    .cached {
      display: none;
    }
  }
  .uprov {
    font-weight: 600;
  }
  .unums {
    display: flex;
    gap: 6px;
    font-size: 11.5px;
  }
  .umeta {
    font-size: var(--fs-xs);
    color: var(--fg-dim);
  }
</style>
