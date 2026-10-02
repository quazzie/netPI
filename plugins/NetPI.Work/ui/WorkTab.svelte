<script>
  import { onMount } from 'svelte';
  import { Section, Empty, IconButton, Menu, tokens, usd } from '@netpi/kit';
  import AgentPool from './AgentPool.svelte';
  import RecentAgent from './RecentAgent.svelte';
  import ProcessRow from './ProcessRow.svelte';
  import { TERMINAL, upsert } from './util.js';

  /** ctx: host plugin API (docs/PROTOCOL.md → Plugin UI tabs) */
  let { ctx } = $props();

  let slots = $state.raw(null); // the agents with their instances (agents.list)
  let disposed = false;
  let agents = $state.raw(null);
  let processes = $state.raw(null);
  let usage = $state.raw(null);
  let errors = $state.raw({});
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
        if (disposed) return;
        slots = s?.agents ?? null;
        agents = s?.runs ?? null;
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
    if (disposed) return;
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

  /** Called by main.js (onShow / onHide). Events are ignored while hidden; a fresh snapshot is taken on show. */
  export function setVisible(v) {
    visible = v;
    if (v && dirty) refresh();
  }

  onMount(() => {
    refresh();
    loadTitles();
    const offs = [
      ctx.on('agent.status', (d) => {
        if (!visible) return void (dirty = true);
        agents = upsert(agents, d?.agent);
      }),
      ctx.on('agents.changed', (d) => {
        if (!visible) return void (dirty = true);
        if (Array.isArray(d?.agents)) slots = d.agents;
      }),
      ctx.on('resources.changed', () => scheduleRefresh()),
      ctx.on('resources.released', () => scheduleRefresh()),
      ctx.on('rpc.changed', () => scheduleRefresh(600)),
      ctx.on('services.changed', () => scheduleRefresh(600)),
      ctx.on('process.started', (d) => {
        if (!visible) return void (dirty = true);
        processes = upsert(processes, d?.process);
      }),
      ctx.on('process.exited', (d) => {
        if (!visible) return void (dirty = true);
        processes = upsert(processes, d?.process);
      }),
      // a turn's tokens, then the ledger's costs (coalesced into one refresh)
      ctx.on('usage.recorded', () => scheduleRefresh(400)),
      ctx.on('usage.changed', () => scheduleRefresh(400)),
      ctx.on('session.created', (d) => setTitle(d?.session)),
      ctx.on('session.updated', (d) => setTitle(d?.session)),
      ctx.on('plugins.changed', () => scheduleRefresh(600)),
    ];
    // safety net: a slow full refresh while visible (elapsed labels tick on their own)
    const slow = setInterval(() => visible && refresh(), 30_000);
    return () => {
      disposed = true;
      offs.forEach((off) => off());
      clearInterval(slow);
      clearTimeout(refreshTimer);
    };
  });

  // ------------------------------------------------------------------ derived views
  // `agents` here are the runs (what each chat or subagent is doing); the agent rows join them to the instances by run id
  const agentById = $derived(new Map((agents ?? []).map((a) => [a.id, a])));
  const recent = $derived(
    (agents ?? [])
      .filter((a) => TERMINAL.has(a.status))
      .sort((a, b) => (Date.parse(b.finishedAt ?? b.createdAt) || 0) - (Date.parse(a.finishedAt ?? a.createdAt) || 0)),
  );
  const failedCount = $derived(recent.filter((a) => a.status === 'failed').length);

  // Commands: a foreground one shows on the row of the chat that runs it (that is where the agent is, and a row does not
  // change size when a command starts or ends); the Background section lists the ones that outlive a tool call, and any
  // foreground one that has no row to show on. Finished ones go in the Finished section.
  const fgRunning = $derived((processes ?? []).filter((p) => p.status === 'running' && !p.background));
  const bgRunning = $derived((processes ?? []).filter((p) => p.status === 'running' && p.background));
  const finishedProcs = $derived(
    (processes ?? [])
      .filter((p) => p.status !== 'running')
      .sort((a, b) => (Date.parse(b.endedAt ?? b.startedAt) || 0) - (Date.parse(a.endedAt ?? a.startedAt) || 0)),
  );
  const cmdsBySession = $derived.by(() => {
    const m = new Map();
    for (const p of fgRunning) {
      const list = m.get(p.sessionId);
      if (list) list.push(p);
      else m.set(p.sessionId, [p]);
    }
    for (const list of m.values()) list.sort((a, b) => (Date.parse(b.startedAt) || 0) - (Date.parse(a.startedAt) || 0));
    return m;
  });
  const rowSessions = $derived(new Set((slots ?? []).flatMap((p) => (p.owners ?? []).map((o) => o.sessionId ?? agentById.get(o.agentId)?.sessionId))));
  const listedProcs = $derived([...bgRunning, ...fgRunning.filter((p) => !rowSessions.has(p.sessionId))]);

  // what each agent has done this period (usage.summary.models), and the token budget its provider has today
  const usageByAgent = $derived.by(() => {
    const m = new Map();
    for (const u of usage?.models ?? []) {
      if (!u.agent) continue;
      const t = m.get(u.agent) ?? { calls: 0, input: 0, output: 0, cacheRead: 0, cost: 0 };
      t.calls += u.calls ?? 0;
      t.input += u.inputTokens ?? 0;
      t.output += u.outputTokens ?? 0;
      t.cacheRead += u.cacheReadTokens ?? 0;
      t.cost += u.costUsd ?? 0;
      m.set(u.agent, t);
    }
    return m;
  });
  const tokenBudgets = $derived(new Map((usage?.providers ?? []).filter((u) => u.budgetTokens).map((u) => [u.provider, u])));
  const budget = $derived(usage?.budget && (usage.budget.monthlyUsd || usage.budget.dailyUsd || usage.budget.spentUsd > 0) ? usage.budget : null);

  // the agents the user set up (always listed) and model calls without an agent (a chip in the summary while they run)
  const setUp = $derived((slots ?? []).filter((p) => p.configured));
  const others = $derived((slots ?? []).filter((p) => !p.configured));
  const usableAgents = $derived(setUp.filter((p) => p.available !== false && !p.disabled));
  const working = $derived(usableAgents.reduce((n, p) => n + (p.owners?.length ?? 0), 0));
  const waiting = $derived(usableAgents.reduce((n, p) => n + (p.waiters?.length ?? 0), 0));
  const free = $derived(usableAgents.reduce((n, p) => n + Math.max(0, (p.capacity ?? 0) - (p.owners?.length ?? 0)), 0));
  const otherCalls = $derived(others.flatMap((p) => (p.owners ?? []).map((o) => ({ pool: p, o }))));
  const otherItems = $derived(
    otherCalls.map(({ pool, o }) => ({
      label: titles.get(o.sessionId ?? agentById.get(o.agentId)?.sessionId) ?? agentById.get(o.agentId)?.name ?? o.label ?? o.agentId,
      hint: pool.key,
      onclick: () => o.sessionId && ctx.app.openSession(o.sessionId),
    })),
  );
</script>

<div class="work">
  <div class="summary">
    <!-- stats that do not fit are dropped whole, least important last -->
    <span class="stats np-fit">
      <span class="stat" title="Instances running a chat or subagent"><b>{working}</b> working</span>
      <span class="stat" class:warn={waiting > 0} title="Runs waiting for a free instance"><b>{waiting}</b> waiting</span>
      <span class="stat" class:ok={free > 0} title="Free instances, ready for work"><b>{free}</b> free</span>
      {#if otherCalls.length}
        <Menu items={otherItems} minWidth={220} placement="bottom-start">
          {#snippet trigger({ toggle })}
            <button class="stat other" title="Model calls that run without an agent (a summarizer on another model)" onclick={toggle}><b>+{otherCalls.length}</b> other</button>
          {/snippet}
        </Menu>
      {/if}
    </span>
    <IconButton icon="refresh" title={updatedAt ? `Refresh (updated ${new Date(updatedAt).toLocaleTimeString()})` : 'Refresh'} size="sm" onclick={refresh} />
  </div>

  {#if failed && !agents && !slots}
    <Empty icon="alert">Work overview unavailable: {failed}</Empty>
  {:else if loading && !agents && !slots}
    <Empty><span class="np-spinner"></span></Empty>
  {:else}
    <!-- ---------------------------------------------------------------- the budget, when one is set -->
    {#if budget}
      {@const b = budget}
      {@const frac = b.monthlyUsd ? Math.min(1, b.spentUsd / b.monthlyUsd) : null}
      <div class="usage budget" title="Paid models since {b.periodStart}; the budget is set in Settings → Agents & budget">
        <div class="uline np-line np-baseline">
          <span class="uprov np-grow">This month</span>
          <span class="np-mono" class:warn={b.warning && !b.exhausted} class:err={b.exhausted}
            >{usd(b.spentUsd)}{#if b.monthlyUsd}<span class="np-dim">&nbsp;/ {usd(b.monthlyUsd)}</span>{/if}</span
          >
        </div>
        <div class="umeta np-line">
          <span class="np-grow">today {usd(b.todayUsd)}{#if b.dailyUsd}<span class="np-dim">&nbsp;/ {usd(b.dailyUsd)}</span>{/if}</span>
          {#if b.exhausted}<span class="err">{b.onLimit === 'ask' ? 'spent · chats ask' : 'spent · paid calls stop'}</span>{/if}
        </div>
        {#if frac != null}
          <div class="np-progress" style="--value: {frac}" data-tone={b.exhausted ? 'err' : b.warning ? 'warn' : undefined}></div>
        {/if}
      </div>
    {/if}

    <!-- ---------------------------------------------------------------- agents: who works on what, what is free, what each has done -->
    <div class="agents">
      {#if !slots}
        <div class="na">Agents not available{errors.agents ? ` — ${errors.agents}` : ''}</div>
      {:else}
        {#each setUp as pool (pool.key)}
          <AgentPool {pool} {agentById} {titles} {ctx} cmds={cmdsBySession} use={usageByAgent.get(pool.key) ?? null} budget={tokenBudgets.get(pool.provider) ?? null} period={usage?.budget?.periodStart ?? ''} />
        {:else}
          <div class="na">
            No agents set up: chats run on their model.
            {#if ctx.app.openSettings}<button class="link" onclick={() => ctx.app.openSettings('agents')}>Set up agents</button>{/if}
          </div>
        {/each}
      {/if}
      {#if errors.runs}<div class="na">Runs not available — {errors.runs}</div>{/if}
      {#if errors.usage}<div class="na">Usage not available — {errors.usage}</div>{/if}
    </div>

    <!-- ---------------------------------------------------------------- background commands (the ones that outlive a tool call) -->
    <Section title="Background" count={processes ? (listedProcs.length ? `${listedProcs.length} running` : null) : null} collapsible open={false} storageKey="work.v2.background">
      {#if !processes}
        <div class="na">Processes not available{errors.processes ? ` — ${errors.processes}` : ''}</div>
      {:else if !listedProcs.length}
        <div class="na">Nothing runs in the background</div>
      {:else}
        {#each listedProcs as p (p.id)}
          <ProcessRow proc={p} {ctx} />
        {/each}
      {/if}
    </Section>

    <!-- ---------------------------------------------------------------- finished work: runs and commands -->
    <Section title="Finished" count={recent.length || finishedProcs.length ? `${recent.length} runs${failedCount ? ` · ${failedCount} failed` : ''} · ${finishedProcs.length} commands` : null} collapsible open={false} storageKey="work.v2.finished">
      {#if !agents && !processes}
        <div class="na">Nothing available{errors.runs ? ` — ${errors.runs}` : ''}</div>
      {:else if !recent.length && !finishedProcs.length}
        <div class="na">Nothing has finished yet</div>
      {:else}
        {#if recent.length}
          <div class="sub first">Runs</div>
          {#each showAllRecent ? recent : recent.slice(0, 6) as a (a.id)}
            <RecentAgent agent={a} {titles} {ctx} />
          {/each}
          {#if recent.length > 6}
            <button class="more" onclick={() => (showAllRecent = !showAllRecent)}>
              {showAllRecent ? 'Show less' : `Show all ${recent.length}`}
            </button>
          {/if}
        {/if}
        {#if finishedProcs.length}
          <div class="sub" class:first={!recent.length}>Commands</div>
          {#each showAllProcs ? finishedProcs : finishedProcs.slice(0, 6) as p (p.id)}
            <ProcessRow proc={p} {ctx} />
          {/each}
          {#if finishedProcs.length > 6}
            <button class="more" onclick={() => (showAllProcs = !showAllProcs)}>
              {showAllProcs ? 'Show less' : `Show all ${finishedProcs.length}`}
            </button>
          {/if}
        {/if}
      {/if}
    </Section>
  {/if}
</div>

<style>
  .work {
    /* fill the tab (.plugin-root is a flex column; a percentage min-height would not resolve) */
    flex: 1 0 auto;
    display: flex;
    flex-direction: column;
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
  .ok {
    color: var(--ok);
  }
  .stat.warn b,
  .stat.ok b {
    color: inherit;
  }
  .other {
    padding: 0;
    border: 0;
    background: none;
    color: inherit;
    font: inherit;
    cursor: pointer;
  }
  .other:hover {
    text-decoration: underline;
  }
  .agents {
    padding: 4px 12px 6px;
    border-bottom: 1px solid var(--border);
  }
  .usage.budget {
    padding: 6px 12px 8px;
    border-bottom: 1px solid var(--border);
  }
  .err {
    color: var(--err);
  }
  .na {
    padding: 4px 0 2px;
    font-size: var(--fs-sm);
    color: var(--fg-dim);
  }
  .link {
    padding: 0;
    border: 0;
    background: none;
    color: var(--accent);
    font: inherit;
    cursor: pointer;
  }
  .link:hover {
    text-decoration: underline;
  }
  .sub.first {
    margin-top: 2px;
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
  /* one line tall: the smaller mono figures share the name's baseline without making the line taller */
  .uline {
    height: 1lh;
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
