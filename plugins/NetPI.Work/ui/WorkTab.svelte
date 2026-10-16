<script>
  import { onMount } from 'svelte';
  import { Section, Empty, IconButton, Menu, tokens, usd, useRefresh } from '@netpi/kit';
  import AgentPool from './AgentPool.svelte';
  import WaitList from './WaitList.svelte';
  import ProcessRow from './ProcessRow.svelte';
  import { ACTIVE, upsert } from './util.js';

  /** ctx: host plugin API (docs/PROTOCOL.md → Plugin UI tabs) */
  let { ctx } = $props();

  let slots = $state.raw(null); // the agents with their instances (agents.list)
  let unassigned = $state.raw([]); // runs waiting for any of several agents, in nobody's queue yet (agents.unassigned)
  let agents = $state.raw(null);
  let processes = $state.raw(null);
  let usage = $state.raw(null);
  let errors = $state.raw({});
  let loading = $state(true);
  let failed = $state('');
  let updatedAt = $state(null);
  // sessionId -> title, from the host's own session list (ctx.app.sessionTitle is reactive, so the rows follow a
  // session.updated): no copy of sessions.list here and no session.* handlers to keep one current
  const titles = { get: (id) => (id ? ctx.app.sessionTitle(id) : null) };
  let showAllProcs = $state(false);

  // ------------------------------------------------------------------ data
  async function load() {
    try {
      const s = await ctx.rpc('work.snapshot');
      if (!tab.alive) return;
      slots = s?.agents ?? null;
      unassigned = s?.unassigned ?? [];
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
    }
  }

  // The snapshot follows what can change it, and is polled as a safety net (the elapsed labels tick on their own). What
  // lands while the tab is hidden only marks it stale: a fresh snapshot is taken when it is shown again.
  // svelte-ignore state_referenced_locally
  const tab = useRefresh(ctx, {
    load,
    events: ['resources.changed', 'resources.released', 'rpc.changed', 'services.changed', 'usage.recorded', 'usage.changed', 'plugins.changed'],
    pollMs: 30_000,
    delayMs: 250,
  });

  /** Called by main.js (onShow / onHide). */
  export function setVisible(v) {
    tab.setVisible(v);
  }

  onMount(() => {
    const offs = [
      ctx.on('agent.status', (d) => {
        if (!tab.visible) return void tab.schedule();
        agents = upsert(agents, d?.agent);
      }),
      ctx.on('agents.changed', (d) => {
        if (!tab.visible) return void tab.schedule();
        if (Array.isArray(d?.agents)) slots = d.agents;
        if (Array.isArray(d?.unassigned)) unassigned = d.unassigned;
      }),
      ctx.on('process.started', (d) => {
        if (!tab.visible) return void tab.schedule();
        processes = upsert(processes, d?.process);
      }),
      ctx.on('process.exited', (d) => {
        if (!tab.visible) return void tab.schedule();
        processes = upsert(processes, d?.process);
      }),
    ];
    return () => offs.forEach((off) => off());
  });

  // ------------------------------------------------------------------ derived views
  // `agents` here are the runs (what each chat or subagent is doing); the agent rows join them to the instances by run id
  const agentById = $derived(new Map((agents ?? []).map((a) => [a.id, a])));
  // Chats whose turn ended while their subagents work: no instance of their own, so no row above, yet not done — the
  // subagents' reports start their next run. Without this line such a chat looks finished.
  const waitingChats = $derived.by(() => {
    const kids = new Map();
    for (const a of agents ?? []) {
      if (!a.isSubagent || !a.parentAgentId || !ACTIVE.has(a.status)) continue;
      const list = kids.get(a.parentAgentId);
      if (list) list.push(a);
      else kids.set(a.parentAgentId, [a]);
    }
    const out = [];
    for (const [pid, list] of kids) {
      const parent = agentById.get(pid);
      if (parent && !ACTIVE.has(parent.status)) out.push({ parent, kids: list });
    }
    return out;
  });

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

  // the token budget its provider has today
  const budget = $derived(usage?.budget && (usage.budget.monthlyUsd || usage.budget.dailyUsd || usage.budget.spentUsd > 0) ? usage.budget : null);

  // the agents the user set up (always listed) and model calls without an agent (a chip in the summary while they run). With no
  // agents set up the model calls are all there is: they are listed like agents (a slot per model), while they run.
  const setUp = $derived((slots ?? []).filter((p) => p.configured));
  const others = $derived((slots ?? []).filter((p) => !p.configured));
  // usable agents (or ones still finishing runs) first, in their own order; switched-off and unavailable ones last
  const isOff = (p) => (p.disabled || p.available === false) && !(p.owners?.length || p.waiters?.length);
  const shownAll = $derived(setUp.length ? setUp : others);
  const shown = $derived(shownAll.filter((p) => !isOff(p)));
  const offAgents = $derived(shownAll.filter(isOff));
  const usableAgents = $derived(shown.filter((p) => p.available !== false && !p.disabled));
  const working = $derived(usableAgents.reduce((n, p) => n + (p.owners?.length ?? 0), 0));
  const waiting = $derived(usableAgents.reduce((n, p) => n + (p.waiters?.length ?? 0), 0) + unassigned.length);
  const unassignedLabel = (w) => titles.get(w.sessionId ?? agentById.get(w.agentId)?.sessionId) ?? agentById.get(w.agentId)?.name ?? w.label ?? w.agentId;
  const free = $derived(usableAgents.reduce((n, p) => n + Math.max(0, (p.capacity ?? 0) - (p.owners?.length ?? 0)), 0));
  const otherCalls = $derived((setUp.length ? others : []).flatMap((p) => (p.owners ?? []).map((o) => ({ pool: p, o }))));
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
      <span class="stat" class:warn={waiting > 0} title="Runs queued for a free instance"><b>{waiting}</b> queued</span>
      <span class="stat" class:ok={free > 0} title="Free instances, ready for work"><b>{free}</b> free</span>
      {#if otherCalls.length}
        <Menu items={otherItems} minWidth={220} placement="bottom-start">
          {#snippet trigger({ toggle })}
            <button class="stat other" title="Model calls that run without an agent (a summarizer on another model)" onclick={toggle}><b>+{otherCalls.length}</b> other</button>
          {/snippet}
        </Menu>
      {/if}
    </span>
    <IconButton icon="refresh" title={updatedAt ? `Refresh (updated ${new Date(updatedAt).toLocaleTimeString()})` : 'Refresh'} size="sm" onclick={() => tab.refresh()} />
  </div>

  {#if failed && !agents && !slots}
    <Empty icon="alert">Work overview unavailable: {failed}</Empty>
  {:else if loading && !agents && !slots}
    <Empty><span class="np-spinner"></span></Empty>
  {:else}
    <!-- ---------------------------------------------------------------- the budget, when one is set -->
    {#if budget}
      {@const b = budget}
      {@const spent = b.settledUsd ?? b.spentUsd}
      {@const frac = b.monthlyUsd ? Math.min(1, spent / b.monthlyUsd) : null}
      <div class="usage budget" title="Paid models since {b.periodStart}; the budget is set in Settings → Agents & budget">
        <div class="uline np-line np-baseline">
          <span class="uprov np-grow">This month</span>
          <span class="np-mono" class:warn={b.warning && !b.exhausted} class:err={b.exhausted}
            title={b.reservedOrUnsettledUsd > 0 ? `${usd(b.reservedOrUnsettledUsd)} more is reserved for calls still running` : undefined}
            >{usd(spent)}{#if b.monthlyUsd}<span class="np-dim">&nbsp;/ {usd(b.monthlyUsd)}</span>{/if}</span
          >
        </div>
        <div class="umeta np-line">
          <span class="np-grow">today {usd(b.todaySettledUsd ?? b.todayUsd)}{#if b.dailyUsd}<span class="np-dim">&nbsp;/ {usd(b.dailyUsd)}</span>{/if}</span>
          {#if b.exhausted}<span class="err">{b.onLimit === 'ask' ? 'spent · chats ask' : 'spent · paid calls stop'}</span>{/if}
        </div>
        {#if frac != null}
          <div class="np-progress" style="--value: {frac}" data-tone={b.exhausted ? 'err' : b.warning ? 'warn' : undefined}></div>
        {/if}
      </div>
    {/if}

    {#if offAgents.length}
      <Section title="Off" count={String(offAgents.length)} collapsible open={false} storageKey="work.v2.off">
        {#each offAgents as pool (pool.key)}
          <AgentPool {pool} {agentById} {titles} {ctx} cmds={cmdsBySession} />
        {/each}
      </Section>
    {/if}
    <!-- ---------------------------------------------------------------- agents: who works on what, what is free, what each has done -->
    <div class="work-agents">
      {#if !slots}
        <div class="na">Agents not available{errors.agents ? ` — ${errors.agents}` : ''}</div>
      {:else}
        {#each shown as pool (pool.key)}
          <AgentPool {pool} {agentById} {titles} {ctx} cmds={cmdsBySession} />
        {:else}
          {#if !offAgents.length}<div class="na">
            No agents set up: chats run on their model.
            {#if ctx.app.openSettings}<button class="link" onclick={() => ctx.app.openSettings('agents')}>Set up agents</button>{/if}
          </div>{/if}
        {/each}
      {/if}
      {#each waitingChats as w (w.parent.id)}
        <button
          class="waiting-chat np-line"
          title="Its turn ended while its subagents work; their reports start its next run: {w.kids.map((k) => k.name || k.id).join(', ')}"
          onclick={() => ctx.app.openSession?.(w.parent.sessionId)}
        >
          <span class="np-dot" data-status="waiting"></span>
          <span class="wtitle np-ellipsis">{titles.get(w.parent.sessionId) ?? w.parent.name}</span>
          <span class="wcount">waiting on {w.kids.length} subagent{w.kids.length === 1 ? '' : 's'}</span>
        </button>
      {/each}
      {#if errors.runs}<div class="na">Runs not available — {errors.runs}</div>{/if}
      {#if errors.usage}<div class="na">Usage not available — {errors.usage}</div>{/if}
    </div>

    {#if setUp.length}
      <!-- runs that may go to any of several agents wait here, in nobody's own queue -->
      <Section title="Queued" count={unassigned.length ? `${unassigned.length} for any agent` : null} collapsible open storageKey="work.v2.queued">
        {#if unassigned.length}
          <WaitList waiters={unassigned} label={unassignedLabel} {ctx} what="for any agent" />
        {:else}
          <div class="na">Nothing waiting for any agent</div>
        {/if}
      </Section>
    {/if}

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

    <!-- ---------------------------------------------------------------- finished commands (a finished run is a chat: the session list has it) -->
    <Section title="Finished" count={finishedProcs.length ? `${finishedProcs.length} commands` : null} collapsible open={false} storageKey="work.v2.finished">
      {#if !processes}
        <div class="na">Nothing available{errors.processes ? ` — ${errors.processes}` : ''}</div>
      {:else if !finishedProcs.length}
        <div class="na">No command has finished yet</div>
      {:else}
        {#each showAllProcs ? finishedProcs : finishedProcs.slice(0, 6) as p (p.id)}
          <ProcessRow proc={p} {ctx} />
        {/each}
        {#if finishedProcs.length > 6}
          <button class="more" onclick={() => (showAllProcs = !showAllProcs)}>
            {showAllProcs ? 'Show less' : `Show all ${finishedProcs.length}`}
          </button>
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
  .work-agents {
    padding: 4px 12px 6px;
    border-bottom: 1px solid var(--border);
  }
  .waiting-chat {
    display: flex;
    align-items: center;
    gap: 6px;
    width: 100%;
    min-width: 0;
    height: 24px;
    padding: 0 2px;
    border: 0;
    background: transparent;
    color: var(--fg-muted);
    font: inherit;
    font-size: var(--fs-sm);
    text-align: left;
    cursor: pointer;
  }
  .waiting-chat:hover {
    color: var(--fg);
  }
  .wtitle {
    flex: 1;
    min-width: 0;
  }
  .wcount {
    flex: none;
    color: var(--info);
    font-size: var(--fs-xs);
  }
  .nobody {
    padding: 0 4px;
    font-size: var(--fs-xs);
    color: var(--fg-dim);
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
